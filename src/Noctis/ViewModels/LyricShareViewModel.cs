using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>A solid background-color choice on the share card.</summary>
public record ShareSolidSwatch(string Hex, string Name, IBrush Preview);

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

    /// <summary>Curated solid-color choices shown when "Solid" is selected.</summary>
    public IReadOnlyList<ShareSolidSwatch> SolidSwatches { get; } = BuildSolidSwatches();

    /// <summary>Preview brush for the "Auto" solid swatch — the artwork-derived color it applies.</summary>
    public IBrush AutoSwatchBrush { get; }

    /// <summary>True when the Solid background is the artwork-derived "Auto" color (drives the swatch ring).</summary>
    [ObservableProperty] private bool _isAutoSolid = true;

    private static IReadOnlyList<ShareSolidSwatch> BuildSolidSwatches()
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
        return swatches
            .Select(s => new ShareSolidSwatch(s.Hex, s.Name, new SolidColorBrush(Color.Parse(s.Hex))))
            .ToList();
    }

    /// <summary>Whether the source lyrics carry timestamps (sync toggle is meaningful).</summary>
    public bool SyncAvailable { get; }

    /// <summary>Frame rate of the karaoke clip's frame sequence.</summary>
    private const int KaraokeFps = 24;

    /// <summary>True when any selected line carries word-level (ELRC) timing.</summary>
    public bool KaraokeAvailable =>
        Lines.Any(l => l.IsSelected && l.Words is { Count: > 0 });

    /// <summary>When on (and available), Save Video renders the word-sweep karaoke clip.</summary>
    [ObservableProperty] private bool _karaokeEnabled = true;

    partial void OnKaraokeEnabledChanged(bool value) => OnPropertyChanged(nameof(CardOptionsSummary));

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
            var aspect = IsStory ? "9:16" : "1:1";
            var text = TextColor switch
            {
                ShareTextColor.White => "White",
                ShareTextColor.Black => "Black",
                _ => "Auto",
            };
            var summary = $"{aspect} · {text}";
            if (SyncAvailable && SyncEnabled)
                summary += " · Sync";
            if (KaraokeAvailable && KaraokeEnabled)
                summary += " · Karaoke";
            return summary;
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
        AutoSwatchBrush = new SolidColorBrush(Color.Parse(_autoColorHex));

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
        if (value) { IsSolidBg = false; RefreshPreview(); }
    }

    partial void OnIsSolidBgChanged(bool value)
    {
        if (value) { IsArtworkBg = false; RefreshPreview(); }
    }

    partial void OnSolidColorHexChanged(string value)
    {
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

    [RelayCommand]
    private void SelectSquare() => IsSquare = true;

    [RelayCommand]
    private void SelectStory() => IsStory = true;

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
        if (string.IsNullOrWhiteSpace(hex)) return;
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
        var selected = Lines.Where(l => l.IsSelected)
            .Select(l => l.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (selected.Count == 0)
        {
            CurrentPng = null;
            Preview = null;
            return;
        }

        var spec = BuildSpec(selected);

        Task.Run(() =>
        {
            try
            {
                var png = ShareCardRenderer.RenderLyricCardStyled(spec);
                using var ms = new MemoryStream(png);
                var bitmap = new Bitmap(ms);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _renderGeneration)
                    {
                        bitmap.Dispose();
                        return;
                    }
                    var old = Preview;
                    CurrentPng = png;
                    Preview = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                    "Share card render failed", ex.Message);
            }
        });
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
        if (CurrentPng is not { } png)
            return "Nothing to export";
        if (string.IsNullOrWhiteSpace(_track.FilePath) || !File.Exists(_track.FilePath))
            return "No audio file for this track";

        var ffmpeg = App.Services?.GetService<IAudioConverterService>()?.GetFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg))
            return "ffmpeg not found — set its path in Settings";

        IsRendering = true;
        try
        {
            if (!(KaraokeEnabled && KaraokeAvailable))
            {
                var (ok, error) = await ShareClipRenderer.RenderAsync(ffmpeg, png, _track.FilePath, outputPath, GetClipTiming());
                return ok ? "Saved" : $"Clip failed: {error}";
            }

            // Karaoke path: per-frame word sweep → PNG sequence → ffmpeg mux.
            // Same line filter as RefreshPreview so spec.Lines and karaoke stay parallel
            // to what the card shows.
            var selected = Lines.Where(l => l.IsSelected && !string.IsNullOrWhiteSpace(l.Text)).ToList();
            var spec = BuildSpec(selected.Select(l => l.Text).ToList());
            var karaoke = BuildKaraokeLines(selected);
            var timing = GetClipTiming();

            var frameDir = Path.Combine(Path.GetTempPath(), $"noctis-karaoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(frameDir);
            try
            {
                await Task.Run(() => ShareCardRenderer.RenderKaraokeFrames(
                    spec, karaoke, timing, KaraokeFps, frameDir,
                    (done, total) => Dispatcher.UIThread.Post(() => StatusText = $"Rendering frames… {done}/{total}")));

                StatusText = "Encoding clip…";
                var pattern = Path.Combine(frameDir, "frame-%05d.png");
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
        if (_player != null)
            _player.PropertyChanged -= OnPlayerPropertyChanged;
        foreach (var line in Lines)
            line.PropertyChanged -= OnLineChanged;
    }
}
