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
using Noctis.Services.LyricsPage;

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
    public SelectableLyricLine(string text, TimeSpan? timestamp = null, IReadOnlyList<WordTiming>? words = null,
        TimeSpan? endTimestamp = null, LyricLine? source = null)
    {
        _text = text;
        OriginalText = text;
        Timestamp = timestamp;
        Words = words;
        EndTimestamp = endTimestamp;
        if (source != null)
        {
            Voice = source.Voice;
            BackgroundWords = source.BackgroundWords;
            BackgroundEndTimestamp = source.BackgroundEndTimestamp;
            IsBackgroundOnly = source.IsBackgroundOnly;
        }
    }

    /// <summary>The line as the lyrics page has it, whatever the user typed over it.</summary>
    public string OriginalText { get; }

    /// <summary>Duet voice of the source line (the Lyrics Page video aligns by it).</summary>
    public LyricVoice Voice { get; }

    /// <summary>The source line's adlib (background vocal) words, when it has any.</summary>
    public IReadOnlyList<WordTiming>? BackgroundWords { get; }

    /// <summary>End of the source line's adlib; bounds its last word's sweep.</summary>
    public TimeSpan? BackgroundEndTimestamp { get; }

    /// <summary>The source line is only an adlib: the main row stays empty, only the small row shows.</summary>
    public bool IsBackgroundOnly { get; }

    /// <summary>Editable line text — changes re-render the card preview.</summary>
    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True while this line is the one currently playing (drives the sync highlight).</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Picked, and so is the line above / below: the list draws the run as one block.</summary>
    [ObservableProperty]
    private bool _joinsPrevious;

    [ObservableProperty]
    private bool _joinsNext;

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

    /// <summary>Lines the Lyrics Page video shows above and below the picked range, as the
    /// lyrics page has its neighbours on screen.</summary>
    public const int PageContextLines = 10;

    /// <summary>The song's last line holds the clip at least this long after it starts.</summary>
    private const double PageLastLineMinSeconds = 4;

    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    private readonly Track _track;
    private readonly PlayerViewModel? _player;
    private readonly string _autoColorHex;
    private int _currentSyncIndex = -1;
    private bool _syncUpdatingSelection;

    /// <summary>How many lines the card shows while following playback.</summary>
    private const int SyncWindow = 4;

    /// <summary>Raised with a line index when the synced view should scroll it into view.</summary>
    public event Action<int>? ScrollToLineRequested;

    public ObservableCollection<SelectableLyricLine> Lines { get; } = new();

    [ObservableProperty] private bool _isSquare = true;
    [ObservableProperty] private bool _isStory;

    // ── Layout: Card (panel header), Poster (big centered artwork) or Lyrics Page (the
    // moving lyrics page as a video). The three are exclusive. ────
    [ObservableProperty] private bool _isPanelLayout = true;
    [ObservableProperty] private bool _isPosterLayout;
    [ObservableProperty] private bool _isPageLayout;

    /// <summary>The Lyrics Page plays the song's timing, so it needs a line with a timestamp.</summary>
    public bool PageLayoutAvailable { get; }

    /// <summary>The Lyrics Page choice's tooltip: what it is, or why it is off.</summary>
    public string PageLayoutTip => PageLayoutAvailable ? L("LyricShare.LyricsPageTip") : L("ShareLyrics.PageNeedsSync");

    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>True while an MP4 clip is being rendered (disables the export buttons).</summary>
    [ObservableProperty] private bool _isRendering;

    [ObservableProperty] private ShareTextColor _textColor = ShareTextColor.Auto;

    // ── Lyrics Page background, readability and safe zones ──
    // What fills the video behind the lyrics, how dark the readability layer over it is, and
    // whether the lyrics keep clear of the apps' buttons. The picked file and the strength
    // are remembered for the next share (AppSettings.ShareLyricsPageBackground*).

    /// <summary>0 = artwork wash, 1 = the track's animated cover, 2 = the user's own file.
    /// A choice that has nothing to show (no animated cover, no file) snaps back to 0.</summary>
    [ObservableProperty] private int _pageBackgroundIndex;          // 0 artwork wash, 1 animated cover, 2 custom

    /// <summary>Readability layer strength, 0-100.</summary>
    [ObservableProperty] private int _pageBackgroundStrength = PageBackgroundTint.DefaultStrength;

    /// <summary>The user's own background (image or video), "" when none.</summary>
    [ObservableProperty] private string _pageBackgroundPath = "";

    /// <summary>Keeps the lyrics out of the social apps' button areas.</summary>
    [ObservableProperty] private bool _safeZonesEnabled;            // follows the format: on for Story, off for Square

    /// <summary>The track's animated cover, when it has one.</summary>
    public string? AnimatedCoverPath { get; }

    public bool AnimatedCoverAvailable => !string.IsNullOrEmpty(AnimatedCoverPath);

    /// <summary>A background file is chosen and still on disk.</summary>
    public bool CustomBackgroundAvailable =>
        !string.IsNullOrEmpty(PageBackgroundPath) && File.Exists(PageBackgroundPath);

    /// <summary>The strength as last read from (or written to) the settings; Detach saves a change.</summary>
    private int _savedPageStrength = PageBackgroundTint.DefaultStrength;

    partial void OnPageBackgroundIndexChanged(int value)
    {
        bool usable = value switch
        {
            0 => true,
            1 => AnimatedCoverAvailable,
            2 => CustomBackgroundAvailable,
            _ => false,
        };
        if (!usable) { PageBackgroundIndex = 0; return; }
        if (IsPageLayout) RefreshPreview();
    }

    partial void OnPageBackgroundStrengthChanged(int value)
    {
        if (value is < 0 or > 100) { PageBackgroundStrength = Math.Clamp(value, 0, 100); return; }
        if (!IsPageLayout) return;
        // Only the layer over the background changes: the shown animator takes it as it is, so a
        // slider drag neither restarts the loop nor a clip's ffmpeg. A build in flight read the
        // old value, so that one is redone.
        if (_animator is LyricsPageAnimator page && !_previewRendering && !_previewStale)
        {
            page.BackgroundStrength = value;
            _animLastT = double.NaN;
            RenderAnimatedFrame();
        }
        else
        {
            RefreshPreview();
        }
    }

    partial void OnPageBackgroundPathChanged(string value)
    {
        OnPropertyChanged(nameof(CustomBackgroundAvailable));
        if (PageBackgroundIndex == 2 && !CustomBackgroundAvailable) PageBackgroundIndex = 0;
        else if (IsPageLayout) RefreshPreview();
    }

    partial void OnSafeZonesEnabledChanged(bool value)
    {
        if (IsPageLayout) RefreshPreview();
    }

    /// <summary>Uses <paramref name="path"/> as the Lyrics Page background and remembers it.</summary>
    public async Task SetPageBackgroundFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        PageBackgroundPath = path;
        PageBackgroundIndex = 2;
        await SavePageSettingsAsync(s => s.ShareLyricsPageBackgroundPath = path);
    }

    /// <summary>Forgets the chosen background file; the artwork takes its place when it was showing.</summary>
    [RelayCommand]
    private Task RemovePageBackground()
    {
        PageBackgroundPath = "";
        return SavePageSettingsAsync(s => s.ShareLyricsPageBackgroundPath = "");
    }

    /// <summary>
    /// The remembered background and strength, as the settings hold them: a file that still
    /// exists is the pick again (index 2), a gone one is dropped; the strength is clamped,
    /// settings.json may hold anything.
    /// </summary>
    internal void ApplyPageSettings(string path, int strength)
    {
        PageBackgroundStrength = Math.Clamp(strength, 0, 100);
        _savedPageStrength = PageBackgroundStrength;
        bool exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        PageBackgroundPath = exists ? path : "";
        if (exists) PageBackgroundIndex = 2;
    }

    /// <summary>Reads the remembered page settings off the UI thread and applies them on it.
    /// Without the app's services (tests, design time) the defaults stay.</summary>
    private async Task LoadPageSettingsAsync(Dispatcher dispatcher)
    {
        if (App.Services?.GetService<IPersistenceService>() is not { } persistence) return;
        try
        {
            var settings = await persistence.LoadSettingsAsync().ConfigureAwait(false);
            var path = settings.ShareLyricsPageBackgroundPath ?? "";
            var strength = settings.ShareLyricsPageBackgroundStrength;
            dispatcher.Post(() => { if (!_detached) ApplyPageSettings(path, strength); });
        }
        catch (Exception ex)
        {
            DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Warn,
                "Share lyrics page settings load failed", ex.Message);
        }
    }

    private static async Task SavePageSettingsAsync(Action<AppSettings> change)
    {
        if (App.Services?.GetService<IPersistenceService>() is not { } persistence) return;
        try
        {
            await persistence.UpdateSettingsAsync(change);
        }
        catch (Exception ex)
        {
            DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Warn,
                "Share lyrics page settings save failed", ex.Message);
        }
    }

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

    /// <summary>0 = Card (panel header), 1 = Poster, 2 = Lyrics Page (ignored for unsynced lyrics).</summary>
    public int LayoutIndex
    {
        get => IsPageLayout ? 2 : IsPosterLayout ? 1 : 0;
        set
        {
            if (value == 2)
            {
                if (PageLayoutAvailable) IsPageLayout = true;
                else OnPropertyChanged(nameof(LayoutIndex));   // the drop-down snaps back
            }
            else if (value == 1) IsPosterLayout = true;
            else if (value == 0) IsPanelLayout = true;
        }
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

    /// <summary>The karaoke timing the preview and the clip use for the card's lines (tests).</summary>
    internal IReadOnlyList<KaraokeLine> CardKaraoke => BuildKaraokeLines(CardSelection());

    /// <summary>"3 of 8" next to Clear; in the Lyrics Page, "12 lines · 34 s" (the clip's length).</summary>
    public string SelectionSummary => IsPageLayout
        ? L("ShareLyrics.PageRangeSummary", SelectedCount, (int)Math.Round(GetClipTiming().DurationSeconds))
        : L("ShareLyrics.SelectedCount", SelectedCount, MaxLines);

    private void RaiseSelectionChanged()
    {
        UpdateSelectionRuns();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanExportVideo));
    }

    /// <summary>Marks the picked lines that touch another picked line, so the list draws each
    /// run of neighbours as one block rather than a stack of separate bars.</summary>
    private void UpdateSelectionRuns()
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            line.JoinsPrevious = line.IsSelected && i > 0 && Lines[i - 1].IsSelected;
            line.JoinsNext = line.IsSelected && i + 1 < Lines.Count && Lines[i + 1].IsSelected;
        }
    }

    /// <summary>
    /// A tap on a row. On a card it picks or drops the line (the 8-line cap still applies).
    /// In the Lyrics Page the picks are one unbroken range: a tap outside it extends it to the
    /// line, a tap inside ends it there, and tapping the only picked line clears it. A range
    /// longer than the 60 s clip is refused with a status.
    /// </summary>
    public void ToggleLine(SelectableLyricLine line)
    {
        if (!IsPageLayout)
        {
            line.IsSelected = !line.IsSelected;
            return;
        }

        int index = Lines.IndexOf(line);
        if (index < 0) return;
        int first = -1, last = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (!Lines[i].IsSelected) continue;
            if (first < 0) first = i;
            last = i;
        }

        if (first < 0) { SetRange(index, index); return; }
        if (first == last && index == first) { SetRange(0, -1); return; }

        int newFirst = Math.Min(first, index);
        int newLast = index < first ? last : index;
        if (!PageRangeFits(newFirst, newLast))
        {
            StatusText = L("ShareLyrics.PageMaxLength", (int)ShareClipTiming.MaxSeconds);
            return;
        }
        SetRange(newFirst, newLast);
    }

    /// <summary>A manual range change in the Lyrics Page: takes over from playback, as a tap
    /// on a card does, then picks lines <paramref name="first"/>..<paramref name="last"/>
    /// (none when last &lt; first).</summary>
    private void SetRange(int first, int last)
    {
        if (SyncEnabled) SyncEnabled = false;
        StatusText = string.Empty;
        ApplyRange(first, last);
    }

    /// <summary>Starts editing one line (any other open edit is kept first).</summary>
    public void BeginEdit(SelectableLyricLine line)
    {
        foreach (var other in Lines)
            if (!ReferenceEquals(other, line)) other.EndEdit(keep: true);
        // An edited line is one meant for the card. When the 8-line cap refuses it, no edit
        // box: the typing would change a line the card does not show. In the Lyrics Page the
        // range first grows to the line (or, past 60 s, refuses it the same way).
        if (!line.IsSelected)
        {
            if (IsPageLayout) ToggleLine(line);
            else line.IsSelected = true;
        }
        if (!line.IsSelected) return;
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

    /// <summary>Skia scale for the on-screen still (export uses CardExportScale): 540 px wide,
    /// as the karaoke overlay, for a ~310 px box.</summary>
    private const float PreviewRenderScale = 0.5f;

    /// <summary>Same small lead as the lyrics page so the sweep matches the vocal.</summary>
    private const double PreviewWordLookaheadSeconds = 0.08;

    /// <summary>Live word-sweep frame shown over the static preview; null when not animating.</summary>
    [ObservableProperty] private Bitmap? _animatedPreview;

    /// <summary>Raised after each animated frame so the view can invalidate the Image
    /// (in-place WriteableBitmap updates don't notify the binding).</summary>
    public event Action? AnimatedFrameRendered;

    private IShareFrameAnimator? _animator;
    private WriteableBitmap? _animBitmap;

    // The Lyrics Page preview: its frames are drawn on a worker (a clip background can block
    // a draw), one at a time into _pageFrame, then copied into _animBitmap on the UI thread.
    // An animator is disposed only once the draw in flight (_pageDraw) has finished.
    private readonly System.Diagnostics.Stopwatch _pagePreviewClock = new();
    private ShareClipTiming _pagePreviewTiming;
    private bool _keepPageClock;
    private Task _pageDraw = Task.CompletedTask;
    private bool _pageDrawBusy;
    private byte[]? _pageFrame;

    /// <summary>A copy of the last page frame drawn (tests: the headless WriteableBitmap is a
    /// stub that reads back blank). Null while none was drawn or one is drawing.</summary>
    internal byte[]? PageFrameSnapshot() => _pageDrawBusy ? null : (byte[]?)_pageFrame?.Clone();
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
            var layout = IsPageLayout ? L("LyricShare.LyricsPage") : IsPosterLayout ? "Poster" : "Card";
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

    public string TrackTitle => _track.Title;
    public string TrackArtist => _track.ArtistDisplay;

    /// <summary>The header's second line: "TUTU · 6ix9ine" (just the title without an artist).</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(TrackArtist) ? TrackTitle : $"{TrackTitle} · {TrackArtist}";

    /// <summary>Suggested file name for the save dialog.</summary>
    public string SuggestedFileName
    {
        get
        {
            // No artist: the title alone (it read " - Intro lyrics.png").
            var name = string.IsNullOrWhiteSpace(_track.Artist)
                ? $"{_track.Title} lyrics"
                : $"{_track.Artist} - {_track.Title} lyrics";
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
        && (!IsPageLayout || PageLayoutAvailable)
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
        int preselectIndex = 0,
        IReadOnlyList<LyricLine>? sources = null)
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
            var source = sources != null && i < sources.Count ? sources[i] : null;
            Lines.Add(new SelectableLyricLine(OneLine(lines[i]), ts, words, end, source));
        }

        SyncAvailable = player != null && Lines.Any(l => l.Timestamp.HasValue);
        PageLayoutAvailable = Lines.Any(l => l.Timestamp.HasValue);
        AnimatedCoverPath = App.Services?.GetService<IAnimatedCoverService>()?.Resolve(track);
        _safeZonesEnabled = IsStory;
        _ = LoadPageSettingsAsync(Dispatcher.UIThread);

        // Pre-select the active line and the next few, like Spotify does.
        if (Lines.Count > 0)
        {
            int start = Math.Clamp(preselectIndex, 0, Lines.Count - 1);
            for (int i = start; i < Math.Min(start + 4, Lines.Count); i++)
                Lines[i].IsSelected = true;
            UpdateSelectionRuns();
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

            // The 8-line cap is the card's; the Lyrics Page is limited by its 60 s clip instead.
            if (!IsPageLayout && line.IsSelected && Lines.Count(l => l.IsSelected) > MaxLines)
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
        var adjusted = position + TimeSpan.FromSeconds(SyncLeadSeconds);
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
        // The page preview follows playback by itself: rebuilding it at each line would snap its
        // list (a new animator's first frame). Only once playback has left its clip is it redone.
        if (IsPageLayout && _animator is LyricsPageAnimator && _player != null
            && PlaybackInPagePreview(_player.Position.TotalSeconds))
            return;
        RefreshPreview();
    }

    /// <summary>How far ahead of a line Sync picks it (<see cref="UpdateSyncHighlight"/>).</summary>
    private const double SyncLeadSeconds = 0.35;

    /// <summary>Whether playback at <paramref name="seconds"/> is inside the page preview's clip
    /// (from Sync's lead before its start).</summary>
    private bool PlaybackInPagePreview(double seconds) =>
        seconds >= _pagePreviewTiming.StartSeconds - SyncLeadSeconds
        && seconds < _pagePreviewTiming.StartSeconds + _pagePreviewTiming.DurationSeconds;

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
        if (value) { IsStory = false; SafeZonesEnabled = false; RefreshPreview(); }
    }

    partial void OnIsStoryChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(FormatIndex));
        if (value) { IsSquare = false; SafeZonesEnabled = true; RefreshPreview(); }
    }

    partial void OnIsPanelLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(LayoutIndex));
        if (value) { IsPosterLayout = false; IsPageLayout = false; }
        RaiseSelectionChanged();   // the selection summary reads differently per layout
        if (value) RefreshPreview();
    }

    partial void OnIsPosterLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(LayoutIndex));
        if (value) { IsPanelLayout = false; IsPageLayout = false; }
        RaiseSelectionChanged();
        if (value) RefreshPreview();
    }

    partial void OnIsPageLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOptionsSummary));
        OnPropertyChanged(nameof(CanExportVideo));
        OnPropertyChanged(nameof(LayoutIndex));
        if (value)
        {
            IsPanelLayout = false;
            IsPosterLayout = false;
            // The page's picks set the clip's time window; following playback would re-pick
            // them, and rebuild the preview, at every line.
            if (SyncEnabled) SyncEnabled = false;
            FillPageRange();
        }
        else
        {
            KeepFirstCardLines();
            // The 60 s warning is the page's; a card has no such limit.
            if (StatusText == L("ShareLyrics.PageMaxLength", (int)ShareClipTiming.MaxSeconds))
                StatusText = string.Empty;
        }
        RaiseSelectionChanged();
        if (value) RefreshPreview();
    }

    /// <summary>
    /// Entering the Lyrics Page: the video plays one stretch of the song, so the picks become
    /// every line from the first pick to the last, cut where the 60 s clip would end.
    /// </summary>
    private void FillPageRange()
    {
        StatusText = string.Empty;   // a card's status says nothing about the page
        int first = -1, last = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (!Lines[i].IsSelected) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return;

        int end = last;
        while (end > first && !PageRangeFits(first, end)) end--;
        ApplyRange(first, end);
        if (end < last)
            StatusText = L("ShareLyrics.PageMaxLength", (int)ShareClipTiming.MaxSeconds);
    }

    /// <summary>Leaving the Lyrics Page for a card: the card holds 8 lines, so the first 8 picks stay.</summary>
    private void KeepFirstCardLines()
    {
        int kept = 0;
        bool changed = false;
        _syncUpdatingSelection = true;
        foreach (var line in Lines)
        {
            if (!line.IsSelected || ++kept <= MaxLines) continue;
            line.IsSelected = false;
            changed = true;
        }
        _syncUpdatingSelection = false;
        if (!changed) return;
        RaiseSelectionChanged();
        OnPropertyChanged(nameof(KaraokeAvailable));
        OnPropertyChanged(nameof(CardOptionsSummary));
    }

    /// <summary>
    /// Whether lines <paramref name="first"/>..<paramref name="last"/> fit one Lyrics Page clip:
    /// <see cref="PageClipWindow"/> is at most 60 s. The clip clamps to 60 s, so a longer range
    /// would cut its last line. Nothing timed in the range: the clip uses its fallback length.
    /// </summary>
    private bool PageRangeFits(int first, int last)
        => PageClipWindow(first, last) is not { } window
           || window.End - window.Start <= ShareClipTiming.MaxSeconds;

    /// <summary>
    /// The Lyrics Page clip of lines <paramref name="first"/>..<paramref name="last"/>, in track
    /// seconds, its end not yet capped: from the range's earliest timestamp to the next timed
    /// line's start. When no timed line follows (the range ends on the song's last timed line),
    /// to where that line is sung out: its end tag, else its last word's end, or its adlib's end
    /// when that is later, but at least 4 s after it starts, not the card's flat 15 s. Null when no line in the range is timed.
    /// One function for the 60 s check, the clip and the page spec, so the three agree.
    /// </summary>
    private (double Start, double End)? PageClipWindow(int first, int last)
    {
        double? start = null;
        SelectableLyricLine? lastTimed = null;
        for (int i = first; i <= last; i++)
        {
            if (Lines[i].Timestamp is not { } ts) continue;
            start = start is { } s ? Math.Min(s, ts.TotalSeconds) : ts.TotalSeconds;
            lastTimed = Lines[i];
        }
        if (start is not { } startSeconds || lastTimed?.Timestamp is not { } lastStart)
            return null;
        startSeconds = Math.Max(0, startSeconds);

        for (int i = last + 1; i < Lines.Count; i++)
            if (Lines[i].Timestamp is { } next) return (startSeconds, next.TotalSeconds);

        double lineStart = lastStart.TotalSeconds;
        // No line follows, so an open last word gets the lyrics page's 2 s sweep.
        double? sungEnd = lastTimed.EndTimestamp?.TotalSeconds
            ?? (lastTimed.Words is { Count: > 0 } words
                ? (words[^1].End ?? KaraokeSweep.ResolveOpenLastWordEnd(words[^1].Start, null)).TotalSeconds
                : null);
        // An adlib can run on past the main line: the clip waits for it too.
        double? adlibEnd = lastTimed.BackgroundEndTimestamp?.TotalSeconds;
        return (startSeconds, Math.Max(Math.Max(sungEnd ?? lineStart, adlibEnd ?? lineStart),
            lineStart + PageLastLineMinSeconds));
    }

    /// <summary>The first and last picked line, or null when none is picked.</summary>
    private (int First, int Last)? PickedSpan()
    {
        int first = -1, last = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (!Lines[i].IsSelected) continue;
            if (first < 0) first = i;
            last = i;
        }
        return first < 0 ? null : (first, last);
    }

    /// <summary>Picks exactly lines <paramref name="first"/>..<paramref name="last"/> in one go
    /// (an empty range, last &lt; first, picks none), then refreshes like a sync step does.</summary>
    private void ApplyRange(int first, int last)
    {
        _syncUpdatingSelection = true;
        for (int i = 0; i < Lines.Count; i++)
        {
            bool sel = i >= first && i <= last;
            if (Lines[i].IsSelected != sel)
                Lines[i].IsSelected = sel;
        }
        _syncUpdatingSelection = false;
        RaiseSelectionChanged();
        OnPropertyChanged(nameof(KaraokeAvailable));
        OnPropertyChanged(nameof(CardOptionsSummary));
        RefreshPreview();
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
    private void SelectPageLayout()
    {
        if (PageLayoutAvailable) IsPageLayout = true;
    }

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

    // The live preview: one render at a time, the newest state wins. A change marks the card
    // stale; it is drawn right after the current UI work (an option sets several flags in a
    // row, each landing here: drawn once, from the final state), or, while a render runs,
    // the moment that render lands.
    //
    // The editable lyric line is a TwoWay TextBox binding with no UpdateSourceTrigger, so
    // every keystroke lands here; unserialized, ten characters started ten concurrent
    // renders. A 200 ms trailing debounce stood in for this, and every option click waited
    // it out too, before a render that decoded the cover again, drew 1080 px, PNG-encoded it
    // and decoded the PNG back (owner 10-12: options took "about a second"; headless with a
    // 3000 px cover, 360-760 ms a change). Now no timer, the cover is decoded once, and the
    // card is drawn at preview size straight into the bitmap.
    private bool _previewRendering;
    private bool _previewStale;
    private bool _previewPosted;
    private bool _detached;

    /// <summary>The cover, decoded by the first render and reused by every later one. Only
    /// the running render touches it; freed by Detach, or by a render landing after it.</summary>
    private SkiaSharp.SKBitmap? _previewArt;
    private bool _previewArtLoaded;

    private void RefreshPreview()
    {
        _previewStale = true;
        if (_previewRendering || _previewPosted) return;
        _previewPosted = true;
        Dispatcher.UIThread.Post(StartPreviewRender);
    }

    /// <summary>True when the card shown is drawn from the current options (tests).</summary>
    internal bool IsPreviewCurrent => !_previewStale && !_previewRendering;

    private void StartPreviewRender()
    {
        _previewPosted = false;
        if (_detached || _previewRendering || !_previewStale) return;
        _previewStale = false;
        RefreshPreviewCore();
    }

    private void RefreshPreviewCore()
    {
        var selectedLines = CardSelection();
        var selected = selectedLines.Select(l => l.Text).ToList();
        if (IsPageLayout ? PickedSpan() == null : selected.Count == 0)
        {
            var old = Preview;
            Preview = null;
            // Deferred, as below: the last frame may still reference it.
            if (old != null) Dispatcher.UIThread.Post(old.Dispose);
            TeardownAnimator();
            return;
        }

        if (IsPageLayout)
        {
            RefreshPagePreview();
            return;
        }

        var spec = BuildSpec(selected);
        bool animate = KaraokeEnabled && _player != null
            && selectedLines.Any(l => l.Words is { Count: > 0 });
        var karaoke = animate ? BuildKaraokeLines(selectedLines) : null;

        _previewRendering = true;
        // Captured here: the worker must not be the first to read Dispatcher.UIThread
        // (lazily created on whichever thread asks, as in each headless test's fresh app).
        var dispatcher = Dispatcher.UIThread;
        Task.Run(() =>
        {
            Bitmap? bitmap = null;
            IShareFrameAnimator? animator = null;
            try
            {
                LoadPreviewArt(spec.ArtworkPath);
                // Preview size only: it fills a ~310 px box. Save/Copy render at full
                // scale on demand (RenderExportPngAsync).
                using (var card = ShareCardRenderer.RenderLyricCardBitmap(spec, PreviewRenderScale, _previewArt))
                {
                    var preview = new WriteableBitmap(new PixelSize(card.Width, card.Height),
                        new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using (var fb = preview.Lock())
                    using (var pixels = card.PeekPixels())
                        pixels.ReadPixels(card.Info, fb.Address, fb.RowBytes);
                    bitmap = preview;
                }
                if (karaoke != null)
                    animator = new ShareCardRenderer.KaraokeCardAnimator(spec, karaoke, PreviewAnimatorScale, _previewArt);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                    "Share card render failed", ex.Message);
            }
            dispatcher.Post(() => OnPreviewRendered(bitmap, animator));
        });
    }

    /// <summary>Decodes the cover on the first render's worker; later renders reuse it.</summary>
    private void LoadPreviewArt(string? artworkPath)
    {
        if (_previewArtLoaded) return;
        _previewArt = ShareCardRenderer.LoadCardArtwork(artworkPath);
        _previewArtLoaded = true;
    }

    /// <summary>
    /// The Lyrics Page preview: the page animator at preview scale, built on a worker (a clip
    /// background starts ffmpeg) from a spec built here once, with no still card under it:
    /// the animated frame fills the well.
    /// </summary>
    private void RefreshPagePreview()
    {
        var spec = BuildPageSpec();   // once per refresh: it walks the range with IndexOf
        var timing = GetClipTiming();
        bool showZones = SafeZonesEnabled;
        _previewRendering = true;
        var dispatcher = Dispatcher.UIThread;   // captured here, as in the card path
        Task.Run(() =>
        {
            IShareFrameAnimator? animator = null;
            try
            {
                LoadPreviewArt(spec.ArtworkPath);
                animator = new LyricsPageAnimator(spec, PreviewAnimatorScale, _previewArt,
                    blockingBackground: false, backgroundFps: 30) { ShowSafeZones = showZones };
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                    "Lyrics Page preview failed", ex.Message);
            }
            dispatcher.Post(() =>
            {
                if (animator != null)
                {
                    // Same clip (an option changed, not the range): the loop goes on where it was.
                    _keepPageClock = _animator is LyricsPageAnimator && _pagePreviewTiming == timing;
                    _pagePreviewTiming = timing;
                }
                OnPreviewRendered(null, animator);
            });
        });
    }

    /// <summary>Shows a finished render (UI thread), then draws again when the card changed meanwhile.
    /// A page render brings an animator and no still: that is a success too.</summary>
    private void OnPreviewRendered(Bitmap? bitmap, IShareFrameAnimator? animator)
    {
        _previewRendering = false;
        if (_detached || (bitmap == null && animator == null))
        {
            bitmap?.Dispose();
            DisposeAnimator(animator);
            if (_detached) { _previewArt?.Dispose(); _previewArt = null; return; }
        }
        else
        {
            var old = Preview;
            Preview = bitmap;
            // Deferred: the previous bitmap can still be referenced by the last
            // committed render frame in this same UI turn. AnimatedCoverImage
            // defers every bitmap disposal for exactly this reason.
            if (old != null) Dispatcher.UIThread.Post(old.Dispose);
            SwapAnimator(animator);
        }

        StartPreviewRender();   // the options changed while this one rendered
    }

    /// <summary>Installs the freshly built animator (or tears everything down when null),
    /// reusing the WriteableBitmap when the pixel size is unchanged. UI thread only.</summary>
    private void SwapAnimator(IShareFrameAnimator? animator)
    {
        if (animator == null)
        {
            TeardownAnimator();
            return;
        }

        DisposeAnimator(_animator);
        _animator = animator;
        _animLastT = double.NaN;
        if (animator is not LyricsPageAnimator) _pagePreviewClock.Reset();
        else if (!_keepPageClock || !_pagePreviewClock.IsRunning) _pagePreviewClock.Restart();
        _keepPageClock = false;

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
        DisposeAnimator(_animator);
        _animator = null;
        _pagePreviewClock.Reset();
        var old = AnimatedPreview;
        AnimatedPreview = null;
        old?.Dispose();
        _animBitmap = null;
        _animLastT = double.NaN;
    }

    /// <summary>Disposes an animator no longer shown. A page animator may still be drawing on a
    /// worker (its clip background must not be torn down mid-draw), so it goes once that draw ends.</summary>
    private void DisposeAnimator(IShareFrameAnimator? animator)
    {
        if (animator is LyricsPageAnimator)
            _pageDraw.ContinueWith(_ => animator.Dispose(), TaskScheduler.Default);
        else
            animator?.Dispose();
    }

    /// <summary>The track time the animated preview shows now. The card follows playback. The
    /// page loops its clip, and follows playback only with Sync on while this song plays inside
    /// that clip: it previews the clip, not wherever the song is.</summary>
    internal double AnimatedPreviewTime()
    {
        if (_animator is not LyricsPageAnimator)
            return GetSmoothedPositionSeconds() + PreviewWordLookaheadSeconds;
        bool playing = SyncEnabled && IsOurTrackPlaying && _player?.State == PlaybackState.Playing;
        double position = playing ? GetSmoothedPositionSeconds() : 0;
        return PagePreviewClock.TrackTime(playing && PlaybackInPagePreview(position), position,
            _pagePreviewClock.Elapsed.TotalSeconds, _pagePreviewTiming);
    }

    private void OnAnimTick()
    {
        if (_animator == null)
        {
            _animTimer?.Stop();
            return;
        }
        double t = AnimatedPreviewTime();
        // Paused (or between coarse position updates while paused) → nothing moved.
        if (!double.IsNaN(_animLastT) && Math.Abs(t - _animLastT) < 0.0005)
            return;
        RenderAnimatedFrameAt(t);
    }

    private void RenderAnimatedFrame() => RenderAnimatedFrameAt(AnimatedPreviewTime());

    private void RenderAnimatedFrameAt(double t)
    {
        if (_animator == null || _animBitmap == null) return;
        if (_animator is LyricsPageAnimator page)
        {
            RequestPageFrame(page, t);
            return;
        }
        _animLastT = t;
        using (var fb = _animBitmap.Lock())
            _animator.RenderFrame(t, fb.Address, fb.RowBytes);
        AnimatedFrameRendered?.Invoke();
    }

    /// <summary>Draws the page frame at <paramref name="t"/> on a worker, unless one is still
    /// drawing (the next tick asks again). UI thread.</summary>
    private void RequestPageFrame(LyricsPageAnimator page, double t)
    {
        if (_pageDrawBusy) return;
        _pageDrawBusy = true;
        _animLastT = t;
        int rowBytes = page.PixelWidth * 4;
        int length = rowBytes * page.PixelHeight;
        if (_pageFrame?.Length != length) _pageFrame = new byte[length];
        var frame = _pageFrame;
        var dispatcher = Dispatcher.UIThread;
        _pageDraw = Task.Run(() =>
        {
            bool drawn = false;
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(frame, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                page.RenderFrame(t, handle.AddrOfPinnedObject(), rowBytes);
                drawn = true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                    "Lyrics Page preview frame failed", ex.Message);
            }
            finally { handle.Free(); }
            dispatcher.Post(() => OnPageFrameDrawn(page, frame, rowBytes, drawn));
        });
    }

    /// <summary>Shows a page frame drawn on the worker while its animator is still the one shown. UI thread.</summary>
    private void OnPageFrameDrawn(LyricsPageAnimator page, byte[] frame, int rowBytes, bool drawn)
    {
        _pageDrawBusy = false;
        if (_animator != page || _animBitmap == null)
        {
            // Swapped meanwhile: the new animator's first frame was skipped while this one drew.
            if (_animator is LyricsPageAnimator next) RequestPageFrame(next, AnimatedPreviewTime());
            return;
        }
        if (!drawn) return;
        using (var fb = _animBitmap.Lock())
        {
            for (int y = 0; y < page.PixelHeight; y++)
                System.Runtime.InteropServices.Marshal.Copy(frame, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }
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
    /// end times (explicit end → next word → line end → the lyrics page's open-word end:
    /// the next lyric line's start, capped at +2 s). Lines without word data get
    /// StartSeconds only (line-level highlight).
    /// </summary>
    private IReadOnlyList<KaraokeLine> BuildKaraokeLines(IReadOnlyList<SelectableLyricLine> selected)
    {
        var result = new List<KaraokeLine>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            var line = selected[i];
            if (line.Words is not { Count: > 0 } words)
            {
                result.Add(new KaraokeLine { StartSeconds = line.Timestamp?.TotalSeconds });
                continue;
            }

            result.Add(new KaraokeLine
            {
                StartSeconds = line.Timestamp?.TotalSeconds,
                Words = ToKaraokeWords(line, words, line.EndTimestamp),
            });
        }
        return result;
    }

    /// <summary>
    /// One word layer of <paramref name="line"/> (its words, or its adlib row) as sanitized
    /// tokens with resolved end times: explicit end, next word, <paramref name="layerEnd"/>,
    /// then the lyrics page's open-word end (the next lyric line's start, capped at +2 s).
    /// </summary>
    private List<KaraokeWord> ToKaraokeWords(SelectableLyricLine line, IReadOnlyList<WordTiming> words, TimeSpan? layerEnd)
    {
        // The next line of the song, not the next picked one: picks can skip lines, and
        // the last pick has none. Either way the open word swept on over lines that are
        // sung meanwhile (or ran 2 s past the next line's start).
        TimeSpan? nextLineStart = null;
        for (int j = Lines.IndexOf(line) + 1; j > 0 && j < Lines.Count; j++)
            if (Lines[j].Timestamp is { } ts) { nextLineStart = ts; break; }

        var karaokeWords = new List<KaraokeWord>(words.Count);
        for (int k = 0; k < words.Count; k++)
        {
            var token = ShareCardRenderer.SanitizeForRender(words[k].Text);
            if (token.Length == 0)
                continue;   // pure-whitespace word carries no rendered token
            double start = words[k].Start.TotalSeconds;
            double end = words[k].End?.TotalSeconds
                ?? (k + 1 < words.Count ? words[k + 1].Start.TotalSeconds
                    : layerEnd?.TotalSeconds
                      ?? KaraokeSweep.ResolveOpenLastWordEnd(words[k].Start, nextLineStart).TotalSeconds);
            karaokeWords.Add(new KaraokeWord(token, start, end));
        }
        return karaokeWords;
    }

    /// <summary>The Lyrics Page video's spec from the dialog as it stands (tests).</summary>
    internal LyricsPageSpec PageSpec => BuildPageSpec();

    /// <summary>
    /// The Lyrics Page video's spec: the picked range plus up to <see cref="PageContextLines"/>
    /// lines either side, as the lyrics page shows its neighbours. Edits count only inside
    /// the range (outside, the lines read as the lyrics page has them); an edited line drops
    /// its word timing, which belongs to the words it replaced, and lights as a whole line.
    /// </summary>
    private LyricsPageSpec BuildPageSpec()
    {
        var lines = new List<LyricsPageLine>();
        if (PickedSpan() is { } span)
        {
            int from = Math.Max(0, span.First - PageContextLines);
            int to = Math.Min(Lines.Count - 1, span.Last + PageContextLines);
            for (int i = from; i <= to; i++)
            {
                var line = Lines[i];
                bool inRange = i >= span.First && i <= span.Last;
                bool edited = inRange && line.Text != line.OriginalText;
                lines.Add(new LyricsPageLine
                {
                    Text = inRange ? line.Text : line.OriginalText,
                    StartSeconds = line.Timestamp?.TotalSeconds,
                    EndSeconds = line.EndTimestamp?.TotalSeconds,
                    Words = !edited && line.Words is { Count: > 0 } words
                        ? ToKaraokeWords(line, words, line.EndTimestamp) : null,
                    BackgroundWords = line.BackgroundWords is { Count: > 0 } adlibs
                        ? ToKaraokeWords(line, adlibs, line.BackgroundEndTimestamp) : null,
                    Voice = line.Voice,
                    IsBackgroundOnly = line.IsBackgroundOnly,
                });
            }
        }

        var (kind, media) = PageBackgroundIndex switch
        {
            1 when AnimatedCoverAvailable => (LyricsPageBackgroundKind.AnimatedCover, AnimatedCoverPath),
            2 when CustomBackgroundAvailable => (LyricsPageBackgroundKind.Custom, PageBackgroundPath),
            _ => (LyricsPageBackgroundKind.ArtworkWash, (string?)null),
        };

        return new LyricsPageSpec
        {
            Title = _track.Title,
            Artist = _track.ArtistDisplay,
            ArtworkPath = _track.AlbumArtworkPath,
            IsExplicit = _track.IsExplicit,
            Format = IsStory ? ShareCardFormat.Story : ShareCardFormat.Square,
            Lines = lines,
            SafeZones = SafeZonesEnabled,
            BackgroundKind = kind,
            BackgroundMediaPath = media,
            BackgroundStrength = PageBackgroundStrength,
            FfmpegPath = App.Services?.GetService<IAudioConverterService>()?.GetFfmpegPath(),
            ClipStartSeconds = GetClipTiming().StartSeconds,
        };
    }

    /// <summary>Derives the clip's audio window from the selected lines and playback position.</summary>
    public ShareClipTiming GetClipTiming()
    {
        // The Lyrics Page ends a range on the song's last line with that line, not 15 s on.
        if (IsPageLayout && PickedSpan() is { } span && PageClipWindow(span.First, span.Last) is { } page)
            return new ShareClipTiming(page.Start,
                Math.Clamp(page.End - page.Start, ShareClipTiming.MinSeconds, ShareClipTiming.MaxSeconds));

        var lines = Lines.Select(l => new ShareClipLine(l.Timestamp, l.IsSelected)).ToList();
        double? position = IsOurTrackPlaying ? _player?.Position.TotalSeconds : null;
        return ShareClipTiming.Compute(lines, position);
    }

    /// <summary>
    /// Renders the card at full export resolution, on demand.
    ///
    /// <see cref="Preview"/> is a preview-size bitmap (it fills a ~310px box and used to
    /// be held at up to 33 MB for the dialog's lifetime), so the Save and Copy paths
    /// render their own.
    /// </summary>
    public async Task<byte[]?> RenderExportPngAsync()
    {
        if (IsPageLayout)
            return await RenderPageStillPngAsync();

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
    /// The Lyrics Page still for Save Card and Copy: the page's frame halfway through the range's
    /// first line (<see cref="LyricsPageRenderer.StillTime"/>), at Save Card's 2× scale.
    /// </summary>
    private async Task<byte[]?> RenderPageStillPngAsync()
    {
        if (PickedSpan() is not { } span) return null;
        var spec = BuildPageSpec();
        var timing = GetClipTiming();
        double? nextStart = Lines.Skip(span.First + 1)
            .Select(l => l.Timestamp?.TotalSeconds).FirstOrDefault(s => s != null);
        var t = LyricsPageRenderer.StillTime(timing, nextStart);
        try
        {
            // Off the UI thread: a clip background waits for its frame.
            var ct = _lifetime.Token;
            return await Task.Run(() => LyricsPageRenderer.RenderStillPng(spec, t, ShareCardRenderer.CardExportScale, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return null;   // the dialog closed meanwhile
        }
        catch (Exception ex)
        {
            DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                "Lyrics Page still render failed", ex.Message);
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
    /// behind a closed dialog. ffmpeg writes a work file in the temp folder that replaces
    /// <paramref name="outputPath"/> only once the clip is complete: written in place, -y
    /// truncated the file the user picked to replace, so a cancel or a failure left a cut-off
    /// clip where it had been. An unfinished work file is deleted.
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
        var workPath = Path.Combine(Path.GetTempPath(), $"noctis-clip-{Guid.NewGuid():N}.mp4");
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

            if (IsPageLayout)
            {
                // Lyrics Page: the page's motion frame by frame at 60 fps, on a worker (a clip
                // background waits for each frame), with the short audio fade in and out.
                var pageSpec = BuildPageSpec();
                (ok, error) = await RenderFrameClipAsync(ffmpeg, LyricsPageFrames.Fps, timing, workPath, cts,
                    (frameDir, report, ct) => LyricsPageRenderer.RenderFrames(
                        pageSpec, timing, LyricsPageFrames.Fps, frameDir, report, ct),
                    ShareClipRenderer.PageFadeSeconds);
            }
            else if (!(KaraokeEnabled && KaraokeAvailable))
            {
                // Base-resolution still: the clip is 1080p-bound, so the 2× supersampled
                // export card would only quadruple the encode cost.
                var still = await Task.Run(() => ShareCardRenderer.RenderLyricCardStyled(spec), token);
                (ok, error) = await ShareClipRenderer.RenderAsync(ffmpeg, still, _track.FilePath, workPath, timing, token);
            }
            else
            {
                // Karaoke path: per-frame word sweep → JPEG sequence → ffmpeg mux.
                var karaoke = BuildKaraokeLines(selected);
                (ok, error) = await RenderFrameClipAsync(ffmpeg, KaraokeFps, timing, workPath, cts,
                    (frameDir, report, ct) => ShareCardRenderer.RenderKaraokeFrames(
                        spec, karaoke, timing, KaraokeFps, frameDir, report, ct),
                    fadeSeconds: 0);
            }

            // ShareClipRenderer turns a killed ffmpeg into (false, "cancelled"): that is a
            // cancel, not a failure.
            if (token.IsCancellationRequested)
            {
                ok = false;
                return L("ShareLyrics.Cancelled");
            }
            if (!ok)
                return L("ShareLyrics.ClipFailed", error);
            await Task.Run(() => File.Move(workPath, outputPath, overwrite: true));
            return L("ShareLyrics.Saved");
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
            // Moved into place on success; whatever is left is unfinished.
            await DeletePartialAsync(workPath);
            if (ReferenceEquals(_exportCts, cts)) _exportCts = null;
            cts.Dispose();
            IsRendering = false;
        }
    }

    /// <summary>
    /// A frame-sequence clip (karaoke card, Lyrics Page): <paramref name="renderFrames"/> writes
    /// JPEGs into a temp frame folder on a worker while the footer counts the pass, then ffmpeg
    /// muxes them with the audio slice into <paramref name="workPath"/>. The frame folder is
    /// always deleted.
    /// </summary>
    private async Task<(bool ok, string error)> RenderFrameClipAsync(string ffmpeg, int fps, ShareClipTiming timing,
        string workPath, CancellationTokenSource cts, Action<string, Action<int, int>, CancellationToken> renderFrames,
        double fadeSeconds)
    {
        var token = cts.Token;
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

            await Task.Run(() => renderFrames(frameDir,
                (done, total) => ((IProgress<(int, int)>)progress).Report((done, total)), token), token);

            StatusText = L("ShareLyrics.Encoding");
            var pattern = Path.Combine(frameDir, "frame-%05d.jpg");
            return await ShareClipRenderer.RenderFramesAsync(
                ffmpeg, pattern, fps, _track.FilePath, workPath, timing, token, fadeSeconds);
        }
        finally
        {
            try { Directory.Delete(frameDir, true); } catch { /* best effort */ }
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

    /// <summary>Cancelled when the dialog closes (<see cref="Detach"/>): stops a still render.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Unsubscribes from the player so the dialog can be garbage-collected.</summary>
    public void Detach()
    {
        // A preview render still running disposes what it made (and the cover) when it lands.
        _detached = true;
        _lifetime.Cancel();
        if (!_previewRendering) { _previewArt?.Dispose(); _previewArt = null; }

        // Stop a running export. Detach() previously tore down only the preview animator,
        // so closing the dialog mid-export left thousands of frames still rendering.
        CancelExport();
        // Stop listening first: putting a blanked line back (EndEdit) re-rendered the card
        // after close, and with word timings re-armed the karaoke timer for a closed dialog.
        foreach (var line in Lines)
        {
            line.PropertyChanged -= OnLineChanged;
            line.EndEdit(keep: true);
        }

        TeardownAnimator();
        if (_player != null)
            _player.PropertyChanged -= OnPlayerPropertyChanged;

        // The readability slider is saved once, on close, not at every step of a drag.
        if (PageBackgroundStrength != _savedPageStrength)
        {
            int strength = PageBackgroundStrength;
            _savedPageStrength = strength;
            _ = SavePageSettingsAsync(s => s.ShareLyricsPageBackgroundStrength = strength);
        }
    }
}
