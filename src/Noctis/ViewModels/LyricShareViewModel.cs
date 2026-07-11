using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>A solid background-color choice on the share card. An empty
/// <paramref name="Hex"/> with <paramref name="IsAuto"/> means "derive from artwork".</summary>
public record ShareSolidSwatch(string Hex, string Name, IBrush Preview, bool IsAuto = false);

/// <summary>A lyric line the user can include on (and edit for) the share card.</summary>
public partial class SelectableLyricLine : ObservableObject
{
    public SelectableLyricLine(string text, TimeSpan? timestamp = null,
        IReadOnlyList<WordTiming>? words = null, TimeSpan? endTimestamp = null)
    {
        _text = text;
        Timestamp = timestamp;
        Words = words;
        EndTimestamp = endTimestamp;
    }

    /// <summary>Editable line text — changes re-render the card preview.</summary>
    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True while this line is the one currently playing (drives the sync highlight).</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Playback timestamp for this line, when the source lyrics were synced.</summary>
    public TimeSpan? Timestamp { get; }

    /// <summary>Per-word (ELRC) timing when the source line has it; drives the karaoke clip.</summary>
    public IReadOnlyList<WordTiming>? Words { get; }

    /// <summary>Line end time (Lyricsfile) — bounds the last word's sweep.</summary>
    public TimeSpan? EndTimestamp { get; }
}

/// <summary>
/// Drives the lyric share-card dialog: line selection/editing, format toggle,
/// text-color choice, live preview, playback sync, and PNG export.
/// </summary>
public partial class LyricShareViewModel : ViewModelBase
{
    /// <summary>Spotify caps at ~5; we allow a little more before the card gets cramped.</summary>
    public const int MaxLines = 8;

    private readonly Track _track;
    private readonly PlayerViewModel? _player;
    private readonly string _autoColorHex;
    private int _renderGeneration;
    private int _currentSyncIndex = -1;
    private bool _syncUpdatingSelection;

    /// <summary>How many lines the card shows while following playback.</summary>
    private const int SyncWindow = 4;

    /// <summary>Raised with a line index when the synced view should scroll it into view.</summary>
    public event Action<int>? ScrollToLineRequested;

    public ObservableCollection<SelectableLyricLine> Lines { get; } = new();

    [ObservableProperty] private bool _isSquare = true;
    [ObservableProperty] private bool _isStory;

    // ── Layout: Card (panel header) or Poster (big centered artwork) ────
    [ObservableProperty] private bool _isPanelLayout = true;
    [ObservableProperty] private bool _isPosterLayout;

    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>True while an MP4 clip is being rendered (disables the export buttons).</summary>
    [ObservableProperty] private bool _isRendering;

    [ObservableProperty] private ShareTextColor _textColor = ShareTextColor.Auto;

    // ── Background color ────────────────────────────────────────────────
    // Default to the Spotify-style full-bleed solid (the artwork-derived "Auto" color).
    [ObservableProperty] private bool _isArtworkBg;
    [ObservableProperty] private bool _isSolidBg = true;
    [ObservableProperty] private string _solidColorHex = "#1A1A2E";

    /// <summary>Solid-color choices for the Background flyout: "Auto" (artwork-derived,
    /// per track) first, then the curated colors.</summary>
    public IReadOnlyList<ShareSolidSwatch> SolidSwatches { get; }

    /// <summary>True when the Solid background is the artwork-derived "Auto" color (drives the swatch ring).</summary>
    [ObservableProperty] private bool _isAutoSolid = true;

    private static IReadOnlyList<ShareSolidSwatch> BuildSolidSwatches(string autoColorHex)
    {
        (string Hex, string Name)[] swatches =
        {
            ("#1A1A2E", "Midnight"), ("#040404", "Black"),   ("#2D1B36", "Plum"),
            ("#0D2137", "Navy"),     ("#1B2D2A", "Pine"),    ("#2C1810", "Espresso"),
            ("#7C7C7C", "Gray"),     ("#6B8E9B", "Slate"),   ("#5C8A6E", "Sage"),
            ("#9B7CB8", "Lavender"), ("#B35A5A", "Brick"),   ("#C9B458", "Gold"),
            ("#ABC1D8", "Sky"),      ("#F7C8B1", "Peach"),   ("#E4ECF4", "Mist"),
            ("#B4E4AC", "Mint"),     ("#D4B8E0", "Lilac"),   ("#F5E6CC", "Cream"),
        };
        var list = new List<ShareSolidSwatch>(swatches.Length + 1)
        {
            new(string.Empty, "Auto — from artwork",
                new SolidColorBrush(Color.Parse(autoColorHex)), IsAuto: true),
        };
        list.AddRange(swatches.Select(s =>
            new ShareSolidSwatch(s.Hex, s.Name, new SolidColorBrush(Color.Parse(s.Hex)))));
        return list;
    }

    /// <summary>Whether the source lyrics carry timestamps (sync toggle is meaningful).</summary>
    public bool SyncAvailable { get; }

    /// <summary>Frame rate of the karaoke clip's frame sequence — 60 so the word sweep
    /// is as fluid in the exported video as it is on the lyrics page.</summary>
    private const int KaraokeFps = 60;

    /// <summary>True when any selected line carries word-level (ELRC) timing.</summary>
    public bool KaraokeAvailable =>
        Lines.Any(l => l.IsSelected && l.Words is { Count: > 0 });

    /// <summary>When on (and available), Save Video renders the word-sweep karaoke clip.</summary>
    [ObservableProperty] private bool _karaokeEnabled = true;

    partial void OnKaraokeEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        RefreshPreview();   // starts/stops the live karaoke preview
    }

    // ── Live karaoke preview ─────────────────────────────────────────────
    // A half-resolution animator repaints the card's word sweep ~60×/s into a
    // WriteableBitmap shown over the static preview, following playback — so what
    // you see is exactly what Save Video exports.

    /// <summary>Preview renders at half card resolution — crisp at dialog size, cheap per frame.</summary>
    private const float PreviewAnimatorScale = 0.5f;

    /// <summary>Same small lead as the lyrics page so the sweep matches the vocal.</summary>
    private const double PreviewWordLookaheadSeconds = 0.08;

    /// <summary>Live word-sweep frame shown over the static preview; null when not animating.</summary>
    [ObservableProperty] private Bitmap? _animatedPreview;

    /// <summary>Raised after each animated frame so the view can invalidate the Image
    /// (in-place WriteableBitmap updates don't notify the binding).</summary>
    public event Action? AnimatedFrameRendered;

    private ShareCardRenderer.KaraokeCardAnimator? _animator;
    private WriteableBitmap? _animBitmap;
    private DispatcherTimer? _animTimer;
    private double _animLastT = double.NaN;

    // Smoothed playback clock, mirroring the lyrics page: LibVLC refreshes Position
    // only every ~150-300ms, so raw reads step; extrapolate with a Stopwatch between
    // raw updates while playing (monotonic + stall guards).
    private long _clockRawMs = -1;
    private double _clockAnchorMs;
    private long _clockAnchorTimestamp;
    private double _clockLastMs;

    /// <summary>When on, the currently-playing line is highlighted and scrolled into view.</summary>
    [ObservableProperty] private bool _syncEnabled;

    public bool IsAutoText => TextColor == ShareTextColor.Auto;
    public bool IsWhiteText => TextColor == ShareTextColor.White;
    public bool IsBlackText => TextColor == ShareTextColor.Black;

    /// <summary>Compact summary of the current options, shown on the "Card options" dropdown button.</summary>
    public string CardOptionsSummary
    {
        get
        {
            var layout = IsPosterLayout ? "Poster" : "Card";
            var aspect = IsStory ? "9:16" : "1:1";
            var text = TextColor switch
            {
                ShareTextColor.White => "White",
                ShareTextColor.Black => "Black",
                _ => "Auto",
            };
            var summary = $"{layout} · {aspect} · {text}";
            if (SyncAvailable && SyncEnabled)
                summary += " · Sync";
            if (KaraokeAvailable && KaraokeEnabled)
                summary += " · Karaoke";
            return summary;
        }
    }

    /// <summary>Compact summary of the background choice, shown on the "Background" dropdown button.</summary>
    public string BackgroundSummary
    {
        get
        {
            if (IsArtworkBg) return "Artwork";
            if (IsAutoSolid) return "Solid · Auto";
            var name = SolidSwatches.FirstOrDefault(s =>
                string.Equals(s.Hex, SolidColorHex, StringComparison.OrdinalIgnoreCase))?.Name;
            return name != null ? $"Solid · {name}" : "Solid";
        }
    }

    /// <summary>Last rendered PNG — what Save/Copy exports.</summary>
    public byte[]? CurrentPng { get; private set; }

    public string TrackTitle => _track.Title;
    public string TrackArtist => _track.ArtistDisplay;

    /// <summary>Suggested file name for the save dialog.</summary>
    public string SuggestedFileName
    {
        get
        {
            var name = $"{_track.Artist} - {_track.Title} lyrics";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name + ".png";
        }
    }

    /// <summary>Suggested file name for the clip save dialog (mirrors the PNG name).</summary>
    public string SuggestedVideoFileName => Path.ChangeExtension(SuggestedFileName, ".mp4");

    /// <summary>
    /// True when a card is rendered AND the track has a real local audio file AND we're
    /// not already rendering — i.e. the Save Video button should be enabled.
    /// </summary>
    public bool CanExportVideo =>
        !IsRendering
        && CurrentPng != null
        && !string.IsNullOrWhiteSpace(_track.FilePath)
        && File.Exists(_track.FilePath);

    // CanExportVideo has no backing field, so raise it manually when its inputs change.
    partial void OnPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(CanExportVideo));
    partial void OnIsRenderingChanged(bool value) => OnPropertyChanged(nameof(CanExportVideo));

    public LyricShareViewModel(Track track, IReadOnlyList<string> lines, int preselectIndex = 0)
        : this(track, lines, null, null, null, null, preselectIndex)
    {
    }

    public LyricShareViewModel(
        Track track,
        IReadOnlyList<string> lines,
        IReadOnlyList<TimeSpan?>? timestamps,
        PlayerViewModel? player,
        IReadOnlyList<IReadOnlyList<WordTiming>?>? wordTimings = null,
        IReadOnlyList<TimeSpan?>? endTimestamps = null,
        int preselectIndex = 0)
    {
        _track = track;
        _player = player;

        // The same vibrant color the card renderer derives, so the "Auto" swatch matches the card.
        _autoColorHex = ShareCardRenderer.GetVibrantColorHex(_track.AlbumArtworkPath);
        _solidColorHex = _autoColorHex;   // default Solid background uses the Auto color from the start
        SolidSwatches = BuildSolidSwatches(_autoColorHex);

        for (int i = 0; i < lines.Count; i++)
        {
            var ts = timestamps != null && i < timestamps.Count ? timestamps[i] : null;
            var words = wordTimings != null && i < wordTimings.Count ? wordTimings[i] : null;
            var end = endTimestamps != null && i < endTimestamps.Count ? endTimestamps[i] : null;
            Lines.Add(new SelectableLyricLine(lines[i], ts, words, end));
        }

        SyncAvailable = player != null && Lines.Any(l => l.Timestamp.HasValue);

        // Pre-select the active line and the next few, like Spotify does.
        if (Lines.Count > 0)
        {
            int start = Math.Clamp(preselectIndex, 0, Lines.Count - 1);
            for (int i = start; i < Math.Min(start + 4, Lines.Count); i++)
                Lines[i].IsSelected = true;
        }

        foreach (var line in Lines)
            line.PropertyChanged += OnLineChanged;

        if (SyncAvailable)
        {
            _syncEnabled = true;
            _player!.PropertyChanged += OnPlayerPropertyChanged;
            UpdateSyncHighlight(_player.Position);
        }

        RefreshPreview();
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SelectableLyricLine line)
            return;

        // Selection changes driven by playback sync are applied in bulk; ignore them here.
        if (_syncUpdatingSelection)
            return;

        if (e.PropertyName == nameof(SelectableLyricLine.IsSelected))
        {
            // A manual checkbox click takes over from playback — stop following.
            if (SyncEnabled)
                SyncEnabled = false;

            if (line.IsSelected && Lines.Count(l => l.IsSelected) > MaxLines)
            {
                // Revert the toggle that exceeded the cap.
                line.IsSelected = false;
                StatusText = $"Up to {MaxLines} lines";
                return;
            }

            StatusText = string.Empty;
            OnPropertyChanged(nameof(KaraokeAvailable));
            OnPropertyChanged(nameof(CardOptionsSummary));
            RefreshPreview();
        }
        else if (e.PropertyName == nameof(SelectableLyricLine.Text) && line.IsSelected)
        {
            // Editing a selected line changes what the card shows.
            RefreshPreview();
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SyncEnabled) return;
        if (e.PropertyName == nameof(PlayerViewModel.Position) && _player != null)
            UpdateSyncHighlight(_player.Position);
    }

    /// <summary>Highlights the line whose timestamp is currently playing and scrolls to it.</summary>
    private void UpdateSyncHighlight(TimeSpan position)
    {
        if (Lines.Count == 0) return;

        // Small lookahead so the highlight lands as the line begins, matching the lyrics page.
        var adjusted = position + TimeSpan.FromMilliseconds(350);
        int index = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            var ts = Lines[i].Timestamp;
            if (ts.HasValue && ts.Value <= adjusted)
                index = i;
        }

        if (index == _currentSyncIndex) return;
        _currentSyncIndex = index;

        for (int i = 0; i < Lines.Count; i++)
        {
            bool current = i == index;
            if (Lines[i].IsCurrent != current)
                Lines[i].IsCurrent = current;
        }

        if (index >= 0)
        {
            // Follow along: the card auto-shows the current line and the next few.
            ApplySyncSelection(index);
            ScrollToLineRequested?.Invoke(index);
        }
    }

    /// <summary>Selects a window of lines starting at <paramref name="index"/> without disabling sync.</summary>
    private void ApplySyncSelection(int index)
    {
        int end = Math.Min(index + SyncWindow, Lines.Count);
        _syncUpdatingSelection = true;
        for (int i = 0; i < Lines.Count; i++)
        {
            bool sel = i >= index && i < end;
            if (Lines[i].IsSelected != sel)
                Lines[i].IsSelected = sel;
        }
        _syncUpdatingSelection = false;
        OnPropertyChanged(nameof(KaraokeAvailable));
        OnPropertyChanged(nameof(CardOptionsSummary));
        RefreshPreview();
    }

    partial void OnSyncEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        if (value && _player != null)
        {
            _currentSyncIndex = -1;
            UpdateSyncHighlight(_player.Position);
        }
        else
        {
            foreach (var line in Lines)
                if (line.IsCurrent) line.IsCurrent = false;
            _currentSyncIndex = -1;
        }
    }

    partial void OnTextColorChanged(ShareTextColor value)
    {
        OnPropertyChanged(nameof(IsAutoText));
        OnPropertyChanged(nameof(IsWhiteText));
        OnPropertyChanged(nameof(IsBlackText));
        OnPropertyChanged(nameof(CardOptionsSummary));
        RefreshPreview();
    }

    partial void OnIsArtworkBgChanged(bool value)
    {
        OnPropertyChanged(nameof(BackgroundSummary));
        if (value) { IsSolidBg = false; RefreshPreview(); }
    }

    partial void OnIsSolidBgChanged(bool value)
    {
        OnPropertyChanged(nameof(BackgroundSummary));
        if (value) { IsArtworkBg = false; RefreshPreview(); }
    }

    partial void OnIsAutoSolidChanged(bool value)
        => OnPropertyChanged(nameof(BackgroundSummary));

    partial void OnSolidColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(BackgroundSummary));
        if (IsSolidBg) RefreshPreview();
    }

    partial void OnIsSquareChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        if (value) { IsStory = false; RefreshPreview(); }
    }

    partial void OnIsStoryChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        if (value) { IsSquare = false; RefreshPreview(); }
    }

    partial void OnIsPanelLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        if (value) { IsPosterLayout = false; RefreshPreview(); }
    }

    partial void OnIsPosterLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        if (value) { IsPanelLayout = false; RefreshPreview(); }
    }

    [RelayCommand]
    private void SelectSquare() => IsSquare = true;

    [RelayCommand]
    private void SelectStory() => IsStory = true;

    [RelayCommand]
    private void SelectPanelLayout() => IsPanelLayout = true;

    [RelayCommand]
    private void SelectPosterLayout() => IsPosterLayout = true;

    [RelayCommand]
    private void ToggleSync() => SyncEnabled = !SyncEnabled;

    [RelayCommand]
    private void ToggleKaraoke() => KaraokeEnabled = !KaraokeEnabled;

    [RelayCommand]
    private void UseAutoText() => TextColor = ShareTextColor.Auto;

    [RelayCommand]
    private void UseWhiteText() => TextColor = ShareTextColor.White;

    [RelayCommand]
    private void UseBlackText() => TextColor = ShareTextColor.Black;

    [RelayCommand]
    private void UseArtworkBg() => IsArtworkBg = true;

    [RelayCommand]
    private void UseSolidBg() => IsSolidBg = true;

    [RelayCommand]
    private void SetSolidColor(string? hex)
    {
        // The "Auto" swatch carries an empty hex — route it to the artwork-derived color.
        if (string.IsNullOrWhiteSpace(hex)) { SetAutoSolid(); return; }
        IsAutoSolid = false;       // a fixed swatch overrides Auto
        IsSolidBg = true;          // picking a swatch implies Solid mode
        SolidColorHex = hex;
        RefreshPreview();          // re-render even if the hex value is unchanged
    }

    [RelayCommand]
    private void SetAutoSolid()
    {
        IsAutoSolid = true;        // derive the solid color from the artwork
        IsSolidBg = true;          // Auto implies Solid mode
        SolidColorHex = _autoColorHex;
        RefreshPreview();          // re-render even if the value is unchanged
    }

    private void RefreshPreview()
    {
        var generation = ++_renderGeneration;
        var selectedLines = Lines.Where(l => l.IsSelected && !string.IsNullOrWhiteSpace(l.Text)).ToList();
        var selected = selectedLines.Select(l => l.Text).ToList();
        if (selected.Count == 0)
        {
            CurrentPng = null;
            Preview = null;
            TeardownAnimator();
            return;
        }

        var spec = BuildSpec(selected);
        bool animate = KaraokeEnabled && _player != null
            && selectedLines.Any(l => l.Words is { Count: > 0 });
        var karaoke = animate ? BuildKaraokeLines(selectedLines) : null;

        Task.Run(() =>
        {
            try
            {
                var png = ShareCardRenderer.RenderLyricCardStyled(spec, ShareCardRenderer.CardExportScale);
                var animator = karaoke != null
                    ? new ShareCardRenderer.KaraokeCardAnimator(spec, karaoke, PreviewAnimatorScale)
                    : null;
                using var ms = new MemoryStream(png);
                var bitmap = new Bitmap(ms);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _renderGeneration)
                    {
                        bitmap.Dispose();
                        animator?.Dispose();
                        return;
                    }
                    var old = Preview;
                    CurrentPng = png;
                    Preview = bitmap;
                    old?.Dispose();
                    SwapAnimator(animator);
                });
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                    "Share card render failed", ex.Message);
            }
        });
    }

    /// <summary>Installs the freshly built animator (or tears everything down when null),
    /// reusing the WriteableBitmap when the pixel size is unchanged. UI thread only.</summary>
    private void SwapAnimator(ShareCardRenderer.KaraokeCardAnimator? animator)
    {
        if (animator == null)
        {
            TeardownAnimator();
            return;
        }

        _animator?.Dispose();
        _animator = animator;
        _animLastT = double.NaN;

        if (_animBitmap == null
            || _animBitmap.PixelSize.Width != animator.PixelWidth
            || _animBitmap.PixelSize.Height != animator.PixelHeight)
        {
            var oldBmp = _animBitmap;
            _animBitmap = new WriteableBitmap(
                new PixelSize(animator.PixelWidth, animator.PixelHeight),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            AnimatedPreview = _animBitmap;
            oldBmp?.Dispose();
        }

        RenderAnimatedFrame();   // first frame immediately, even when paused

        if (_animTimer == null)
        {
            // ~60 Hz: matches typical display refresh so the sweep never visibly steps.
            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _animTimer.Tick += (_, _) => OnAnimTick();
        }
        _animTimer.Start();
    }

    private void TeardownAnimator()
    {
        _animTimer?.Stop();
        _animator?.Dispose();
        _animator = null;
        var old = AnimatedPreview;
        AnimatedPreview = null;
        old?.Dispose();
        _animBitmap = null;
        _animLastT = double.NaN;
    }

    private void OnAnimTick()
    {
        if (_animator == null)
        {
            _animTimer?.Stop();
            return;
        }
        double t = GetSmoothedPositionSeconds() + PreviewWordLookaheadSeconds;
        // Paused (or between coarse position updates while paused) → nothing moved.
        if (!double.IsNaN(_animLastT) && Math.Abs(t - _animLastT) < 0.0005)
            return;
        RenderAnimatedFrameAt(t);
    }

    private void RenderAnimatedFrame()
        => RenderAnimatedFrameAt(GetSmoothedPositionSeconds() + PreviewWordLookaheadSeconds);

    private void RenderAnimatedFrameAt(double t)
    {
        if (_animator == null || _animBitmap == null) return;
        _animLastT = t;
        using (var fb = _animBitmap.Lock())
            _animator.RenderFrame(t, fb.Address, fb.RowBytes);
        AnimatedFrameRendered?.Invoke();
    }

    /// <summary>Extrapolated playback position in seconds (see clock fields above).</summary>
    private double GetSmoothedPositionSeconds()
    {
        if (_player == null) return 0;
        var raw = _player.Position;
        if (_player.State != PlaybackState.Playing)
        {
            // Not advancing — drop the anchor so resume re-anchors fresh.
            _clockRawMs = -1;
            _clockLastMs = raw.TotalMilliseconds;
            return raw.TotalSeconds;
        }

        var rawMs = (long)raw.TotalMilliseconds;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (rawMs != _clockRawMs)
        {
            _clockRawMs = rawMs;
            _clockAnchorMs = rawMs;
            _clockAnchorTimestamp = now;
        }

        var elapsedMs = (now - _clockAnchorTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs > 1000) elapsedMs = 1000;   // stall guard: don't run away from a buffering player
        var estimate = _clockAnchorMs + elapsedMs;
        if (estimate < _clockLastMs && _clockLastMs - estimate < 300)
            estimate = _clockLastMs;               // monotonic guard: hold tiny backwards re-anchors
        _clockLastMs = estimate;
        return estimate / 1000.0;
    }

    public void ReportStatus(string message) => StatusText = message;

    /// <summary>The card spec for the given lyric lines under the current options —
    /// shared by the live preview and the karaoke frame renderer so they always match.</summary>
    private LyricCardSpec BuildSpec(IReadOnlyList<string> lines) => new()
    {
        Title = _track.Title,
        Artist = _track.ArtistDisplay,
        ArtworkPath = _track.AlbumArtworkPath,
        Lines = lines,
        Format = IsStory ? ShareCardFormat.Story : ShareCardFormat.Square,
        TextColor = TextColor,
        IsExplicit = _track.IsExplicit,
        Background = IsSolidBg ? ShareBackground.Solid : ShareBackground.Artwork,
        SolidColorHex = SolidColorHex,
        Layout = IsPosterLayout ? ShareCardLayout.Poster : ShareCardLayout.Panel,
    };

    /// <summary>
    /// Karaoke timing parallel to the card's lines: sanitized word tokens with resolved
    /// end times (explicit end → next word → line end → next line start → +2 s).
    /// Lines without word data get StartSeconds only (line-level highlight).
    /// </summary>
    private static IReadOnlyList<KaraokeLine> BuildKaraokeLines(IReadOnlyList<SelectableLyricLine> selected)
    {
        var result = new List<KaraokeLine>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            var line = selected[i];
            double? nextLineStart = i + 1 < selected.Count ? selected[i + 1].Timestamp?.TotalSeconds : null;
            if (line.Words is not { Count: > 0 } words)
            {
                result.Add(new KaraokeLine { StartSeconds = line.Timestamp?.TotalSeconds });
                continue;
            }

            var karaokeWords = new List<KaraokeWord>(words.Count);
            for (int k = 0; k < words.Count; k++)
            {
                var token = ShareCardRenderer.SanitizeForRender(words[k].Text);
                if (token.Length == 0)
                    continue;   // pure-whitespace word carries no rendered token
                double start = words[k].Start.TotalSeconds;
                double end = words[k].End?.TotalSeconds
                    ?? (k + 1 < words.Count ? words[k + 1].Start.TotalSeconds
                        : line.EndTimestamp?.TotalSeconds ?? nextLineStart ?? start + 2);
                karaokeWords.Add(new KaraokeWord(token, start, end));
            }
            result.Add(new KaraokeLine
            {
                StartSeconds = line.Timestamp?.TotalSeconds,
                Words = karaokeWords,
            });
        }
        return result;
    }

    /// <summary>Derives the clip's audio window from the selected lines and playback position.</summary>
    public ShareClipTiming GetClipTiming()
    {
        var lines = Lines.Select(l => new ShareClipLine(l.Timestamp, l.IsSelected)).ToList();
        double? position = _player?.Position.TotalSeconds;
        return ShareClipTiming.Compute(lines, position);
    }

    /// <summary>
    /// Renders the current card + matching audio slice to an MP4 at <paramref name="outputPath"/>.
    /// Returns a status string for the dialog (never throws). ffmpeg is resolved from the
    /// app's audio-converter service; absence is reported, not fatal.
    /// </summary>
    public async Task<string> ExportClipAsync(string outputPath)
    {
        if (CurrentPng is null)
            return "Nothing to export";
        if (string.IsNullOrWhiteSpace(_track.FilePath) || !File.Exists(_track.FilePath))
            return "No audio file for this track";

        var ffmpeg = App.Services?.GetService<IAudioConverterService>()?.GetFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg))
            return "ffmpeg not found — set its path in Settings";

        IsRendering = true;
        StatusText = string.Empty;   // the footer spinner signals progress; text only for the frame pass
        try
        {
            // Same line filter as RefreshPreview so the exported card, spec.Lines and
            // karaoke stay parallel to what the preview shows.
            var selected = Lines.Where(l => l.IsSelected && !string.IsNullOrWhiteSpace(l.Text)).ToList();
            var spec = BuildSpec(selected.Select(l => l.Text).ToList());
            var timing = GetClipTiming();

            if (!(KaraokeEnabled && KaraokeAvailable))
            {
                // Base-resolution still: the clip is 1080p-bound, so reusing the 2×
                // supersampled CurrentPng would only quadruple the encode cost.
                var still = await Task.Run(() => ShareCardRenderer.RenderLyricCardStyled(spec));
                var (ok, error) = await ShareClipRenderer.RenderAsync(ffmpeg, still, _track.FilePath, outputPath, timing);
                return ok ? "Saved" : $"Clip failed: {error}";
            }

            // Karaoke path: per-frame word sweep → JPEG sequence → ffmpeg mux.
            var karaoke = BuildKaraokeLines(selected);

            var frameDir = Path.Combine(Path.GetTempPath(), $"noctis-karaoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(frameDir);
            try
            {
                StatusText = "Saving";
                await Task.Run(() => ShareCardRenderer.RenderKaraokeFrames(
                    spec, karaoke, timing, KaraokeFps, frameDir));

                StatusText = string.Empty;
                var pattern = Path.Combine(frameDir, "frame-%05d.jpg");
                var (ok, error) = await ShareClipRenderer.RenderFramesAsync(
                    ffmpeg, pattern, KaraokeFps, _track.FilePath, outputPath, timing);
                return ok ? "Saved" : $"Clip failed: {error}";
            }
            finally
            {
                try { Directory.Delete(frameDir, true); } catch { /* best effort */ }
            }
        }
        finally
        {
            IsRendering = false;
        }
    }

    /// <summary>Unsubscribes from the player so the dialog can be garbage-collected.</summary>
    public void Detach()
    {
        ++_renderGeneration;   // stale in-flight preview renders dispose instead of re-installing
        TeardownAnimator();
        if (_player != null)
            _player.PropertyChanged -= OnPlayerPropertyChanged;
        foreach (var line in Lines)
            line.PropertyChanged -= OnLineChanged;
    }
}
