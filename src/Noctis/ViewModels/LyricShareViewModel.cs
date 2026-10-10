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

/// <summary>One entry of the Background drop-down: the blurred artwork, the artwork-derived
/// solid ("Auto"), or a curated solid color.</summary>
public record ShareBackgroundChoice(string Name, IBrush Preview, bool IsArtwork = false, bool IsAuto = false, string Hex = "");

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

    /// <summary>True while the row shows its inline text box (double-click to edit).</summary>
    [ObservableProperty]
    private bool _isEditing;

    private string? _textBeforeEdit;

    public void BeginEdit()
    {
        if (IsEditing) return;
        _textBeforeEdit = Text;
        IsEditing = true;
    }

    /// <summary>Leaves edit mode. <paramref name="keep"/> false (Esc) restores the text; an
    /// emptied line also gets its text back rather than turning into a blank row.</summary>
    public void EndEdit(bool keep)
    {
        if (!IsEditing) return;
        if ((!keep || string.IsNullOrWhiteSpace(Text)) && _textBeforeEdit != null)
            Text = _textBeforeEdit;
        _textBeforeEdit = null;
        IsEditing = false;
    }
}

/// <summary>
/// Drives the lyric share-card dialog: line selection/editing, format toggle,
/// text-color choice, live preview, playback sync, and PNG export.
/// </summary>
public partial class LyricShareViewModel : ViewModelBase
{
    /// <summary>Spotify caps at ~5; we allow a little more before the card gets cramped.</summary>
    public const int MaxLines = 8;

    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

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

    // ── Pill drop-downs (owner 10-08: the outlined summary pills + flyouts became labelled
    // pill-field ComboBoxes). Each maps onto the same flags the commands set, so every option
    // the flyouts held is still here. ──

    /// <summary>Background drop-down: Artwork, Auto (artwork-derived solid), then the curated solids.</summary>
    public IReadOnlyList<ShareBackgroundChoice> BackgroundChoices { get; }

    private bool _backgroundUpdating;

    public ShareBackgroundChoice? SelectedBackground
    {
        get
        {
            if (IsArtworkBg) return BackgroundChoices[0];
            if (IsAutoSolid) return BackgroundChoices[1];
            return BackgroundChoices.FirstOrDefault(c => !c.IsArtwork && !c.IsAuto
                && string.Equals(c.Hex, SolidColorHex, StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            if (value is null || ReferenceEquals(value, SelectedBackground)) return;
            // Several flags change per pick; the drop-down hears about it once, at the end,
            // so it never reads (and writes back) a half-applied state.
            _backgroundUpdating = true;
            try
            {
                if (value.IsArtwork) IsArtworkBg = true;
                else if (value.IsAuto) SetAutoSolid();
                else SetSolidColor(value.Hex);
            }
            finally { _backgroundUpdating = false; }
            OnPropertyChanged(nameof(SelectedBackground));
        }
    }

    private void RaiseBackgroundChanged()
    {
        OnPropertyChanged(nameof(BackgroundSummary));
        if (!_backgroundUpdating) OnPropertyChanged(nameof(SelectedBackground));
    }

    /// <summary>0 = Card (panel header), 1 = Poster.</summary>
    public int LayoutIndex
    {
        get => IsPosterLayout ? 1 : 0;
        set { if (value == 1) IsPosterLayout = true; else if (value == 0) IsPanelLayout = true; }
    }

    /// <summary>0 = 1:1, 1 = 9:16.</summary>
    public int FormatIndex
    {
        get => IsStory ? 1 : 0;
        set { if (value == 1) IsStory = true; else if (value == 0) IsSquare = true; }
    }

    /// <summary>0 = Auto, 1 = White, 2 = Black.</summary>
    public int TextColorIndex
    {
        get => TextColor switch { ShareTextColor.White => 1, ShareTextColor.Black => 2, _ => 0 };
        set
        {
            if (value is < 0 or > 2) return;
            TextColor = value switch { 1 => ShareTextColor.White, 2 => ShareTextColor.Black, _ => ShareTextColor.Auto };
        }
    }

    // ── Selection summary ──

    public int SelectedCount => Lines.Count(l => l.IsSelected);

    /// <summary>At least one picked line has text: what the card, Copy and the saves need.</summary>
    public bool HasSelection => Lines.Any(IsOnCard);

    private static bool IsOnCard(SelectableLyricLine l) => l.IsSelected && !string.IsNullOrWhiteSpace(l.Text);

    /// <summary>The lines the card shows, in lyric order whatever order they were picked in.
    /// One filter for the preview, Save/Copy and the clip, so the three always agree.</summary>
    private List<SelectableLyricLine> CardSelection() => Lines.Where(IsOnCard).ToList();

    /// <summary>The card's lyric text (tests).</summary>
    internal IReadOnlyList<string> CardLines => CardSelection().Select(l => l.Text).ToList();

    /// <summary>"3 of 8" next to Clear.</summary>
    public string SelectionSummary => L("ShareLyrics.SelectedCount", SelectedCount, MaxLines);

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanExportVideo));
    }

    /// <summary>A tap on a row: picks or drops the line (the 8-line cap still applies).</summary>
    public void ToggleLine(SelectableLyricLine line) => line.IsSelected = !line.IsSelected;

    /// <summary>Starts editing one line (any other open edit is kept first).</summary>
    public void BeginEdit(SelectableLyricLine line)
    {
        foreach (var other in Lines)
            if (!ReferenceEquals(other, line)) other.EndEdit(keep: true);
        // An edited line is one meant for the card.
        if (!line.IsSelected) line.IsSelected = true;
        line.BeginEdit();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        // A manual change takes over from playback, as a single tap does.
        if (SyncEnabled) SyncEnabled = false;
        _syncUpdatingSelection = true;
        foreach (var line in Lines)
            if (line.IsSelected) line.IsSelected = false;
        _syncUpdatingSelection = false;
        StatusText = string.Empty;
        RaiseSelectionChanged();
        OnPropertyChanged(nameof(KaraokeAvailable));
        OnPropertyChanged(nameof(CardOptionsSummary));
        RefreshPreview();
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

    /// <summary>Skia scale for the on-screen preview render (export uses CardExportScale).</summary>
    private const float PreviewRenderScale = 1f;

    /// <summary>Decode width for the preview bitmap — it fills a ~320px box.</summary>
    private const int PreviewDecodeWidth = 480;

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

    /// <summary>The header's second line: "TUTU · 6ix9ine" (just the title without an artist).</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(TrackArtist) ? TrackTitle : $"{TrackTitle} · {TrackArtist}";

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
    /// True when lines are picked AND the track has a real local audio file AND we're
    /// not already rendering — i.e. the Save Video button should be enabled. (It used to
    /// wait for the preview render too; the clip renders its own card, so the button no
    /// longer lags the selection by the preview's debounce.)
    /// </summary>
    public bool CanExportVideo =>
        !IsRendering
        && HasSelection
        && !string.IsNullOrWhiteSpace(_track.FilePath)
        && File.Exists(_track.FilePath);

    // CanExportVideo has no backing field, so raise it manually when its inputs change.
    partial void OnPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(CanExportVideo));
    partial void OnIsRenderingChanged(bool value) => OnPropertyChanged(nameof(CanExportVideo));

    /// <summary>
    /// A lyric line as one line. Multi-line LRC lines (GitHub #116) carry their breaks, and the
    /// list printed them, so lines broke at the lyrics page's wrap points into a ragged
    /// column of 2-4 words (owner 10-08; live check 10-09: "(Great John on the\nbeat, by the
    /// way)"). The card wraps by spaces only, so it measured "the\nbeat," as one word.
    /// </summary>
    internal static string OneLine(string text) =>
        text.IndexOfAny(new[] { '\r', '\n' }) < 0
            ? text
            : string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim()).Where(part => part.Length > 0));

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
        var auto = Color.Parse(_autoColorHex);
        BackgroundChoices = new[]
        {
            new ShareBackgroundChoice(L("LyricShare.Artwork"), new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(auto, 0), new GradientStop(Color.Parse("#141414"), 1) },
            }, IsArtwork: true),
            new ShareBackgroundChoice(L("LyricShare.Auto"), new SolidColorBrush(auto), IsAuto: true),
        }.Concat(SolidSwatches.Where(s => !s.IsAuto).Select(s => new ShareBackgroundChoice(s.Name, s.Preview, Hex: s.Hex)))
         .ToList();

        for (int i = 0; i < lines.Count; i++)
        {
            var ts = timestamps != null && i < timestamps.Count ? timestamps[i] : null;
            var words = wordTimings != null && i < wordTimings.Count ? wordTimings[i] : null;
            var end = endTimestamps != null && i < endTimestamps.Count ? endTimestamps[i] : null;
            Lines.Add(new SelectableLyricLine(OneLine(lines[i]), ts, words, end));
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
                StatusText = L("ShareLyrics.UpToLines", MaxLines);
                return;
            }

            StatusText = string.Empty;
            RaiseSelectionChanged();
            OnPropertyChanged(nameof(KaraokeAvailable));
            OnPropertyChanged(nameof(CardOptionsSummary));
            RefreshPreview();
        }
        else if (e.PropertyName == nameof(SelectableLyricLine.Text) && line.IsSelected)
        {
            // Editing a selected line changes what the card shows.
            RaiseSelectionChanged();
            RefreshPreview();
        }
    }

    /// <summary>
    /// Whether the player is on this dialog's song. Sync, the live karaoke sweep and the
    /// clip's start read the player's clock, which belongs to whatever plays now: after the
    /// song changed, the next song's position re-picked this song's lines.
    /// </summary>
    private bool IsOurTrackPlaying
    {
        get
        {
            if (_player?.CurrentTrack is not { } now) return false;
            if (ReferenceEquals(now, _track)) return true;
            if (now.Id != Guid.Empty && now.Id == _track.Id) return true;
            return !string.IsNullOrEmpty(now.FilePath)
                   && string.Equals(now.FilePath, _track.FilePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SyncEnabled || _player == null) return;
        if (e.PropertyName is nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.CurrentTrack))
            UpdateSyncHighlight(_player.Position);
    }

    /// <summary>Highlights the line whose timestamp is currently playing and scrolls to it.</summary>
    private void UpdateSyncHighlight(TimeSpan position)
    {
        if (Lines.Count == 0) return;
        if (!IsOurTrackPlaying)
        {
            // Another song (or nothing) plays: keep the picked lines, drop the highlight.
            foreach (var line in Lines)
                if (line.IsCurrent) line.IsCurrent = false;
            _currentSyncIndex = -1;
            return;
        }

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
        RaiseSelectionChanged();
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
        OnPropertyChanged(nameof(TextColorIndex));
        OnPropertyChanged(nameof(CardOptionsSummary));
        RefreshPreview();
    }

    partial void OnIsArtworkBgChanged(bool value)
    {
        RaiseBackgroundChanged();
        if (value) { IsSolidBg = false; RefreshPreview(); }
    }

    partial void OnIsSolidBgChanged(bool value)
    {
        RaiseBackgroundChanged();
        if (value) { IsArtworkBg = false; RefreshPreview(); }
    }

    partial void OnIsAutoSolidChanged(bool value) => RaiseBackgroundChanged();

    partial void OnSolidColorHexChanged(string value)
    {
        RaiseBackgroundChanged();
        if (IsSolidBg) RefreshPreview();
    }

    partial void OnIsSquareChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(FormatIndex));
        if (value) { IsStory = false; RefreshPreview(); }
    }

    partial void OnIsStoryChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(FormatIndex));
        if (value) { IsSquare = false; RefreshPreview(); }
    }

    partial void OnIsPanelLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(LayoutIndex));
        if (value) { IsPosterLayout = false; RefreshPreview(); }
    }

    partial void OnIsPosterLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(LayoutIndex));
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

    // Trailing-edge debounce for the preview.
    //
    // The editable lyric line is a TwoWay TextBox binding with no UpdateSourceTrigger, so
    // Avalonia pushes to the source on every keystroke — and each push started a fresh
    // Task.Run rendering a 2160x2160 (or 2160x3840) Skia surface, PNG-encoded at quality
    // 100 and re-decoded. Nothing serialized them: typing ten characters kicked off ten
    // concurrent ~33 MB-surface renders and threw away nine.
    private const int PreviewDebounceMs = 200;
    private CancellationTokenSource? _previewDebounceCts;

    private void RefreshPreview()
    {
        _previewDebounceCts?.Cancel();
        _previewDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewDebounceCts = cts;

        _ = DebouncedRefreshAsync(cts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(PreviewDebounceMs, token);
            if (token.IsCancellationRequested) return;
            RefreshPreviewCore();
        }
        catch (OperationCanceledException) { /* superseded by a newer edit */ }
    }

    private void RefreshPreviewCore()
    {
        var generation = ++_renderGeneration;
        var selectedLines = CardSelection();
        var selected = selectedLines.Select(l => l.Text).ToList();
        if (selected.Count == 0)
        {
            CurrentPng = null;
            var old = Preview;
            Preview = null;
            // Deferred, as below: the last frame may still reference it.
            if (old != null) Dispatcher.UIThread.Post(old.Dispose);
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
                // Render the on-screen preview at 1x. It is displayed in a ~320x480 box,
                // but was rendered at CardExportScale and the decoded 2160x2160 (18.7 MB)
                // or 2160x3840 (33.2 MB) bitmap was then held for the dialog's lifetime.
                // The export path (Save/Copy) still renders at full scale on demand.
                var png = ShareCardRenderer.RenderLyricCardStyled(spec, PreviewRenderScale);
                var animator = karaoke != null
                    ? new ShareCardRenderer.KaraokeCardAnimator(spec, karaoke, PreviewAnimatorScale)
                    : null;
                using var ms = new MemoryStream(png);
                var bitmap = Bitmap.DecodeToWidth(ms, PreviewDecodeWidth);
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
                    // Deferred: the previous bitmap can still be referenced by the last
                    // committed render frame in this same UI turn. AnimatedCoverImage
                    // defers every bitmap disposal for exactly this reason.
                    if (old != null) Dispatcher.UIThread.Post(old.Dispose);
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
            // 30 Hz, not 60. Each tick re-renders the whole Skia card and copies the
            // surface back (1.17 MB Square / 2.07 MB Story) on the UI thread, and
            // DrawKaraokeRows re-measures every row through SplitFallbackRuns — which
            // probes ContainsGlyph once per character, natively. At 16ms that was
            // 60–120k glyph probes a second competing with layout and input while
            // playback ran. The sweep is smooth at 30fps and the cost halves.
            // 30 ms, not 33: Win32 DispatcherTimers fire on the 15.6 ms USER-timer grid and round
            // UP — 33 ms measured 21.3 ticks/s (47 ms), 30 ms measured 31.7 ticks/s (31.6 ms).
            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
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
        // Another song's clock says nothing about this card: hold the last frame's time.
        if (!IsOurTrackPlaying) return _clockLastMs / 1000.0;
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
        double? position = IsOurTrackPlaying ? _player?.Position.TotalSeconds : null;
        return ShareClipTiming.Compute(lines, position);
    }

    /// <summary>
    /// Renders the card at full export resolution, on demand.
    ///
    /// <see cref="CurrentPng"/> is now the preview-resolution image (it fills a ~320px
    /// box and used to be held at up to 33 MB for the dialog's lifetime), so the Save and
    /// Copy paths must re-render rather than reuse it.
    /// </summary>
    public async Task<byte[]?> RenderExportPngAsync()
    {
        var selected = CardSelection().Select(l => l.Text).ToList();
        if (selected.Count == 0) return null;

        var spec = BuildSpec(selected);
        try
        {
            return await Task.Run(() =>
                ShareCardRenderer.RenderLyricCardStyled(spec, ShareCardRenderer.CardExportScale));
        }
        catch (Exception ex)
        {
            DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                "Share card export render failed", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Renders the current card + matching audio slice to an MP4 at <paramref name="outputPath"/>.
    /// Returns a status string for the dialog (never throws). ffmpeg is resolved from the
    /// app's audio-converter service; absence is reported, not fatal.
    ///
    /// Both paths (still card, karaoke frames) run under one cancellation source, so Esc and
    /// closing the dialog (<see cref="CancelExport"/>, <see cref="Detach"/>) stop either. The
    /// still path used to get no token at all: closing mid-export left ffmpeg writing the clip
    /// behind a closed dialog. A clip this run created and did not finish (cancelled or
    /// failed) is deleted; a file the user chose to replace in the picker is not ours to remove.
    /// </summary>
    public async Task<string> ExportClipAsync(string outputPath)
    {
        if (!HasSelection)
            return L("ShareLyrics.NothingToExport");
        if (string.IsNullOrWhiteSpace(_track.FilePath) || !File.Exists(_track.FilePath))
            return L("ShareLyrics.NoAudioFile");

        var ffmpeg = App.Services?.GetService<IAudioConverterService>()?.GetFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg))
            return L("ShareLyrics.NoFfmpeg");

        _exportCts?.Dispose();
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        var token = cts.Token;
        bool existedBefore = File.Exists(outputPath);
        bool ok = false;

        IsRendering = true;
        StatusText = string.Empty;   // the footer spinner signals progress; text only for the frame pass
        try
        {
            // Same line filter as RefreshPreview so the exported card, spec.Lines and
            // karaoke stay parallel to what the preview shows.
            var selected = CardSelection();
            var spec = BuildSpec(selected.Select(l => l.Text).ToList());
            var timing = GetClipTiming();
            string error;

            if (!(KaraokeEnabled && KaraokeAvailable))
            {
                // Base-resolution still: the clip is 1080p-bound, so reusing the 2×
                // supersampled CurrentPng would only quadruple the encode cost.
                var still = await Task.Run(() => ShareCardRenderer.RenderLyricCardStyled(spec), token);
                (ok, error) = await ShareClipRenderer.RenderAsync(ffmpeg, still, _track.FilePath, outputPath, timing, token);
            }
            else
            {
                // Karaoke path: per-frame word sweep → JPEG sequence → ffmpeg mux.
                var karaoke = BuildKaraokeLines(selected);
                var frameDir = Path.Combine(Path.GetTempPath(), $"noctis-karaoke-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);
                try
                {
                    // Progress: a 60s Story clip is 3600 frames at 1080x1920 — minutes of
                    // work, so the footer counts it. Reports still queued when the run ends
                    // (cancel, done) must not overwrite the final status.
                    StatusText = L("ShareLyrics.Saving", 0);
                    var progress = new Progress<(int Done, int Total)>(p =>
                    {
                        if (p.Total <= 0) return;
                        var pct = (int)Math.Round(100.0 * p.Done / p.Total);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (IsRendering && !token.IsCancellationRequested && ReferenceEquals(_exportCts, cts))
                                StatusText = L("ShareLyrics.Saving", pct);
                        });
                    });

                    await Task.Run(() => ShareCardRenderer.RenderKaraokeFrames(
                        spec, karaoke, timing, KaraokeFps, frameDir,
                        (done, total) => ((IProgress<(int, int)>)progress).Report((done, total)),
                        token), token);

                    StatusText = L("ShareLyrics.Encoding");
                    var pattern = Path.Combine(frameDir, "frame-%05d.jpg");
                    (ok, error) = await ShareClipRenderer.RenderFramesAsync(
                        ffmpeg, pattern, KaraokeFps, _track.FilePath, outputPath, timing, token);
                }
                finally
                {
                    try { Directory.Delete(frameDir, true); } catch { /* best effort */ }
                }
            }

            // ShareClipRenderer turns a killed ffmpeg into (false, "cancelled"): that is a
            // cancel, not a failure.
            if (token.IsCancellationRequested)
            {
                ok = false;
                return L("ShareLyrics.Cancelled");
            }
            return ok ? L("ShareLyrics.Saved") : L("ShareLyrics.ClipFailed", error);
        }
        catch (OperationCanceledException)
        {
            return L("ShareLyrics.Cancelled");
        }
        catch (Exception ex)
        {
            DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error, "Share clip export failed", ex.Message);
            return L("ShareLyrics.ClipFailed", ex.Message);
        }
        finally
        {
            if (!ok && !existedBefore)
                await DeletePartialAsync(outputPath);
            if (ReferenceEquals(_exportCts, cts)) _exportCts = null;
            cts.Dispose();
            IsRendering = false;
        }
    }

    /// <summary>Removes a clip the run left unfinished. ffmpeg is killed by then, but Windows
    /// can hold the handle a moment after the process exits, so a few short retries.</summary>
    private static async Task DeletePartialAsync(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(50);
        }
    }

    /// <summary>Cancels an in-flight clip export (Esc while it runs, and closing the dialog).</summary>
    public void CancelExport()
    {
        try { _exportCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>True while a clip export is running and can be cancelled.</summary>
    public bool CanCancelExport => IsRendering;

    private CancellationTokenSource? _exportCts;

    /// <summary>Unsubscribes from the player so the dialog can be garbage-collected.</summary>
    public void Detach()
    {
        ++_renderGeneration;   // stale in-flight preview renders dispose instead of re-installing

        // Stop a running export. Detach() previously tore down only the preview animator,
        // so closing the dialog mid-export left thousands of frames still rendering.
        CancelExport();
        _previewDebounceCts?.Cancel();
        foreach (var line in Lines)
            line.EndEdit(keep: true);

        TeardownAnimator();
        if (_player != null)
            _player.PropertyChanged -= OnPlayerPropertyChanged;
        foreach (var line in Lines)
            line.PropertyChanged -= OnLineChanged;
    }
}
