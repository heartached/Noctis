using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>A background-color choice in the lyrics-page picker. <see cref="Preview"/> is
/// settable so the "Auto" swatch can show the current track's artwork-derived color.</summary>
public partial class ColorSwatch : ObservableObject
{
    public ColorSwatch(string key, string name, IBrush preview, bool isAuto = false)
    {
        Key = key;
        Name = name;
        _preview = preview;
        IsAuto = isAuto;
    }

    public string Key { get; }
    public string Name { get; }
    public bool IsAuto { get; }

    [ObservableProperty] private IBrush _preview;

    /// <summary>The choice the background shows now (the picker rings it).</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// ViewModel for the Lyrics view that displays synchronized lyrics
/// alongside album art and playback controls.
/// Supports: embedded lyrics, .lrc/.ttml files with timestamp syncing.
/// </summary>
public partial class LyricsViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILrcLibService _lrcLib;
    private readonly INetEaseService _netEase;
    private readonly IMetadataService _metadata;
    private readonly IPersistenceService _persistence;
    private readonly ILibraryService _library;
    private string? _selectedColorHex;
    private CancellationTokenSource? _statusClearCts;
    private readonly EventHandler? _accentHandler;

    [ObservableProperty] private bool _isColorModeArtwork = true;
    [ObservableProperty] private bool _isColorModeSolid;
    [ObservableProperty] private bool _isColorModeGradient;
    [ObservableProperty] private string _activeSwatchKey = "";

    /// <summary>"Auto" swatches, first in each grid. Their previews track the current track's
    /// artwork: the vibrant color Solid·Auto shows and the gradient Gradient·Auto shows.</summary>
    private readonly ColorSwatch _autoSwatch =
        new("", "Auto", new SolidColorBrush(DefaultAdaptiveColor), isAuto: true);
    private readonly ColorSwatch _autoGradientSwatch =
        new("", "Auto", CreateDefaultUnifiedBrush(), isAuto: true);

    public IReadOnlyList<ColorSwatch> SolidSwatches { get; }
    public IReadOnlyList<ColorSwatch> GradientSwatches { get; }

    partial void OnActiveSwatchKeyChanged(string value) => UpdateSwatchSelection();

    private void UpdateSwatchSelection()
    {
        foreach (var swatch in SolidSwatches.Concat(GradientSwatches))
            swatch.IsSelected = swatch.Key == ActiveSwatchKey;
    }

    private static List<ColorSwatch> BuildSolidSwatches()
    {
        return new List<ColorSwatch>
        {
            // Dark tones
            new("#1A1A2E", "Deep Navy", new SolidColorBrush(Color.Parse("#1A1A2E"))),
            new("#2D1B36", "Dark Plum", new SolidColorBrush(Color.Parse("#2D1B36"))),
            new("#0D2137", "Ink Blue", new SolidColorBrush(Color.Parse("#0D2137"))),
            new("#1B2D2A", "Forest", new SolidColorBrush(Color.Parse("#1B2D2A"))),
            new("#040404", "Midnight", new SolidColorBrush(Color.Parse("#040404"))),
            new("#3A1C3F", "Velvet", new SolidColorBrush(Color.Parse("#3A1C3F"))),
            new("#2C1810", "Espresso", new SolidColorBrush(Color.Parse("#2C1810"))),
            new("#1A0A2E", "Indigo Night", new SolidColorBrush(Color.Parse("#1A0A2E"))),
            new("#0A1628", "Obsidian", new SolidColorBrush(Color.Parse("#0A1628"))),
            new("#2A1A1A", "Dark Cherry", new SolidColorBrush(Color.Parse("#2A1A1A"))),
            // Mid tones
            new("#4A3728", "Mocha", new SolidColorBrush(Color.Parse("#4A3728"))),
            new("#8B4513", "Saddle", new SolidColorBrush(Color.Parse("#8B4513"))),
            new("#7C7C7C", "Slate", new SolidColorBrush(Color.Parse("#7C7C7C"))),
            new("#6B8E9B", "Storm", new SolidColorBrush(Color.Parse("#6B8E9B"))),
            new("#5C8A6E", "Sage", new SolidColorBrush(Color.Parse("#5C8A6E"))),
            new("#9B7CB8", "Lavender", new SolidColorBrush(Color.Parse("#9B7CB8"))),
            new("#C9B458", "Antique Gold", new SolidColorBrush(Color.Parse("#C9B458"))),
            new("#7B6D8D", "Amethyst", new SolidColorBrush(Color.Parse("#7B6D8D"))),
            new("#5B7065", "Eucalyptus", new SolidColorBrush(Color.Parse("#5B7065"))),
            new("#8C6E5D", "Clay", new SolidColorBrush(Color.Parse("#8C6E5D"))),
            new("#4D6A8F", "Denim", new SolidColorBrush(Color.Parse("#4D6A8F"))),
            new("#B35A5A", "Brick", new SolidColorBrush(Color.Parse("#B35A5A"))),
            // Light tones
            new("#ABC1D8", "Cool Blue", new SolidColorBrush(Color.Parse("#ABC1D8"))),
            new("#F7C8B1", "Peach", new SolidColorBrush(Color.Parse("#F7C8B1"))),
            new("#E4ECF4", "Frost", new SolidColorBrush(Color.Parse("#E4ECF4"))),
            new("#EF797E", "Coral", new SolidColorBrush(Color.Parse("#EF797E"))),
            new("#B4E4AC", "Mint", new SolidColorBrush(Color.Parse("#B4E4AC"))),
            new("#D4A0A0", "Dusty Rose", new SolidColorBrush(Color.Parse("#D4A0A0"))),
            new("#E8C8A0", "Champagne", new SolidColorBrush(Color.Parse("#E8C8A0"))),
            new("#A8D8EA", "Sky", new SolidColorBrush(Color.Parse("#A8D8EA"))),
            new("#D4B8E0", "Lilac", new SolidColorBrush(Color.Parse("#D4B8E0"))),
            new("#F5E6CC", "Cream", new SolidColorBrush(Color.Parse("#F5E6CC"))),
        };
    }

    private static LinearGradientBrush MakePreviewGradient(string hex1, string hex2)
    {
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse(hex1), 0),
                new GradientStop(Color.Parse(hex2), 1),
            }
        };
    }

    private static List<ColorSwatch> BuildGradientSwatches()
    {
        return new List<ColorSwatch>
        {
            // Dark atmospheric
            new("grad:#6A0572,#1A1A2E", "Purple Night", MakePreviewGradient("#6A0572", "#1A1A2E")),
            new("grad:#0F2027,#2C5364", "Deep Sea", MakePreviewGradient("#0F2027", "#2C5364")),
            new("grad:#232526,#414345", "Charcoal", MakePreviewGradient("#232526", "#414345")),
            new("grad:#1A0530,#3A1C71", "Cosmic", MakePreviewGradient("#1A0530", "#3A1C71")),
            new("grad:#0C0C1D,#1B3A4B", "Abyss", MakePreviewGradient("#0C0C1D", "#1B3A4B")),
            new("grad:#2C1810,#5C3D2E", "Bourbon", MakePreviewGradient("#2C1810", "#5C3D2E")),
            new("grad:#141E30,#243B55", "Royal Blue", MakePreviewGradient("#141E30", "#243B55")),
            new("grad:#0F0C29,#302B63", "Midnight Indigo", MakePreviewGradient("#0F0C29", "#302B63")),
            new("grad:#1F1C2C,#928DAB", "Misty Violet", MakePreviewGradient("#1F1C2C", "#928DAB")),
            new("grad:#2B1B17,#6D4C41", "Dark Amber", MakePreviewGradient("#2B1B17", "#6D4C41")),
            // Vibrant
            new("grad:#3A1C3F,#D4145A", "Berry Crush", MakePreviewGradient("#3A1C3F", "#D4145A")),
            new("grad:#0B486B,#F56217", "Sunset Ocean", MakePreviewGradient("#0B486B", "#F56217")),
            new("grad:#4B134F,#C94B4B", "Magenta Fire", MakePreviewGradient("#4B134F", "#C94B4B")),
            new("grad:#134E5E,#71B280", "Emerald Dusk", MakePreviewGradient("#134E5E", "#71B280")),
            new("grad:#0D324D,#7F5A83", "Twilight", MakePreviewGradient("#0D324D", "#7F5A83")),
            new("grad:#1D2B64,#F8CDDA", "Dawn", MakePreviewGradient("#1D2B64", "#F8CDDA")),
            new("grad:#642B73,#C6426E", "Orchid", MakePreviewGradient("#642B73", "#C6426E")),
            new("grad:#373B44,#4286F4", "Steel Blue", MakePreviewGradient("#373B44", "#4286F4")),
            new("grad:#1A2A3A,#E74856", "Red Horizon", MakePreviewGradient("#1A2A3A", "#E74856")),
            new("grad:#0B3D0B,#2E8B57", "Deep Forest", MakePreviewGradient("#0B3D0B", "#2E8B57")),
            // New additions
            new("grad:#0D0D0D,#4A0E4E", "Void Purple", MakePreviewGradient("#0D0D0D", "#4A0E4E")),
            new("grad:#1A1A2E,#E94560", "Neon Rose", MakePreviewGradient("#1A1A2E", "#E94560")),
            new("grad:#16222A,#3A6073", "Arctic Teal", MakePreviewGradient("#16222A", "#3A6073")),
            new("grad:#2C3E50,#FD746C", "Warm Dusk", MakePreviewGradient("#2C3E50", "#FD746C")),
            new("grad:#0F2027,#B29F7D", "Desert Night", MakePreviewGradient("#0F2027", "#B29F7D")),
            new("grad:#200122,#6F0000", "Blood Moon", MakePreviewGradient("#200122", "#6F0000")),
            new("grad:#1B1B3A,#08D9D6", "Cyber", MakePreviewGradient("#1B1B3A", "#08D9D6")),
            new("grad:#2D1B69,#F97316", "Electric Sunset", MakePreviewGradient("#2D1B69", "#F97316")),
            new("grad:#0A2E36,#61892F", "Moss", MakePreviewGradient("#0A2E36", "#61892F")),
            new("grad:#2E1437,#C850C0", "Fuchsia Haze", MakePreviewGradient("#2E1437", "#C850C0")),
        };
    }
    /// <summary>Line cursor + word-layer driver (Core); the ViewModel keeps the UI side:
    /// opacities, the word-clock frame subscription, the trace.</summary>
    private readonly LyricsTimeline _timeline;
    private bool _hasSyncedLyrics;
    private Track? _currentTrack;
    private string _loadedLyrics = string.Empty;
    private string _loadedSyncedLyrics = string.Empty;
    private LrcLibResult? _currentOnlineResult;
    private LrcLibResult? _alternateOnlineResult;
    private string? _alternateSource;
    private int _searchGeneration;

    /// <summary>Online-fetched lyrics ({track id}.lrc / .lyricsfile). Internal: the Noctis server
    /// serves the .lrc to a signed-in phone when a song has no stored lyrics.</summary>
    internal static readonly string LyricsCacheDir = Path.Combine(
        Helpers.AppPaths.DataRoot, "lyrics_cache");

    private static readonly Color DefaultAdaptiveColor = Color.FromRgb(0x0D, 0x1B, 0x2A);

    // Dedicated lyrics sync timer — bypasses the fragile PropertyChanged chain.
    // Runs at a fixed 100ms cadence for line-level sync. Word-level sweep smoothness
    // does NOT come from this timer: while the active line has word timings we
    // subscribe to Avalonia's global animation clock (one tick per rendered frame)
    // so the karaoke sweep is frame-synced instead of stepping every 33ms.
    private readonly DispatcherTimer _lyricsSyncTimer;

    private const int LineSyncIntervalMs = 100;

    // ── Rate-locked playback clock ──
    // Raw Position reaches us in coarse steps (audio-layer blocks → 100ms poll →
    // dispatcher), far too chunky for the word-level colour sweep. The clock runs
    // continuously and bends its rate toward the raw source instead of re-anchoring
    // (which froze or jumped the sweep at every poll). See LyricsPlaybackClock.
    private readonly LyricsPlaybackClock _clock = new();

    // True while the RequestAnimationFrame loop driving the word sweep is running.
    // Managed by UpdateWordClockSubscription() / OnWordClockFrame().
    private bool _wordClockRunning;

    // Monotonic line cursor — avoids re-scanning every tick.

    public PlayerViewModel Player => _player;

    /// <summary>Id of the track whose lyrics <see cref="LyricLines"/> currently hold (the
    /// Local API uses it to avoid serving the previous track's lyrics mid-load).</summary>
    internal Guid? LoadedTrackId => _currentTrack?.Id;

    /// <summary>Lyrics lines for the current track.</summary>
    public BulkObservableCollection<LyricLine> LyricLines { get; } = new();

    /// <summary>Whether the current lyrics have timestamp sync.</summary>
    [ObservableProperty]
    private bool _isSynced;

    /// <summary>Whether the Synchronized tab is selected.</summary>
    [ObservableProperty]
    private bool _isSyncTabSelected = true;

    /// <summary>Whether the Unsynchronized tab is selected.</summary>
    [ObservableProperty]
    private bool _isUnsyncTabSelected;

    /// <summary>Whether synced lyrics are available (controls Sync tab visibility).</summary>
    [ObservableProperty]
    private bool _hasSyncedLyricsAvailable;

    /// <summary>Set by the shell — true only while the lyrics PAGE is up in a fullscreen
    /// window; drives the focus dimming gate in <see cref="UpdateLineOpacities"/>.</summary>
    [ObservableProperty]
    private bool _isFullScreenPageActive;

    partial void OnIsFullScreenPageActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLyricsFocusActive));
        RefreshFocusDimming();
    }

    /// <summary>True while the opt-in fullscreen focus dimming is in effect (fullscreen
    /// lyrics page + Appearance toggle). The page view also anchors the active line
    /// near center while this is on (FocusAnchorRatio), since the dimmed-away lines
    /// leave the lower viewport empty at the page's default anchor.</summary>
    public bool IsLyricsFocusActive => IsFullScreenPageActive && Player.LyricsFullScreenFocusEnabled;

    /// <summary>Settings toggles for the TTML translation / romanization rows and the
    /// background-vocal row (issue #78). Display-only: the line templates AND these with
    /// each line's own flags, so flipping one never reloads the lyrics.</summary>
    public bool ShowTranslations => Player.LyricsShowTranslations;
    public bool ShowRomanization => Player.LyricsShowRomanization;
    public bool ShowBackgroundVocals => Player.LyricsShowBackgroundVocals;

    /// <summary>Plain text lyrics without timestamps for the Unsync tab.</summary>
    public BulkObservableCollection<LyricLine> UnsyncedLines { get; } = new();

    /// <summary>
    /// True when the loaded lyrics carry duet voice markers (any non-default
    /// <see cref="LyricLine.Voice"/>). The page and panel bind this to force the
    /// lyric column to full width so voice-2 lines have a stable right edge.
    /// </summary>
    [ObservableProperty]
    private bool _hasDuetLines;

    /// <summary>Lines bound to the lyrics page — synced or plain depending on the toggle.</summary>
    public IEnumerable<LyricLine> ActiveLyricLines =>
        IsSyncTabSelected ? (IEnumerable<LyricLine>)LyricLines : UnsyncedLines;

    partial void OnIsSyncTabSelectedChanged(bool value) =>
        OnPropertyChanged(nameof(ActiveLyricLines));

    [RelayCommand]
    private void ToggleLyricsMode()
    {
        if (!HasSyncedLyricsAvailable) return;
        IsSyncTabSelected = !IsSyncTabSelected;
        IsUnsyncTabSelected = !IsSyncTabSelected;
    }

    [RelayCommand]
    private void SelectSyncedLyrics()
    {
        if (!HasSyncedLyricsAvailable) return;
        IsSyncTabSelected = true;
        IsUnsyncTabSelected = false;
    }

    [RelayCommand]
    private void SelectPlainLyrics()
    {
        IsSyncTabSelected = false;
        IsUnsyncTabSelected = true;
    }

    [RelayCommand]
    private void OpenBackgroundColorPicker()
    {
        OpenBackgroundColorRequested?.Invoke();
    }

    /// <summary>Index of the currently active lyric line (for auto-scroll).</summary>
    [ObservableProperty]
    private int _activeLineIndex = -1;

    /// <summary>The line the lyric list glides to: <see cref="ActiveLineIndex"/>, the next
    /// line up to <see cref="Helpers.LineMotion.ScrollLeadMs"/> early so the list has landed
    /// when its first word starts, or an earlier line still being sung over it (duets) —
    /// LyricsTimeline.ScrollIndex. The views scroll on this; the highlight and sweep stay
    /// on ActiveLineIndex.</summary>
    [ObservableProperty]
    private int _scrollLineIndex = -1;

    // Every reset/seek of the active line resets the scroll target with it; the early
    // lead is re-derived by the next UpdateActiveLine.
    partial void OnActiveLineIndexChanged(int value) => ScrollLineIndex = value;

    // Apple Music brightens and un-blurs the incoming line as the list sets off toward
    // it, not when its first word starts — measured: incoming sharpness and brightness
    // follow the glide (half-way ~160 ms in), the outgoing line dims within ~100 ms. So
    // the focus ramp moves with the early glide; the sweep stays on activation. A lead
    // dropped with the line unchanged (a seek back within it) brings the focus back.
    partial void OnScrollLineIndexChanged(int oldValue, int newValue)
    {
        if (ActiveLineIndex >= 0 && _hasSyncedLyrics && IsSyncTabSelected
            && (newValue == ActiveLineIndex + 1 || oldValue == ActiveLineIndex + 1 && newValue == ActiveLineIndex))
            UpdateLineOpacities(ActiveLineIndex);
    }

    /// <summary>Whether to show favorite heart in metadata row (reflects current track's favorite status).</summary>
    public bool ShowMetadataFavoriteHeart => Player?.CurrentTrack?.IsFavorite ?? false;

    /// <summary>Adaptive gradient brush for the left panel (darker tint).</summary>
    [ObservableProperty]
    private IBrush _leftPanelBrush = CreateDefaultGradient();

    /// <summary>Adaptive gradient brush for the right/lyrics panel (subdued).</summary>
    [ObservableProperty]
    private IBrush _lyricsBackgroundBrush = CreateDefaultSubduedGradient();

    /// <summary>Unified horizontal gradient spanning both panels — removes the hard seam.</summary>
    [ObservableProperty]
    private IBrush _fullBackgroundBrush = CreateDefaultUnifiedBrush();

    // ── Fluid mesh colours (AMLL-style animated background blobs) ──

    [ObservableProperty] private Color _meshBaseColor = DefaultAdaptiveColor;
    [ObservableProperty] private Color _meshBlobColor1 = Color.FromRgb(0x3A, 0x1C, 0x71);
    [ObservableProperty] private Color _meshBlobColor2 = Color.FromRgb(0xD7, 0x6D, 0x77);
    [ObservableProperty] private Color _meshBlobColor3 = Color.FromRgb(0xFF, 0xAF, 0x7B);

    private void UpdateMeshColors(Color dominant, Color secondary)
    {
        MeshBaseColor = Darken(dominant, 0.55);
        MeshBlobColor1 = dominant;
        MeshBlobColor2 = secondary;
        MeshBlobColor3 = ShiftHue(dominant, 35);
    }

    private static Color Darken(Color c, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        return Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    }

    private static Color ShiftHue(Color c, double degrees)
    {
        // RGB → HSV → shift H → RGB.
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double v = max;
        double d = max - min;
        double s = max <= 0 ? 0 : d / max;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        h = (h + degrees) % 360;
        if (h < 0) h += 360;

        double c2 = v * s;
        double x = c2 * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c2;
        double rr = 0, gg = 0, bb = 0;
        if (h < 60) { rr = c2; gg = x; }
        else if (h < 120) { rr = x; gg = c2; }
        else if (h < 180) { gg = c2; bb = x; }
        else if (h < 240) { gg = x; bb = c2; }
        else if (h < 300) { rr = x; bb = c2; }
        else { rr = c2; bb = x; }
        return Color.FromRgb((byte)((rr + m) * 255), (byte)((gg + m) * 255), (byte)((bb + m) * 255));
    }

    // ── Adaptive foreground colors (react to background luminance) ──

    // Cached per-branch brushes. UpdateForegroundsForBackground runs on every track
    // change; fresh instances made every colour binding on every word control
    // re-evaluate even when the branch didn't change (the setters skip same-instance
    // sets). Immutable, like every other shared brush: a SolidColorBrush belongs to the
    // thread that made it, and these were made by whichever thread first touched this
    // class. A plain test on the thread pool reading LyricsCacheDir got there first on some
    // CI runs, and every lyrics page after it threw "a different thread owns it" reading
    // or drawing them. Never mutated; LyricsView.SetResourceBrush copies their colour.
    private static ImmutableSolidColorBrush Solid(string hex) => new(Color.Parse(hex));
    private static readonly ImmutableSolidColorBrush LightPrimaryFg = Solid("#111111");
    private static readonly ImmutableSolidColorBrush LightSecondaryFg = Solid("#55111111");
    private static readonly ImmutableSolidColorBrush LightSubtleFg = Solid("#555555");
    private static readonly ImmutableSolidColorBrush LightSliderFilled = Solid("#CC111111");
    private static readonly ImmutableSolidColorBrush LightSliderUnfilled = Solid("#33111111");
    private static readonly ImmutableSolidColorBrush LightControlFill = Solid("#222222");
    private static readonly ImmutableSolidColorBrush LightBtnBg = Solid("#22000000");
    private static readonly ImmutableSolidColorBrush LightBtnBgHover = Solid("#33000000");
    private static readonly ImmutableSolidColorBrush LightSliderThumb = Solid("#DD111111");
    private static readonly ImmutableSolidColorBrush MediumSecondaryFg = Solid("#DDFFFFFF");
    private static readonly ImmutableSolidColorBrush MediumSubtleFg = Solid("#CCCCCC");
    private static readonly ImmutableSolidColorBrush MediumSliderFilled = Solid("#EEFFFFFF");
    private static readonly ImmutableSolidColorBrush MediumSliderUnfilled = Solid("#44FFFFFF");
    private static readonly ImmutableSolidColorBrush MediumBtnBg = Solid("#44000000");
    private static readonly ImmutableSolidColorBrush MediumBtnBgHover = Solid("#55000000");
    private static readonly ImmutableSolidColorBrush MediumSliderThumb = Solid("#FFFFFFFF");
    private static readonly ImmutableSolidColorBrush DarkSecondaryFg = Solid("#B0FFFFFF");
    private static readonly ImmutableSolidColorBrush DarkSubtleFg = Solid("#999999");
    private static readonly ImmutableSolidColorBrush DarkSliderFilled = Solid("#CCFFFFFF");
    private static readonly ImmutableSolidColorBrush DarkSliderUnfilled = Solid("#33FFFFFF");
    private static readonly ImmutableSolidColorBrush DarkBtnBg = Solid("#33FFFFFF");
    private static readonly ImmutableSolidColorBrush DarkBtnBgHover = Solid("#55FFFFFF");
    private static readonly ImmutableSolidColorBrush DarkSliderThumb = Solid("#EEFFFFFF");

    [ObservableProperty] private IBrush _lyricsPrimaryFg = Brushes.White;
    [ObservableProperty] private IBrush _lyricsSecondaryFg = new SolidColorBrush(Color.Parse("#B0FFFFFF"));
    [ObservableProperty] private IBrush _lyricsAccentFg = ResolveAccentBrush();
    [ObservableProperty] private IBrush _lyricsSubtleFg = new SolidColorBrush(Color.Parse("#999999"));
    [ObservableProperty] private IBrush _lyricsSliderFilled = new SolidColorBrush(Color.Parse("#CCFFFFFF"));
    [ObservableProperty] private IBrush _lyricsSliderUnfilled = new SolidColorBrush(Color.Parse("#33FFFFFF"));
    [ObservableProperty] private IBrush _lyricsControlFill = Brushes.White;
    [ObservableProperty] private IBrush _lyricsBtnBg = new SolidColorBrush(Color.Parse("#33FFFFFF"));
    [ObservableProperty] private IBrush _lyricsBtnBgHover = new SolidColorBrush(Color.Parse("#55FFFFFF"));
    [ObservableProperty] private IBrush _lyricsSliderThumb = new SolidColorBrush(Color.Parse("#EEFFFFFF"));

    private void UpdateForegroundsForBackground(IBrush bg)
    {
        Color bgColor;
        if (bg is SolidColorBrush scb)
            bgColor = scb.Color;
        else if (bg is LinearGradientBrush lgb && lgb.GradientStops.Count > 0)
        {
            // Average the gradient stops for luminance check
            double avgR = 0, avgG = 0, avgB = 0;
            foreach (var stop in lgb.GradientStops)
            {
                avgR += stop.Color.R;
                avgG += stop.Color.G;
                avgB += stop.Color.B;
            }
            int count = lgb.GradientStops.Count;
            bgColor = Color.FromRgb((byte)(avgR / count), (byte)(avgG / count), (byte)(avgB / count));
        }
        else
            return;

        // Relative luminance (ITU-R BT.709)
        double lum = (0.2126 * bgColor.R + 0.7152 * bgColor.G + 0.0722 * bgColor.B) / 255.0;

        if (lum > 0.65) // Light background
        {
            LyricsPrimaryFg = LightPrimaryFg;
            LyricsSecondaryFg = LightSecondaryFg;
            LyricsAccentFg = ResolveAccentBrush("AccentColorBrushDark1");
            LyricsSubtleFg = LightSubtleFg;
            LyricsSliderFilled = LightSliderFilled;
            LyricsSliderUnfilled = LightSliderUnfilled;
            LyricsControlFill = LightControlFill;
            LyricsBtnBg = LightBtnBg;
            LyricsBtnBgHover = LightBtnBgHover;
            LyricsSliderThumb = LightSliderThumb;
        }
        else if (lum > 0.35) // Medium background — boost contrast
        {
            LyricsPrimaryFg = Brushes.White;
            LyricsSecondaryFg = MediumSecondaryFg;
            LyricsAccentFg = ResolveAccentBrush("AccentColorBrushLight1");
            LyricsSubtleFg = MediumSubtleFg;
            LyricsSliderFilled = MediumSliderFilled;
            LyricsSliderUnfilled = MediumSliderUnfilled;
            LyricsControlFill = Brushes.White;
            LyricsBtnBg = MediumBtnBg;
            LyricsBtnBgHover = MediumBtnBgHover;
            LyricsSliderThumb = MediumSliderThumb;
        }
        else // Dark background
        {
            LyricsPrimaryFg = Brushes.White;
            LyricsSecondaryFg = DarkSecondaryFg;
            LyricsAccentFg = ResolveAccentBrush();
            LyricsSubtleFg = DarkSubtleFg;
            LyricsSliderFilled = DarkSliderFilled;
            LyricsSliderUnfilled = DarkSliderUnfilled;
            LyricsControlFill = Brushes.White;
            LyricsBtnBg = DarkBtnBg;
            LyricsBtnBgHover = DarkBtnBgHover;
            LyricsSliderThumb = DarkSliderThumb;
        }
    }

    /// <summary>
    /// Re-resolves the accent foreground brushes against the current background luminance.
    /// Called whenever the global accent colour changes so the lyrics page recolours live.
    /// </summary>
    private void RefreshAccentForegrounds()
    {
        // Route through RefreshLyricsForegrounds so the choice respects the current
        // mode (artwork uses visible-blurred luminance, color modes use FullBackgroundBrush).
        RefreshLyricsForegrounds();
    }

    private static IBrush ResolveAccentBrush(string key = "AccentColorBrush")
    {
        if (Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var b) == true && b is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse("#E74856"));
    }

    private void ResetForegroundsToDefault()
    {
        LyricsPrimaryFg = Brushes.White;
        LyricsSecondaryFg = DarkSecondaryFg;
        LyricsAccentFg = ResolveAccentBrush();
        LyricsSubtleFg = DarkSubtleFg;
        LyricsSliderFilled = DarkSliderFilled;
        LyricsSliderUnfilled = DarkSliderUnfilled;
        LyricsControlFill = Brushes.White;
        LyricsBtnBg = DarkBtnBg;
        LyricsBtnBgHover = DarkBtnBgHover;
        LyricsSliderThumb = DarkSliderThumb;
    }

    /// <summary>Whether the "Search Lyrics" button should be shown (no local lyrics found).</summary>
    [ObservableProperty]
    private bool _showSearchButton;

    /// <summary>Message shown above the Search Lyrics button after a failed search.</summary>
    [ObservableProperty]
    private string _searchFailedMessage = string.Empty;

    /// <summary>Whether a lyrics search is in progress.</summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>Whether online lyrics are currently displayed (enables "Save to File").</summary>
    [ObservableProperty]
    private bool _canSaveToFile;

    /// <summary>
    /// Whether "Save Lyrics to File ▸ TTML" is offered: the page shows time-synced lyrics of a
    /// local file — online ones (as for LRC), or the song's own from an .lrc/.elrc/.lyricsfile,
    /// its tags or the cache. Not for lyrics read from a .ttml (that file is already there),
    /// plain lyrics, or a Lyrics Studio preview.
    /// </summary>
    [ObservableProperty]
    private bool _canSaveTtmlToFile;

    /// <summary>The LRC/ELRC text of the song's own synced lyrics on the page, for a TTML save; null otherwise.</summary>
    private string? _localSyncedForSave;

    /// <summary>Whether lyrics can be removed (true for online-fetched or cached service lyrics).</summary>
    [ObservableProperty]
    private bool _canRemoveLyrics;

    /// <summary>GitHub #162: the song on the page is marked as having no lyrics — the page says
    /// so (with Undo) instead of offering a search, and nothing searches for it automatically.</summary>
    [ObservableProperty]
    private bool _isMarkedNoLyrics;

    partial void OnIsMarkedNoLyricsChanged(bool value)
    {
        // A song marked as having none has nothing to share (ShareLyrics returns on zero lines).
        OnPropertyChanged(nameof(ShareAvailable));
        OnPropertyChanged(nameof(NoLyricsSearchTip));
        UpdateNoLyricsCentered();
    }

    /// <summary>
    /// Tooltip of the Search Lyrics button the player island carries while the page is
    /// centred for a song with no lyrics (it took the place of the text row under the
    /// player): what the page knows about the song's lyrics, then what the button does.
    /// </summary>
    public string NoLyricsSearchTip
    {
        get
        {
            var search = Localization.Loc.T("Lyrics.SearchLyrics");
            if (IsSearching) return Localization.Loc.T("Lyrics.SearchingLyrics");
            if (IsMarkedNoLyrics) return Localization.Loc.T("Lyrics.MarkedNoLyrics") + "\n" + search;
            return string.IsNullOrEmpty(SearchFailedMessage) ? search : SearchFailedMessage + "\n" + search;
        }
    }

    partial void OnSearchFailedMessageChanged(string value) => OnPropertyChanged(nameof(NoLyricsSearchTip));

    /// <summary>
    /// The lyrics page centres the player (cover, track info, controls) instead of the
    /// split layout: Settings ▸ "Center the player when a song has no lyrics" is on and the
    /// song is KNOWN to have none — nothing found (instrumental included: that search ends
    /// on the Search Lyrics state too) or marked as having none. While lyrics are still
    /// loading (the local probe, an online search, a track change before its load lands)
    /// the layout holds whatever it was, so a load never bounces the page between the two
    /// layouts; lines arriving switch it back to split.
    /// </summary>
    [ObservableProperty]
    private bool _isNoLyricsCentered;

    /// <summary>Set while a no-lyrics result may start the automatic download straight away:
    /// that search is still loading, so the page must not centre for the instant between.</summary>
    private bool _holdNoLyricsCentered;

    internal void UpdateNoLyricsCentered()
    {
        if (_holdNoLyricsCentered) return;
        if (!_player.LyricsCenterWhenNoLyrics || _player.CurrentTrack == null)
            IsNoLyricsCentered = false;
        else if (ShowSearchButton || IsMarkedNoLyrics)
            IsNoLyricsCentered = true;
        else if (LyricLines.Count > 0 || UnsyncedLines.Count > 0)
            IsNoLyricsCentered = false;
        // Otherwise still loading: hold.
    }

    /// <summary>Status text for save operation feedback.</summary>
    [ObservableProperty]
    private string _saveStatusText = string.Empty;

    /// <summary>Whether auto-follow has been paused by user manual scroll.</summary>
    [ObservableProperty]
    private bool _isAutoFollowPaused;

    /// <summary>Name of the lyrics source currently displayed (e.g. "LRCLIB", "NetEase", "Local").</summary>
    [ObservableProperty]
    private string _lyricsSourceName = string.Empty;

    /// <summary>"Written By: …" credit shown after the last lyric line: the current track's
    /// composer(s), comma-joined. Empty when the track has no composer tag or no lyrics are
    /// displayed — the views collapse the footer then. Kept in lock-step with the line
    /// collections (see <see cref="UpdateWrittenByCredit"/>), so it swaps behind the same
    /// fade as the lyrics themselves.</summary>
    [ObservableProperty]
    private string _writtenByText = string.Empty;

    /// <summary>Whether an alternate lyrics source is available to switch to.</summary>
    [ObservableProperty]
    private bool _hasAlternateLyrics;

    /// <summary>Label for the alternate lyrics button (e.g. "Try NetEase", "Try LRCLIB").</summary>
    [ObservableProperty]
    private string _alternateLyricsLabel = string.Empty;

    private Action<string>? _viewArtistAction;
    private Action<Track>? _viewAlbumAction;

    /// <summary>
    /// Raised when the user requests the background color picker to open from outside the
    /// lyrics view's own bar (e.g. from the standard PlaybackBar's ⋯ menu).
    /// The lyrics view's code-behind subscribes and opens LyricsBackgroundColorDialog over its window.
    /// </summary>
    public event Action? OpenBackgroundColorRequested;

    /// <summary>Lyrics providers of running plugins (set by MainWindowViewModel); read per search.</summary>
    internal Func<IReadOnlyList<Services.Plugins.PluginLyricsSource>>? PluginLyricsSources { get; set; }

    /// <summary>Built-in sources past LRCLIB and NetEase — Musixmatch, Kugou, YouTube Music (issue #113),
    /// in priority order. Set by MainWindowViewModel; each is searched when its Settings switch is on.</summary>
    internal IReadOnlyList<Services.Lyrics.OnlineLyricsSource> ExtraLyricsSources { get; set; } =
        Array.Empty<Services.Lyrics.OnlineLyricsSource>();

    /// <summary>Longest an extra source may take before it counts as "could not answer".</summary>
    internal TimeSpan ExtraSourceTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long an automatic search keeps waiting on the extra sources once LRCLIB / NetEase
    /// have answered with something less than word-synced. They run alongside from the start;
    /// this only bounds the wait for a richer format, so a slow source can't hold lyrics back.
    /// </summary>
    internal TimeSpan ExtraSourceGrace { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Opens the Search Lyrics (source picker) dialog; set by MainWindowViewModel.</summary>
    internal Func<LyricsSearchViewModel, Task>? ShowLyricsSearchDialog { get; set; }

    public LyricsViewModel(PlayerViewModel player, ILrcLibService lrcLib, INetEaseService netEase, IMetadataService metadata, IPersistenceService persistence, ILibraryService library)
    {
        _timeline = new LyricsTimeline(LyricLines, WordLookahead, LyricsLookahead);
        // First handler on the collection, ahead of every view's: a long document's lines are
        // set to rest before any view builds their word layers (see WordLayerWindowMinWords).
        LyricLines.CollectionChanged += (_, _) => OnLyricLinesChangedForWordLayers();
        // Plugin API 1.5: a lyrics effect says which lines read right to left.
        LyricLines.CollectionChanged += (_, _) => ApplyLineDirections();
        Controls.LyricsWordEffect.Changed += OnLyricsWordEffectChanged;
        _player = player;
        _timeline.WordMotion = player.LyricsGlowAndLift;
        _lrcLib = lrcLib;
        _netEase = netEase;
        _metadata = metadata;
        _persistence = persistence;
        _library = library;

        // "Auto" first, then the fixed swatches. Built per view model: each one carries
        // its own selected state.
        SolidSwatches = BuildSolidSwatches().Prepend(_autoSwatch).ToList();
        GradientSwatches = BuildGradientSwatches().Prepend(_autoGradientSwatch).ToList();
        UpdateSwatchSelection();

        // Dedicated sync timer — polls player position and drives line highlighting.
        // Fixed 100ms cadence; word-level sweep is frame-driven via the render-clock
        // subscription (see UpdateWordClockSubscription), for which this tick doubles
        // as the self-healing re-subscribe check after pause/resume or timer restarts.
        _lyricsSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs) };
        _lyricsSyncTimer.Tick += (_, _) =>
        {
            // Visibility-gate backstop: several paths (TrackStarted, play/pause,
            // library updates, online-lyrics apply) call Start() unconditionally.
            // With no lyrics surface on screen there is nothing to drive, so park
            // the timer; SetLyricsSurfaceVisible restarts it when a surface opens,
            // and while parked the Position fallback in OnPlayerPropertyChanged
            // keeps ActiveLineIndex fresh for the reopen jump.
            if (!IsAnyLyricsSurfaceVisible)
            {
                _lyricsSyncTimer.Stop();
                return;
            }
            if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
                UpdateActiveLine(GetPlaybackPosition());
            UpdateWordClockSubscription();
        };

        // The Written-By credit must appear exactly when lyrics do: every load/clear
        // path mutates these collections (Clear and ReplaceAll both raise
        // CollectionChanged), so one hook covers local probes, online results,
        // removals, search failures and the queue-end clear.
        LyricLines.CollectionChanged += (_, _) => UpdateWrittenByCredit();
        UnsyncedLines.CollectionChanged += (_, _) => UpdateWrittenByCredit();

        // Duet files force the lyric column to its full MaxWidth (view-side MinWidth
        // binding) so voice-2 lines right-align against a stable edge; single-voice
        // files keep today's content-hugging layout untouched.
        LyricLines.CollectionChanged += (_, _) =>
            HasDuetLines = LyricLines.Any(l => l.Voice != LyricVoice.Default);

        // Lines arriving is what ends a centred no-lyrics layout (see UpdateNoLyricsCentered).
        LyricLines.CollectionChanged += (_, _) => UpdateNoLyricsCentered();
        UnsyncedLines.CollectionChanged += (_, _) => UpdateNoLyricsCentered();

        // Subscribe to track changes to update lyrics
        _player.TrackStarted += OnTrackStarted;

        // Reload lyrics when metadata is edited (e.g. synced lyrics toggled off)
        _library.LibraryUpdated += OnLibraryUpdated;
        RefreshHeaderCredits();

        // Subscribe to state changes to start/stop the sync timer
        _player.PropertyChanged += OnPlayerPropertyChanged;

        // A committed seek — timeline release, Previous-restart, any surface — is an
        // explicit "go here", the same intent as clicking a lyric line (see SeekToLine):
        // browsing is over, so resume following the new position. Without this, a seek
        // during the wheel-scroll pause window left the view parked while every visible
        // line dimmed to opacity 0 (outside the ±9 window around the new active line).
        // The views re-anchor on the resulting IsAutoFollowPaused change.
        _player.Seeked += (_, _) => IsAutoFollowPaused = false;

        // React to accent colour changes so the artist/album text recolours live.
        _accentHandler = (_, _) => Dispatcher.UIThread.Post(RefreshAccentForegrounds);
        App.AccentApplied += _accentHandler;

        // Load lyrics for current track if one is playing
        if (_player.CurrentTrack != null)
        {
            LoadLyricsForTrack(_player.CurrentTrack);
            UpdateAdaptiveBackground(_player.AlbumArt);
            _player.CurrentTrack.PropertyChanged += OnCurrentTrackPropertyChanged;
            if (_hasSyncedLyrics && IsSyncTabSelected)
                _lyricsSyncTimer.Start();
        }

        // Load saved background color preference
        _ = LoadSavedBackgroundColorAsync();
    }

    private static LinearGradientBrush CreateDefaultGradient()
    {
        return DominantColorExtractor.CreateGradientFromColor(DefaultAdaptiveColor);
    }

    private static LinearGradientBrush CreateDefaultSubduedGradient()
    {
        var (_, right) = DominantColorExtractor.GenerateAdaptiveBrushes(DefaultAdaptiveColor);
        return right;
    }

    private static LinearGradientBrush CreateDefaultUnifiedBrush()
        => DominantColorExtractor.GenerateUnifiedBrush(DefaultAdaptiveColor);

    /// <summary>
    /// Extracts the dominant color from the current album art and updates
    /// both left and right panel brushes. Called on track change.
    /// </summary>
    /// <summary>Cached average color of the current album art. In artwork mode this is
    /// what's actually visible (heavily blurred → reads as the bitmap's average), so the
    /// foreground luminance check needs to fall back to this rather than the dominant
    /// brush, which can be a small accent that doesn't match the visible wash.</summary>
    private Color? _averageArtworkColor;

    /// <summary>The scrim painted over the blurred artwork in artwork mode
    /// (Rectangle Fill="#99000000" in LyricsView.axaml, LyricsPanelView.axaml and the mini
    /// player). Keep in sync if that changes — it shifts the visible luminance enough to
    /// matter for the readability threshold.</summary>
    private const double ArtworkScrimAlpha = 0x99 / 255.0;

    private int _adaptiveBackgroundGeneration;

    /// <summary>
    /// Track change / new art: the colours come from the cover FILE, analysed on a worker
    /// (SkiaSharp), then applied here on the UI thread. Analysing the Bitmap instead ran
    /// three RenderTargetBitmap round-trips plus a full-size decode for the vibrant colour
    /// on the UI thread, right on top of the lyrics fade, and at a track change the
    /// Bitmap can still be the PREVIOUS cover, whose colours then got cached under the new
    /// path. Warm caches (a cover seen before) apply at once.
    /// </summary>
    private void RequestAdaptiveBackground()
    {
        var generation = ++_adaptiveBackgroundGeneration;
        var artPath = _player.AlbumArt != null ? _player.CurrentArtPath : null;
        if (artPath == null
            || (DominantColorExtractor.HasCachedColors(artPath) && ShareCardRenderer.HasVibrantColor(artPath)))
        {
            UpdateAdaptiveBackground(_player.AlbumArt);
            return;
        }
        _ = WarmThenApplyAdaptiveBackgroundAsync(artPath, generation);
    }

    private async Task WarmThenApplyAdaptiveBackgroundAsync(string artPath, int generation)
    {
        var warmed = false;
        try
        {
            warmed = await Task.Run(() =>
            {
                ShareCardRenderer.GetVibrantColorHex(artPath);
                return DominantColorExtractor.WarmFromFile(artPath);
            });
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Artwork colour warm-up failed: {ex.Message}");
        }

        // Back on the UI thread. A newer track or cover owns the background now.
        if (generation != _adaptiveBackgroundGeneration) return;
        // Unreadable file: the on-screen bitmap is still the previous cover (this one can't
        // decode either), so analysing it would cache the old colours under this path.
        // Keep the current background instead.
        if (!warmed) return;
        UpdateAdaptiveBackground(_player.AlbumArt);
    }

    private void UpdateAdaptiveBackground(Bitmap? albumArt)
    {
        // Each Extract* call renders the art into a RenderTargetBitmap and round-trips
        // it through a PNG encode/decode on the UI thread — heavy enough to visibly
        // stall render-priority animations at track start. Route through the path-keyed
        // caches so a given artwork is only ever analyzed once per session.
        var artPath = albumArt != null ? _player.CurrentArtPath : null;

        // The vibrant color the share-card renderer derives (path-cached) — used for the
        // Solid·Auto background so the lyrics page, the share card and the share dialog's
        // "A" swatch all show the exact same artwork color.
        Color? vibrant = null;
        if (artPath != null)
        {
            try { vibrant = Color.Parse(ShareCardRenderer.GetVibrantColorHex(artPath)); }
            catch { /* fall back to the dominant color below */ }
        }

        // Keep the "Auto" swatch showing the current track's artwork-derived color.
        _autoSwatch.Preview = new SolidColorBrush(vibrant ?? (artPath != null
            ? DominantColorExtractor.GetOrExtractDominantColor(artPath, albumArt!)
            : DominantColorExtractor.ExtractDominantColor(albumArt)));
        // The gradient one previews what Gradient·Auto draws. Here only from the path-keyed
        // palette cache; art without a file gets it below, along with its palette.
        if (albumArt == null)
            _autoGradientSwatch.Preview = CreateDefaultUnifiedBrush();
        else if (artPath != null)
        {
            try
            {
                var (d, s) = DominantColorExtractor.GetOrExtractPalette(artPath, albumArt);
                _autoGradientSwatch.Preview = DominantColorExtractor.GenerateUnifiedBrush(d, s);
            }
            catch { /* keeps the last preview */ }
        }

        // Don't override when a custom background color is selected
        if (_selectedColorHex != null) return;

        if (albumArt == null)
        {
            LeftPanelBrush = CreateDefaultGradient();
            LyricsBackgroundBrush = CreateDefaultSubduedGradient();
            FullBackgroundBrush = CreateDefaultUnifiedBrush();
            _averageArtworkColor = null;
            RefreshLyricsForegrounds();
            return;
        }

        try
        {
            var (dominant, secondary) = artPath != null
                ? DominantColorExtractor.GetOrExtractPalette(artPath, albumArt)
                : DominantColorExtractor.ExtractColorPalette(albumArt);
            var (left, right) = DominantColorExtractor.GenerateAdaptiveBrushes(dominant, secondary);
            LeftPanelBrush = left;
            LyricsBackgroundBrush = right;
            // Solid·Auto shows the artwork's vibrant color as a true solid; the unified
            // gradient's hue-shifted stops drifted visibly away from the cover's color.
            var unified = DominantColorExtractor.GenerateUnifiedBrush(dominant, secondary);
            _autoGradientSwatch.Preview = unified;
            FullBackgroundBrush = IsColorModeSolid && vibrant is { } v
                ? new SolidColorBrush(v)
                : unified;
            UpdateMeshColors(dominant, secondary);
            _averageArtworkColor = artPath != null
                ? DominantColorExtractor.GetOrExtractAverageColor(artPath, albumArt)
                : DominantColorExtractor.ExtractAverageColor(albumArt);
            RefreshLyricsForegrounds();
        }
        catch
        {
            LeftPanelBrush = CreateDefaultGradient();
            LyricsBackgroundBrush = CreateDefaultSubduedGradient();
            FullBackgroundBrush = CreateDefaultUnifiedBrush();
            _averageArtworkColor = null;
            RefreshLyricsForegrounds();
        }
    }

    /// <summary>
    /// Re-computes the lyrics-page foreground brushes against whichever surface is
    /// actually visible right now: the blurred artwork (with scrim applied) in artwork
    /// mode, or the FullBackgroundBrush color/gradient in solid/gradient mode.
    /// Called after track change and on mode toggle.
    /// </summary>
    private void RefreshLyricsForegrounds()
    {
        UpdateForegroundsForBackground(PageTintBrush);
        OnPropertyChanged(nameof(PageTintBrush));
    }

    /// <summary>The colour the page visibly shows: the scrimmed blurred artwork's average in
    /// artwork mode, else the solid/gradient background. "Tint Whole Window" paints it
    /// around the sidebar and player islands while this page is open (GitHub #142).</summary>
    public IBrush PageTintBrush
    {
        get
        {
            if (IsColorModeArtwork && _averageArtworkColor is Color avg)
            {
                // Scrim is pure black at ArtworkScrimAlpha opacity, so the visible color is
                // a straight linear interpolation of avg toward black.
                double k = 1.0 - ArtworkScrimAlpha;
                return new SolidColorBrush(Color.FromRgb(
                    (byte)Math.Clamp(avg.R * k, 0, 255),
                    (byte)Math.Clamp(avg.G * k, 0, 255),
                    (byte)Math.Clamp(avg.B * k, 0, 255)));
            }
            return FullBackgroundBrush;
        }
    }

    partial void OnFullBackgroundBrushChanged(IBrush value) => OnPropertyChanged(nameof(PageTintBrush));

    partial void OnIsColorModeArtworkChanged(bool value) => OnPropertyChanged(nameof(PageTintBrush));

    [RelayCommand]
    private void ResumeAutoFollow()
    {
        IsAutoFollowPaused = false;
    }

    [RelayCommand]
    private async Task SelectColorModeArtwork()
    {
        IsColorModeArtwork = true;
        IsColorModeSolid = false;
        IsColorModeGradient = false;
        // Restore adaptive (album-art-derived) colors so any previously chosen
        // swatch stops bleeding through the now-visible blurred artwork.
        await SetBackgroundColor(null);
        await PersistArtworkBackgroundPreferenceAsync(true);
    }

    [RelayCommand]
    private async Task SelectColorModeSolid()
    {
        IsColorModeArtwork = false;
        IsColorModeSolid = true;
        IsColorModeGradient = false;
        // On Auto, Solid and Gradient derive different brushes from the artwork
        // (vibrant solid vs unified gradient) — recompute for the new mode. That also
        // re-picks foregrounds against the now-visible surface (no longer the blurred
        // artwork) so timeline/metadata text stays readable.
        if (_selectedColorHex == null)
            UpdateAdaptiveBackground(_player.AlbumArt);
        else
            RefreshLyricsForegrounds();
        await PersistArtworkBackgroundPreferenceAsync(false, gradient: false);
    }

    [RelayCommand]
    private async Task SelectColorModeGradient()
    {
        IsColorModeArtwork = false;
        IsColorModeSolid = false;
        IsColorModeGradient = true;
        // Same contract as SelectColorModeSolid: Auto derives per mode.
        if (_selectedColorHex == null)
            UpdateAdaptiveBackground(_player.AlbumArt);
        else
            RefreshLyricsForegrounds();
        await PersistArtworkBackgroundPreferenceAsync(false, gradient: true);
    }

    private async Task PersistArtworkBackgroundPreferenceAsync(bool showArtwork, bool? gradient = null)
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            if (settings.LyricsShowArtworkBackground == showArtwork
                && (gradient is null || settings.LyricsBackgroundGradient == gradient)) return;
            settings.LyricsShowArtworkBackground = showArtwork;
            if (gradient is { } g) settings.LyricsBackgroundGradient = g;
            await _persistence.SaveSettingsAsync(settings);
        }
        catch { }
    }

    [RelayCommand]
    private async Task SetBackgroundColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            _selectedColorHex = null;
            ActiveSwatchKey = "";
            ResetForegroundsToDefault();
            UpdateAdaptiveBackground(_player.AlbumArt);
        }
        else if (hex.StartsWith("grad:"))
        {
            _selectedColorHex = hex;
            ActiveSwatchKey = hex;
            try
            {
                var parts = hex[5..].Split(',');
                var c1 = Color.Parse(parts[0]);
                var c2 = Color.Parse(parts[1]);
                FullBackgroundBrush = DominantColorExtractor.GenerateGradientBrush(c1, c2);
                LyricsBackgroundBrush = FullBackgroundBrush;
                UpdateForegroundsForBackground(FullBackgroundBrush);
            }
            catch
            {
                _selectedColorHex = null;
                ActiveSwatchKey = "";
                UpdateAdaptiveBackground(_player.AlbumArt);
            }
        }
        else
        {
            _selectedColorHex = hex;
            ActiveSwatchKey = hex;
            try
            {
                var color = Color.Parse(hex);
                var brush = new SolidColorBrush(color);
                FullBackgroundBrush = brush;
                LyricsBackgroundBrush = brush;
                UpdateForegroundsForBackground(brush);
            }
            catch
            {
                _selectedColorHex = null;
                ActiveSwatchKey = "";
                ResetForegroundsToDefault();
                UpdateAdaptiveBackground(_player.AlbumArt);
            }
        }

        // Persist preference
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            settings.LyricsBackgroundColorHex = _selectedColorHex ?? "";
            await _persistence.SaveSettingsAsync(settings);
        }
        catch { }
    }

    private async Task LoadSavedBackgroundColorAsync()
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();

            // Restore the Artwork/Solid/Gradient mode preference.
            // The Solid/Gradient sub-mode is filled in below when a swatch was saved.
            IsColorModeArtwork = settings.LyricsShowArtworkBackground;
            if (settings.LyricsShowArtworkBackground)
            {
                IsColorModeSolid = false;
                IsColorModeGradient = false;
            }
            else if (!IsColorModeSolid && !IsColorModeGradient)
            {
                // The sub-mode the user chose (LyricsBackgroundGradient; a saved swatch below
                // overrides it). With no saved swatch the background stays on Auto — recompute
                // it for that mode (vibrant solid or unified gradient) since the constructor
                // derived it in artwork mode.
                IsColorModeGradient = settings.LyricsBackgroundGradient;
                IsColorModeSolid = !settings.LyricsBackgroundGradient;
                if (string.IsNullOrEmpty(settings.LyricsBackgroundColorHex))
                    UpdateAdaptiveBackground(_player.AlbumArt);
            }

            var saved = settings.LyricsBackgroundColorHex;
            if (!string.IsNullOrEmpty(saved))
            {
                if (saved.StartsWith("grad:"))
                {
                    var parts = saved[5..].Split(',');
                    if (parts.Length >= 2 && Color.TryParse(parts[0], out var c1) && Color.TryParse(parts[1], out var c2))
                    {
                        _selectedColorHex = saved;
                        ActiveSwatchKey = saved;
                        FullBackgroundBrush = DominantColorExtractor.GenerateGradientBrush(c1, c2);
                        LyricsBackgroundBrush = FullBackgroundBrush;
                        IsColorModeSolid = false;
                        IsColorModeGradient = true;
                        UpdateForegroundsForBackground(FullBackgroundBrush);
                        return;
                    }
                }
                else if (Color.TryParse(saved, out var color))
                {
                    _selectedColorHex = saved;
                    ActiveSwatchKey = saved;
                    // A solid swatch is Solid mode whatever the mode flag says.
                    IsColorModeSolid = !IsColorModeArtwork;
                    IsColorModeGradient = false;
                    var brush = new SolidColorBrush(color);
                    FullBackgroundBrush = brush;
                    LyricsBackgroundBrush = brush;
                    UpdateForegroundsForBackground(brush);
                    return;
                }

                // Unreadable (a hand-edited settings.json): stay on Auto. Kept as the custom
                // colour it drew nothing and stopped the background following the artwork.
                _selectedColorHex = null;
                ActiveSwatchKey = "";
                UpdateAdaptiveBackground(_player.AlbumArt);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load lyrics background: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SelectSyncTab()
    {
        IsSyncTabSelected = true;
        IsUnsyncTabSelected = false;
        // Restart sync timer if playing synced lyrics
        if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
            _lyricsSyncTimer.Start();
    }

    [RelayCommand]
    private void SelectUnsyncTab()
    {
        IsSyncTabSelected = false;
        IsUnsyncTabSelected = true;
        // Stop sync timer — unsync tab doesn't need it
        _lyricsSyncTimer.Stop();
    }

    /// <summary>Sets the action to navigate to an artist's discography.</summary>
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    /// <summary>Sets the action to navigate to the current track's album.</summary>
    public void SetViewAlbumAction(Action<Track> action) => _viewAlbumAction = action;

    [RelayCommand]
    private void ViewArtist()
    {
        var artist = _player.CurrentTrack?.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
    }

    // ── Header credits (Discord, aaron 2026-09-21) ──
    // "Kanye West, GLC, Consequence" was ONE button that always opened the first name;
    // the album page already renders one link per credited artist, so the lyrics page
    // now does the same. The facts line printed the playing file's TRCK total (the
    // current DISC's count) — the album's own count spans every disc.

    /// <summary>One entry per credited artist of the current track, in tag order.</summary>
    [ObservableProperty] private IReadOnlyList<ArtistTokenItem> _artistTokens = Array.Empty<ArtistTokenItem>();

    /// <summary>True when the credit splits into more than one artist (per-artist links shown).</summary>
    [ObservableProperty] private bool _hasMultipleArtists;

    /// <summary>The album's track count across all discs, falling back to the tag's per-disc total.</summary>
    [ObservableProperty] private int _albumTrackCount;

    [RelayCommand]
    private void ViewArtistNamed(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _viewArtistAction?.Invoke(name);
    }

    private void RefreshHeaderCredits()
    {
        var track = _player.CurrentTrack;
        var tokens = BuildArtistTokens(track?.Artist);
        ArtistTokens = tokens;
        HasMultipleArtists = tokens.Length > 1;
        AlbumTrackCount = track is null ? 0 : ResolveAlbumTrackCount(track, _library.GetAlbumById(track.AlbumId));
    }

    /// <summary>Splits a credit with the library's configured separators (Settings → Library).</summary>
    internal static ArtistTokenItem[] BuildArtistTokens(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<ArtistTokenItem>();
        var names = Track.ParseArtistTokens(artist);
        if (names.Length == 0) names = new[] { artist.Trim() };
        return names.Select((n, i) => new ArtistTokenItem(n, IsLast: i == names.Length - 1)).ToArray();
    }

    /// <summary>The album's grouped count (every disc) when the library knows it, else the tag's per-disc total.</summary>
    internal static int ResolveAlbumTrackCount(Track track, Album? album)
        => album is { TrackCount: > 0 } ? album.TrackCount : track.TrackCount;

    [RelayCommand]
    private void ViewAlbum()
    {
        var track = _player.CurrentTrack;
        if (track == null) return;

        Dispatcher.UIThread.Post(
            () => _viewAlbumAction?.Invoke(track),
            DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task SearchLyrics()
    {
        var track = _currentTrack;
        if (track == null) return;
        // Marked as having no lyrics (GitHub #162): only Search Lyrics' dialog may look any up.
        if (track.NoLyrics) return;

        DebugLogger.Info(DebugLogger.Category.Lyrics, "SearchLyrics", $"artist={track.Artist}, title={track.Title}");
        var generation = ++_searchGeneration;
        IsSearching = true;
        ShowSearchButton = false;
        SearchFailedMessage = string.Empty;
        SaveStatusText = string.Empty;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _lrcRedTarget = null;
        RefreshLrcRedOffer();

        try
        {
            // Load settings to check which providers are enabled
            var settings = await _persistence.LoadSettingsAsync();
            var lrcLibEnabled = settings.LrcLibEnabled;
            var netEaseEnabled = settings.NetEaseEnabled;
            // Plugin providers ("lyrics.provider") race alongside the built-ins and rank after
            // them; each is time-boxed and its failures contained by PluginLyricsSource.
            IReadOnlyList<Services.Plugins.PluginLyricsSource> pluginSources;
            try { pluginSources = PluginLyricsSources?.Invoke() ?? Array.Empty<Services.Plugins.PluginLyricsSource>(); }
            catch { pluginSources = Array.Empty<Services.Plugins.PluginLyricsSource>(); }
            var pluginResults = new LrcLibResult?[pluginSources.Count];
            var pluginErrored = new bool[pluginSources.Count];
            // Musixmatch / Kugou / YouTube Music (issue #113): switched on in Settings, ranked
            // after LRCLIB and NetEase and before plugins, waited on only as long as they could
            // still improve the pick (see WaitForExtraSourcesAsync).
            var extraSources = ExtraLyricsSources.Where(s => s.IsEnabled(settings)).ToList();
            var extraResults = new LrcLibResult?[extraSources.Count];
            var extraErrored = new bool[extraSources.Count];
            var extraTasks = new Task[extraSources.Count];
            var extraCts = new CancellationTokenSource();

            var artist = track.Artist ?? "";
            var title = track.Title ?? "";
            var duration = track.Duration.TotalSeconds;

            LrcLibResult? lrcLibResult = null;
            LrcLibResult? netEaseResult = null;
            var lrcLibInstrumental = false;
            // Provider errors (network/timeout/5xx/bad response) are distinct from a
            // definitive miss — the services throw LyricsProviderException for the
            // former and return null/empty for the latter.
            var lrcLibErrored = false;
            var netEaseErrored = false;

            // Search enabled providers in parallel
            var tasks = new List<Task>();

            if (lrcLibEnabled)
            {
                tasks.Add(FetchLrcLibAsync());
            }

            if (netEaseEnabled)
            {
                tasks.Add(FetchNetEaseAsync());
            }

            for (var i = 0; i < pluginSources.Count; i++)
                tasks.Add(FetchPluginAsync(i));

            // With both providers switched off, Task.WhenAll on an empty list completed
            // instantly and the user got "No Lyrics found." — indistinguishable from a
            // genuine miss, with no hint that the providers are disabled in Settings.
            if (tasks.Count == 0 && extraSources.Count == 0)
            {
                LyricLines.Clear();
                UnsyncedLines.Clear();
                SearchFailedMessage = Localization.Loc.T("Lyrics.ProvidersOff");
                // The page shows this message only inside the Search Lyrics state; hiding that
                // state left the page blank, with no hint and no way back to the search dialog
                // (where any one source can still be picked).
                ShowSearchButton = true;
                return;
            }

            for (var i = 0; i < extraSources.Count; i++)
                extraTasks[i] = FetchExtraAsync(i);

            await Task.WhenAll(tasks);
            await WaitForExtraSourcesAsync();
            // Sources still running past the wait are left out of this pick; stop them.
            extraCts.Cancel();
            _ = Task.WhenAll(extraTasks).ContinueWith(_ => extraCts.Dispose(), TaskScheduler.Default);

            async Task FetchLrcLibAsync()
            {
                try
                {
                    var result = await FetchFromLrcLibAsync(_lrcLib, artist, title, duration, CancellationToken.None);
                    if (result is { Instrumental: true })
                    {
                        // The exact match says this track is instrumental — that is an
                        // answer, not a miss.
                        lrcLibInstrumental = true;
                        return;
                    }
                    lrcLibResult = result;
                }
                catch (Exception ex)
                {
                    lrcLibErrored = true;
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "LRCLIB:Error", ex.Message);
                    // Keyed per failure kind, not per track: an offline session
                    // fails for every track and must not eat the session log.
                    DebugLog.WriteOnce("Lyrics", $"lrclib:{ex.GetBaseException().GetType().Name}",
                        $"LRCLIB fetch failed: {ex.Message}");
                }
            }

            async Task FetchNetEaseAsync()
            {
                try
                {
                    netEaseResult = await _netEase.SearchLyricsAsync(artist, title, duration);
                }
                catch (Exception ex)
                {
                    netEaseErrored = true;
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "NetEase:Error", ex.Message);
                    DebugLog.WriteOnce("Lyrics", $"netease:{ex.GetBaseException().GetType().Name}",
                        $"NetEase fetch failed: {ex.Message}");
                }
            }

            async Task FetchPluginAsync(int index)
            {
                try
                {
                    pluginResults[index] = await pluginSources[index].FindAsync(artist, title, track.Album ?? "", track.Duration);
                }
                catch (Exception ex)
                {
                    pluginErrored[index] = true;
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "Plugin:Error", $"{pluginSources[index].Name}: {ex.Message}");
                }
            }

            async Task FetchExtraAsync(int index)
            {
                try
                {
                    extraResults[index] = await SearchExtraSourceAsync(extraSources[index], artist, title, duration, extraCts.Token);
                }
                catch (Exception ex)
                {
                    extraErrored[index] = true;
                    if (!extraCts.IsCancellationRequested)
                    {
                        DebugLogger.Warn(DebugLogger.Category.Lyrics, $"{extraSources[index].Name}:Error", ex.Message);
                        DebugLog.WriteOnce("Lyrics", $"{extraSources[index].Name}:{ex.GetBaseException().GetType().Name}",
                            $"{extraSources[index].Name} fetch failed: {ex.Message}");
                    }
                }
            }

            // LRCLIB, NetEase and the plugins are in. The extra sources rank after the first
            // two, so they can only win with a richer format: never wait for them over a
            // word-synced built-in answer, at most ExtraSourceGrace when there is something to
            // show already, up to their own time box when there is nothing — and stop as soon
            // as nothing still running could beat what has arrived.
            async Task WaitForExtraSourcesAsync()
            {
                if (extraTasks.Length == 0) return;
                var builtInRank = Math.Max(LyricsSearchSelector.FormatRank(lrcLibResult), LyricsSearchSelector.FormatRank(netEaseResult));
                if (builtInRank >= LyricsSearchSelector.WordSyncedRank) return;
                var hasAnswer = builtInRank > 0 || pluginResults.Any(r => r is { HasLyrics: true });
                var deadline = Task.Delay(hasAnswer ? ExtraSourceGrace : ExtraSourceTimeout + TimeSpan.FromSeconds(1));
                while (true)
                {
                    var pending = extraTasks.Where(t => !t.IsCompleted).ToArray();
                    if (pending.Length == 0 || ExtraPickIsFinal(builtInRank, extraResults, extraTasks.Select(t => t.IsCompleted).ToArray()))
                        return;
                    if (await Task.WhenAny(Task.WhenAny(pending), deadline) == deadline) return;
                }
            }

            // Race condition guard
            if (generation != _searchGeneration) return;

            // Pick best result: the richest format (word-synced > line-synced > plain), then
            // provider order (LRCLIB, NetEase, the extra sources, then plugin providers in load
            // order). An extra source still running when the wait ended is left out.
            var candidates = new List<(LrcLibResult? Result, string Source)> { (lrcLibResult, "LRCLIB"), (netEaseResult, "NetEase") };
            for (var i = 0; i < extraSources.Count; i++)
                candidates.Add((extraTasks[i].IsCompleted ? extraResults[i] : null, extraSources[i].Name));
            for (var i = 0; i < pluginSources.Count; i++) candidates.Add((pluginResults[i], pluginSources[i].Name));
            var (primary, primarySource, alternate, altSource) = PickBestResult(candidates);
            // Settings ▸ Lyrics ▸ Preferred lyrics type (GitHub #135): what this pick keeps.
            var preferred = LyricsSaveOptions.ParsePreferredType(_player.LyricsPreferredType);
            if (primary != null) primary = ApplyPreferredType(primary, preferred);
            if (alternate != null) alternate = ApplyPreferredType(alternate, preferred);
            var pluginInstrumental = netEaseResult is { Instrumental: true, HasLyrics: false }
                                     || pluginResults.Any(r => r is { Instrumental: true, HasLyrics: false })
                                     || extraResults.Any(r => r is { Instrumental: true, HasLyrics: false });
            // An extra source that never answered in time counts as errored for the message below.
            var extrasAllErrored = extraTasks.Select((t, i) => !t.IsCompleted || extraErrored[i]).All(e => e);

            if (primary != null && primary.HasLyrics)
            {
                DebugLogger.Info(DebugLogger.Category.Lyrics, "SearchLyrics:Found",
                    $"source={primarySource}, synced={primary.HasSyncedLyrics}");
                DisplayOnlineLyrics(primary);
                LyricsSourceName = primarySource;

                // Store alternate if available
                if (alternate != null && alternate.HasLyrics)
                {
                    _alternateOnlineResult = alternate;
                    _alternateSource = altSource;
                    HasAlternateLyrics = true;
                    AlternateLyricsLabel = Localization.Loc.T("Lyrics.TrySource", altSource);
                }

                // lrc.red (searched, so on in Settings) has the song only line-synced or plain:
                // its model can word-sync it from the page.
                var lrcRedAt = extraSources.FindIndex(s => s.Name == Services.Lyrics.OnlineLyricsSource.LrcRed);
                if (lrcRedAt >= 0 && extraTasks[lrcRedAt].IsCompleted)
                    OfferLrcRedSync(track, extraResults[lrcRedAt]);
            }
            else
            {
                LyricLines.Clear();
                UnsyncedLines.Clear();
                if (lrcLibInstrumental || pluginInstrumental)
                {
                    // A definitive "instrumental" answer outranks any provider error.
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:Instrumental");
                    SearchFailedMessage = Localization.Loc.T("Lyrics.Instrumental");
                }
                else if ((!lrcLibEnabled || lrcLibErrored) && (!netEaseEnabled || netEaseErrored) && pluginErrored.All(e => e)
                         && extrasAllErrored)
                {
                    // EVERY enabled provider errored — plausibly an offline user (or
                    // a blanket outage), which must not read as "this track has no
                    // lyrics". If any provider answered — even empty-handed — the
                    // connection demonstrably works and the miss branch below tells
                    // the truth; one provider's outage must not dress every miss up
                    // as a connectivity failure.
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:ProviderError");
                    SearchFailedMessage = Localization.Loc.T("Lyrics.SearchFailedOffline");
                }
                else
                {
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:NotFound");
                    SearchFailedMessage = Localization.Loc.T("Lyrics.NoLyricsFound");
                }
                ShowSearchButton = true;
            }
        }
        catch (Exception ex)
        {
            // Unexpected — the provider tasks catch their own exceptions, so this
            // is result handling/display blowing up, not the network.
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:Unhandled", ex.ToString());
            if (generation == _searchGeneration)
            {
                LyricLines.Clear();
                UnsyncedLines.Clear();
                SearchFailedMessage = Localization.Loc.T("Lyrics.SearchFailedOffline");
                ShowSearchButton = true;
            }
        }
        finally
        {
            // Only the newest search owns this flag. A superseded search's continuation
            // used to clear it unconditionally, so: search A in flight -> track changes
            // -> auto-search B starts and sets IsSearching -> A lands and clears it, and
            // B's "Searching for Lyrics" indicator vanished while B was still running.
            if (generation == _searchGeneration)
                IsSearching = false;
        }
    }

    /// <summary>
    /// An online result reduced to the preferred lyrics type (GitHub #135): line-synced drops the
    /// word timings (Lyricsfile and ELRC word tags), plain drops every timing. Word-synced, the
    /// default, keeps the result as it is. A result with nothing of the preferred kind keeps what
    /// it has: line timings are not invented, and plain is always derivable.
    /// </summary>
    internal static LrcLibResult ApplyPreferredType(LrcLibResult result, LyricsPreferredType type)
    {
        if (type == LyricsPreferredType.WordSynced || !result.HasLyrics) return result;
        var synced = SyncedToWrite(result);
        var plain = !string.IsNullOrWhiteSpace(result.PlainLyrics) ? result.PlainLyrics
            : string.IsNullOrWhiteSpace(synced) ? null : LyricsTextHelper.StripTimestamps(synced);
        return new LrcLibResult
        {
            Id = result.Id,
            Name = result.Name,
            TrackName = result.TrackName,
            ArtistName = result.ArtistName,
            AlbumName = result.AlbumName,
            Duration = result.Duration,
            Instrumental = result.Instrumental,
            PlainLyrics = plain,
            SyncedLyrics = type == LyricsPreferredType.LineSynced && !string.IsNullOrWhiteSpace(synced)
                ? Services.Lyrics.LyricsWriter.LineLevelProjection(synced)
                : null,
            Lyricsfile = null,
            HasWordSync = false,
            Isrc = result.Isrc,
        };
    }

    /// <summary>LRCLIB's lookup for one track (<see cref="LrcLibLookup.FetchAsync"/>, shared
    /// with the phone).</summary>
    internal static Task<LrcLibResult?> FetchFromLrcLibAsync(
        ILrcLibService lrcLib, string artist, string title, double duration, CancellationToken ct) =>
        LrcLibLookup.FetchAsync(lrcLib, artist, title, duration, ct);

    /// <summary>One extra source's search, time-boxed by <see cref="ExtraSourceTimeout"/>; a source
    /// that runs past it throws <see cref="LyricsProviderException"/> like any other failure.</summary>
    internal async Task<LrcLibResult?> SearchExtraSourceAsync(
        Services.Lyrics.OnlineLyricsSource source, string artist, string title, double duration, CancellationToken ct)
    {
        // Task.Run: the search must not run its synchronous start on the caller (the UI thread).
        var task = Task.Run(() => source.SearchAsync(artist, title, duration, ct), CancellationToken.None);
        try
        {
            return await task.WaitAsync(ExtraSourceTimeout, ct);
        }
        catch (TimeoutException ex)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new LyricsProviderException(source.Name, ex);
        }
    }

    /// <summary>
    /// True when no extra source still running could beat the answer already in hand: an extra
    /// source has a word-synced answer (the top format) and every extra source ranked ahead of
    /// it has finished. Built-in answers rank ahead of all extras and are handled by the caller.
    /// </summary>
    internal static bool ExtraPickIsFinal(int builtInRank, IReadOnlyList<LrcLibResult?> extraResults, IReadOnlyList<bool> completed)
    {
        if (builtInRank >= LyricsSearchSelector.WordSyncedRank) return true;
        for (var i = 0; i < extraResults.Count; i++)
        {
            if (!completed[i]) return false;
            if (LyricsSearchSelector.FormatRank(extraResults[i]) >= LyricsSearchSelector.WordSyncedRank) return true;
        }
        return false;
    }

    /// <summary>
    /// Picks the best lyrics result over providers in priority order (built-ins first, then plugin
    /// providers): the richest format wins — word-synced, then line-synced, then plain
    /// (<see cref="LyricsSearchSelector.FormatRank"/>) — and the earlier provider wins a tie;
    /// the alternate is the best of the rest by the same rule. With LRCLIB and NetEase
    /// alone this is the old two-way rule (synced beats unsynced, LRCLIB wins ties).
    /// </summary>
    internal static (LrcLibResult? Primary, string PrimarySource, LrcLibResult? Alternate, string? AlternateSource)
        PickBestResult(IReadOnlyList<(LrcLibResult? Result, string Source)> candidates)
    {
        var withLyrics = candidates.Where(c => c.Result is { HasLyrics: true }).ToList();
        if (withLyrics.Count == 0) return (null, "", null, null);

        static (LrcLibResult? Result, string Source) Best(List<(LrcLibResult? Result, string Source)> list)
        {
            var best = list[0];
            foreach (var c in list)
                if (LyricsSearchSelector.FormatRank(c.Result) > LyricsSearchSelector.FormatRank(best.Result))
                    best = c;
            return best;
        }

        var primary = Best(withLyrics);
        var rest = withLyrics.Where(c => !ReferenceEquals(c.Result, primary.Result)).ToList();
        if (rest.Count == 0) return (primary.Result, primary.Source, null, null);
        var alternate = Best(rest);
        return (primary.Result, primary.Source, alternate.Result, alternate.Source);
    }

    /// <summary>
    /// Switches to the alternate lyrics source when the user clicks "Try alternate".
    /// </summary>
    [RelayCommand]
    private void SwitchToAlternateLyrics()
    {
        if (_alternateOnlineResult == null || _alternateSource == null) return;

        // Swap current and alternate
        var prevResult = _currentOnlineResult;
        var prevSource = LyricsSourceName;

        // userSwitchedSource: this is an explicit choice, so an app-written sidecar
        // holding the previous source may be replaced — otherwise it out-prioritizes
        // the cache on the next play and the switch silently reverts.
        DisplayOnlineLyrics(_alternateOnlineResult, userSwitchedSource: true);
        LyricsSourceName = _alternateSource;

        _alternateOnlineResult = prevResult;
        _alternateSource = prevSource;
        HasAlternateLyrics = prevResult != null && prevResult.HasLyrics;
        AlternateLyricsLabel = Localization.Loc.T("Lyrics.TrySource", prevSource);
    }

    // ── Search Lyrics with a source picker (issue #113) ──

    /// <summary>Opens the Search Lyrics dialog for the track on the page.</summary>
    [RelayCommand]
    private async Task OpenLyricsSearch()
    {
        if (_currentTrack == null || ShowLyricsSearchDialog == null) return;
        var search = CreateLyricsSearchViewModel(_currentTrack);
        // Auto runs straight away, so the dialog opens on results rather than an empty list.
        _ = search.SearchAsync();
        await ShowLyricsSearchDialog(search);
    }

    /// <summary>The source picker for <paramref name="track"/>, searching through this page's providers.</summary>
    internal LyricsSearchViewModel CreateLyricsSearchViewModel(Track track) => new(
        track,
        AllSourceNames(),
        async () => AutoSourceNames(await _persistence.LoadSettingsAsync()),
        (sources, artist, title, ct) => SearchSourcesAsync(sources, artist, title, track.Duration.TotalSeconds, track.Album ?? "", ct),
        (result, source) => ApplySearchedLyrics(track, result, source))
    {
        // GitHub #162: "No Lyrics" in the footer marks the song (Undo when it already is).
        SetNoLyrics = marked =>
        {
            if (!ReferenceEquals(_currentTrack, track)) SetNoLyricsMark(track, marked);
            else if (marked) _ = MarkNoLyricsCommand.ExecuteAsync(null);
            else UnmarkNoLyricsCommand.Execute(null);
        },
        // lrc.red's row offers its model only while lrc.red is on in Settings. Its answer, once
        // used, is credited on the page as the model's.
        SyncWithLrcRedModel = LrcRed is { } lrcRed
            ? async (isrc, ct) =>
            {
                var synced = await lrcRed.SyncWithModelAsync(isrc, ct);
                _lrcRedModelResult = synced;
                return synced;
            }
            : null,
        CanOfferLrcRedModel = async () => LrcRed != null && await LrcRedModelAllowedAsync(),
    };

    private IReadOnlyList<Services.Plugins.PluginLyricsSource> CurrentPluginSources()
    {
        try { return PluginLyricsSources?.Invoke() ?? Array.Empty<Services.Plugins.PluginLyricsSource>(); }
        catch { return Array.Empty<Services.Plugins.PluginLyricsSource>(); }
    }

    /// <summary>Every online source in Auto's priority order: LRCLIB, NetEase, the extra sources, then plugin providers.</summary>
    internal IReadOnlyList<string> AllSourceNames()
    {
        var names = new List<string> { "LRCLIB", "NetEase" };
        names.AddRange(ExtraLyricsSources.Select(s => s.Name));
        names.AddRange(CurrentPluginSources().Select(p => p.Name));
        return names;
    }

    /// <summary>What "Auto" searches: the same sources as the automatic lookup — those switched on in Settings, plus plugin providers.</summary>
    internal IReadOnlyList<string> AutoSourceNames(AppSettings settings)
    {
        var names = new List<string>();
        if (settings.LrcLibEnabled) names.Add("LRCLIB");
        if (settings.NetEaseEnabled) names.Add("NetEase");
        names.AddRange(ExtraLyricsSources.Where(s => s.IsEnabled(settings)).Select(s => s.Name));
        names.AddRange(CurrentPluginSources().Select(p => p.Name));
        return names;
    }

    /// <summary>
    /// Searches the named sources in parallel for the source picker — each named one, whatever
    /// its Settings switch says — and returns one hit per source in the order given. A source
    /// that fails or times out comes back as errored; only the caller's cancel propagates.
    /// </summary>
    internal async Task<IReadOnlyList<Services.Lyrics.LyricsSourceHit>> SearchSourcesAsync(
        IReadOnlyList<string> sources, string artist, string title, double duration, string album, CancellationToken ct)
    {
        var plugins = CurrentPluginSources();
        return await Task.WhenAll(sources.Select(SearchOneAsync));

        async Task<Services.Lyrics.LyricsSourceHit> SearchOneAsync(string name)
        {
            try
            {
                LrcLibResult? result;
                if (name == "LRCLIB")
                    result = await FetchFromLrcLibAsync(_lrcLib, artist, title, duration, ct);
                else if (name == "NetEase")
                    result = await _netEase.SearchLyricsAsync(artist, title, duration, ct);
                else if (ExtraLyricsSources.FirstOrDefault(s => s.Name == name) is { } extra)
                    result = await SearchExtraSourceAsync(extra, artist, title, duration, ct);
                else if (plugins.FirstOrDefault(p => p.Name == name) is { } plugin)
                    result = await plugin.FindAsync(artist, title, album, TimeSpan.FromSeconds(duration), ct);
                else
                    result = null;
                return new Services.Lyrics.LyricsSourceHit(name, result, Errored: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "SourceSearch:Error", $"{name}: {ex.Message}");
                return new Services.Lyrics.LyricsSourceHit(name, null, Errored: true,
                    Busy: ex is LyricsProviderException { IsBusy: true });
            }
        }
    }

    /// <summary>
    /// Shows and keeps lyrics picked in the source picker. Like "Try alternate" it may replace
    /// a sidecar this app wrote, never the user's own. A pick for a track that has left the
    /// page meanwhile is saved without being shown.
    /// </summary>
    internal void ApplySearchedLyrics(Track track, LrcLibResult result, string source)
    {
        if (!result.HasLyrics) return;
        // Lyrics picked by hand: the song has lyrics after all (GitHub #162).
        SetNoLyricsMark(track, false);

        if (_currentTrack == null || _currentTrack.Id != track.Id)
        {
            PersistOnlineLyricsToSidecar(result, allowReplaceAppSidecar: true, target: track);
            var text = result.SyncedLyrics ?? result.PlainLyrics;
            if (!string.IsNullOrWhiteSpace(text))
                _ = SaveLyricsToCacheAsync(track.Id, text);
            return;
        }

        // Supersede an automatic search still in flight, so it can't land over the pick.
        ++_searchGeneration;
        IsSearching = false;
        SearchFailedMessage = string.Empty;
        DisplayOnlineLyrics(result, userSwitchedSource: true);
        LyricsSourceName = source;
        _alternateOnlineResult = null;
        _alternateSource = null;
        HasAlternateLyrics = false;
        AlternateLyricsLabel = string.Empty;
        // lrc.red's line-synced or plain pick: its model can word-sync it from the page.
        _lrcRedTarget = null;
        if (source == Services.Lyrics.OnlineLyricsSource.LrcRed)
            _ = OfferLrcRedSyncAsync(track, result);
        RefreshLrcRedOffer();
        RefreshLyricsCredit(); // lrc.red to lrc.red (its model's answer) leaves the name as it was
    }

    // ── Word-sync with lrc.red's model ──
    //
    // lrc.red can time every word of songs it has only line-synced or plain with its in-house
    // model (LrcRedLyricsService.SyncWithModelAsync). It is donation-funded and asks for credit,
    // so the model runs only on a press of the page's pill (or lrc.red's row in Search Lyrics),
    // one request per press, while lrc.red is on in Settings; a song change cancels it.

    /// <summary>lrc.red, for its model sync; set by MainWindowViewModel. Null: never offered.</summary>
    internal ILrcRedLyricsService? LrcRed { get; set; }

    /// <summary>The lrc.red recording of the lyrics on the page that its model can word-sync.</summary>
    private (Guid TrackId, string Isrc)? _lrcRedTarget;
    private CancellationTokenSource? _lrcRedSyncCts;
    private Guid? _lrcRedSyncTrackId;
    /// <summary>The model's answer, while it is on the page: credited as the model's.</summary>
    private LrcLibResult? _lrcRedModelResult;

    /// <summary>The pill shows: the page's lyrics are lrc.red-matched and not word-synced, or a sync is running.</summary>
    [ObservableProperty] private bool _showLrcRedSync;
    [ObservableProperty] private bool _isLrcRedSyncing;
    /// <summary>"Word-sync with lrc.red's model", "Timing every word… a few seconds", or "Try again" after a failure.</summary>
    [ObservableProperty] private string _lrcRedSyncLabel = string.Empty;
    /// <summary>Why the last sync failed, beside the pill; empty otherwise.</summary>
    [ObservableProperty] private string _lrcRedSyncError = string.Empty;
    /// <summary>"Lyrics from lrc.red" / "Word-synced by lrc.red's model" under lrc.red's lyrics (its API asks to be credited).</summary>
    [ObservableProperty] private string _lyricsCreditText = string.Empty;

    partial void OnLyricsSourceNameChanged(string value) => RefreshLyricsCredit();

    private void RefreshLyricsCredit() =>
        LyricsCreditText = LyricsSourceName != Services.Lyrics.OnlineLyricsSource.LrcRed ? string.Empty
            : Localization.Loc.T(_lrcRedModelResult != null && ReferenceEquals(_currentOnlineResult, _lrcRedModelResult)
                ? "Lyrics.LrcRedModelCredit" : "Lyrics.LrcRedCredit");

    /// <summary>The model is offered while lrc.red is on in Settings and the preferred lyrics type keeps word timings.</summary>
    private async Task<bool> LrcRedModelAllowedAsync()
    {
        if (LyricsSaveOptions.ParsePreferredType(_player.LyricsPreferredType) != LyricsPreferredType.WordSynced) return false;
        try { return (await _persistence.LoadSettingsAsync()).LrcRedEnabled; }
        catch { return false; }
    }

    /// <summary>
    /// Remembers lrc.red's answer for the track on the page when its model could word-sync it:
    /// lrc.red has lyrics for the recording (an ISRC), only line-synced or plain. The caller has
    /// checked that lrc.red is on.
    /// </summary>
    private void OfferLrcRedSync(Track track, LrcLibResult? lrcRed)
    {
        if (LrcRed == null || lrcRed is not { HasLyrics: true, Isrc: { Length: > 0 } isrc }
            || LyricsSearchSelector.FormatRank(lrcRed) >= LyricsSearchSelector.WordSyncedRank
            || LyricsSaveOptions.ParsePreferredType(_player.LyricsPreferredType) != LyricsPreferredType.WordSynced
            || _currentTrack?.Id != track.Id)
            return;
        _lrcRedTarget = (track.Id, isrc);
        RefreshLrcRedOffer();
    }

    private async Task OfferLrcRedSyncAsync(Track track, LrcLibResult lrcRed)
    {
        if (LrcRed == null || lrcRed.Isrc == null || !await LrcRedModelAllowedAsync()) return;
        OfferLrcRedSync(track, lrcRed);
    }

    private void RefreshLrcRedOffer()
    {
        var shownRank = LyricsSearchSelector.FormatRank(_currentOnlineResult);
        var offered = LrcRed != null && _lrcRedTarget is { } target && _currentTrack?.Id == target.TrackId && !IsPreviewActive
                      && shownRank is > 0 and < LyricsSearchSelector.WordSyncedRank;
        ShowLrcRedSync = offered || IsLrcRedSyncing;
        LrcRedSyncLabel = Localization.Loc.T(IsLrcRedSyncing ? "Lyrics.LrcRedSyncWorking"
            : LrcRedSyncError.Length > 0 ? "Lyrics.LrcRedSyncRetry" : "Lyrics.LrcRedSync");
        if (!ShowLrcRedSync) LrcRedSyncError = string.Empty;
    }

    /// <summary>Drops the offer; <paramref name="cancel"/> also stops a sync in flight.</summary>
    private void ResetLrcRedSync(bool cancel)
    {
        if (cancel && _lrcRedSyncCts is { } running)
        {
            running.Cancel();
            _lrcRedSyncCts = null;
            _lrcRedSyncTrackId = null;
            IsLrcRedSyncing = false;
        }
        _lrcRedTarget = null;
        LrcRedSyncError = string.Empty;
        RefreshLrcRedOffer();
    }

    /// <summary>
    /// The pill's press: one POST to lrc.red's model for the recording on the page. On success the
    /// page switches to the word-synced lyrics and keeps them as any picked online lyrics are kept
    /// (cache, and the sidecar or tags per Settings ▸ Lyrics ▸ Save lyrics to); on failure it says
    /// why and the pill reads Try again — nothing is retried on its own.
    /// </summary>
    [RelayCommand]
    private async Task WordSyncWithLrcRed()
    {
        if (IsLrcRedSyncing || LrcRed is not { } lrcRed || _lrcRedTarget is not { } target
            || _currentTrack is not { } track || track.Id != target.TrackId)
            return;
        // Settings are read at the press too: the model only runs while lrc.red is on.
        if (!await LrcRedModelAllowedAsync())
        {
            ResetLrcRedSync(cancel: false);
            return;
        }
        if (_currentTrack?.Id != track.Id || IsLrcRedSyncing) return;

        var cts = new CancellationTokenSource();
        _lrcRedSyncCts = cts;
        _lrcRedSyncTrackId = track.Id;
        IsLrcRedSyncing = true;
        LrcRedSyncError = string.Empty;
        RefreshLrcRedOffer();
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LrcRed.ModelSync", $"isrc={target.Isrc}, title={track.Title}");
        try
        {
            var result = await lrcRed.SyncWithModelAsync(target.Isrc, cts.Token);
            if (cts.IsCancellationRequested || _currentTrack?.Id != track.Id) return;
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LrcRed.ModelSynced", $"isrc={target.Isrc}");

            // The lyrics shown before stay one tap away, unless they were lrc.red's own line timing.
            var previous = _currentOnlineResult;
            var previousSource = LyricsSourceName;
            ++_searchGeneration; // an automatic search still in flight must not land over these
            IsSearching = false;
            SearchFailedMessage = string.Empty;
            _lrcRedModelResult = result;
            // Done for this song: switching back to the line-synced lyrics offers no second run.
            _lrcRedTarget = null;
            DisplayOnlineLyrics(result, userSwitchedSource: true);
            LyricsSourceName = Services.Lyrics.OnlineLyricsSource.LrcRed;
            if (previous is { HasLyrics: true } && previousSource.Length > 0 && previousSource != Services.Lyrics.OnlineLyricsSource.LrcRed)
            {
                _alternateOnlineResult = previous;
                _alternateSource = previousSource;
                HasAlternateLyrics = true;
                AlternateLyricsLabel = Localization.Loc.T("Lyrics.TrySource", previousSource);
            }
            else if (_alternateSource == Services.Lyrics.OnlineLyricsSource.LrcRed)
            {
                _alternateOnlineResult = null;
                _alternateSource = null;
                HasAlternateLyrics = false;
                AlternateLyricsLabel = string.Empty;
            }
            RefreshLyricsCredit();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The song changed: nothing to say.
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LrcRed.ModelSyncFailed", ex.Message);
            if (!cts.IsCancellationRequested && _currentTrack?.Id == track.Id)
                LrcRedSyncError = LrcRedSyncMessage(ex);
        }
        finally
        {
            if (ReferenceEquals(_lrcRedSyncCts, cts))
            {
                _lrcRedSyncCts = null;
                _lrcRedSyncTrackId = null;
                IsLrcRedSyncing = false;
                RefreshLrcRedOffer();
            }
            cts.Dispose();
        }
    }

    /// <summary>A failed model sync in words, for the page, Search Lyrics and Lyrics Studio.</summary>
    internal static string LrcRedSyncMessage(Exception ex) => ex is not LrcRedSyncException sync
        ? Localization.Loc.T("Lyrics.LrcRedSyncBadAnswer")
        : sync.Failure switch
        {
            LrcRedSyncFailure.Network => Localization.Loc.T("Lyrics.LrcRedSyncNetwork"),
            LrcRedSyncFailure.Timeout => Localization.Loc.T("Lyrics.LrcRedSyncTimeout"),
            LrcRedSyncFailure.RateLimited => Localization.Loc.T("Lyrics.LrcRedSyncBusy"),
            LrcRedSyncFailure.NotWordSynced => Localization.Loc.T("Lyrics.LrcRedSyncNoWords"),
            LrcRedSyncFailure.Refused when sync.ServerMessage is { Length: > 0 } said => Localization.Loc.T("Lyrics.LrcRedSyncRefused", said),
            LrcRedSyncFailure.Refused => Localization.Loc.T("Lyrics.LrcRedSyncStatus", sync.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"),
            _ => Localization.Loc.T("Lyrics.LrcRedSyncBadAnswer"),
        };

    /// <summary>Asks before Save replaces the song's lyrics files; the message names them. Tests swap it.</summary>
    internal Func<string, Task<bool>> ConfirmReplaceLyrics { get; set; } = Views.ConfirmationDialog.ShowAsync;

    /// <summary>Save is offered for synced online lyrics of a song that is a file on this computer.</summary>
    private static bool CanSaveOnlineLyrics(Track? track, LrcLibResult? result) =>
        result is not null && (result.HasSyncedLyrics || result.HasLyricsfile)
        && track is { SourceType: SourceType.Local } && !string.IsNullOrWhiteSpace(track.FilePath)
        && SyncedToWrite(result) is not null;

    /// <summary>Offers Save ▸ TTML for the song's own synced lyrics on the page (<paramref name="synced"/>: their LRC/ELRC text, null when none).</summary>
    private void OfferTtmlSave(Track track, string? synced)
    {
        _localSyncedForSave = string.IsNullOrWhiteSpace(synced) || IsPreviewActive
            || track is not { SourceType: SourceType.Local } || string.IsNullOrWhiteSpace(track.FilePath)
            ? null : synced;
        CanSaveTtmlToFile = _localSyncedForSave is not null;
    }

    /// <summary>
    /// The synced text an online result is written as: the word timings of its Lyricsfile as
    /// ELRC when it has them, else its line-level synced lyrics. LRCLIB sends both; writing only
    /// the line-level twin made Save turn word-synced lyrics into line-synced ones, and the
    /// automatic save left a line-level .lrc that out-ranks the cached Lyricsfile, so the song
    /// went word-by-word on the first play and line-by-line from then on (live check 10-05).
    /// </summary>
    internal static string? SyncedToWrite(LrcLibResult result) =>
        (result.HasLyricsfile ? LyricsfileParser.ToElrc(result.Lyricsfile) : null)
        ?? (string.IsNullOrWhiteSpace(result.SyncedLyrics) ? null : result.SyncedLyrics);

    /// <summary>
    /// GitHub #115: saves the online lyrics on the page next to the song, under its name, and
    /// replaces what is there — word-synced as .elrc plus the line-level .lrc, line-synced as
    /// .lrc — through <see cref="Services.Lyrics.LyricsWriter"/>, the same writer Lyrics Studio
    /// saves with. The .ttml and LRCGET .lyricsfile the page reads first go too, or they would
    /// hide the save. Asks first, naming the files it changes; a file Noctis didn't write goes to
    /// the Recycle Bin. The audio file's tags are left alone. Afterwards the page re-reads the
    /// files, so what it shows is what was saved.
    /// </summary>
    /// <remarks>
    /// This used to write the .lrc straight over the song's own file (no prompt, no recycle bin),
    /// put word timings in the .lrc, leave a .elrc/.ttml that hid the save, and add a .txt the
    /// lyrics page never reads.
    /// </remarks>
    [RelayCommand]
    private Task SaveLyricsToFile() => SaveLyricsToFileCore(asTtml: false);

    /// <summary>
    /// "Save Lyrics to File ▸ TTML" (Discord, nutf!xx: players that read TTML but not ELRC):
    /// the same save as LRC, plus a .ttml beside the song in Apple Music's layout — word spans
    /// and background vocals when the lyrics have word timings, line times otherwise. The page
    /// reads the .ttml first, so it shows the save. Offered for the song's own synced lyrics too
    /// (an .lrc/.elrc/.lyricsfile, its tags, the cache), not only online ones: the automatic
    /// save had already put most found lyrics in an .lrc, which LRC alone never turned into TTML.
    /// </summary>
    [RelayCommand]
    private Task SaveLyricsAsTtml() => SaveLyricsToFileCore(asTtml: true);

    /// <summary>The audio file's tags hold lyrics a save of these would replace: not empty, not already
    /// these, and not lyrics this page put there itself. For the prompt before a save (GitHub #135).</summary>
    private static bool TagsHoldOtherLyrics(string path, string? plain, string? synced)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var current = EmbeddedLyrics.ReadFile(path);
        if (current.IsEmpty || EmbeddedLyrics.Holds(current, plain, synced)) return false;
        return !(PageEmbeddedLyrics.TryGetValue(path, out var ours) && SameLyricsText(current.Text, ours));
    }

    /// <summary>A .ttml of LRC/ELRC <paramref name="synced"/>: word timings when it has them, line times otherwise; null when nothing is timed.</summary>
    internal static string? BuildTtmlFromSynced(string synced)
    {
        var lines = Services.LyricsStudio.ExistingLyricsLoader.ParseTimed(synced);
        return lines.Count == 0 ? null : Services.LyricsStudio.TimedLyricsBuilder.BuildTtml(lines);
    }

    private async Task SaveLyricsToFileCore(bool asTtml)
    {
        if (_currentTrack is not { } track || IsPreviewActive) return;
        string synced, plain;
        if (_currentOnlineResult is { } result)
        {
            if (!CanSaveOnlineLyrics(track, result)) return;
            synced = SyncedToWrite(result)!;
            plain = !string.IsNullOrWhiteSpace(result.PlainLyrics) ? result.PlainLyrics : LyricsTextHelper.StripTimestamps(synced);
        }
        else
        {
            // The song's own lyrics: only TTML is offered for them; its plain lyrics are kept.
            if (!asTtml || !CanSaveTtmlToFile || _localSyncedForSave is not { } local) return;
            synced = local;
            var own = track.Lyrics;
            plain = !string.IsNullOrWhiteSpace(own) && !LyricsTextHelper.ContainsTimestamps(own) ? own : LyricsTextHelper.StripTimestamps(synced);
        }
        var trackPath = track.FilePath;
        var ttml = asTtml ? BuildTtmlFromSynced(synced) : null;
        if (asTtml && ttml is null)
        {
            ShowStatusText(Localization.Loc.T("Lyrics.SaveFailed"), 5000);
            return;
        }

        var mode = SaveTargetSetting;
        try
        {
            // On the writer lane, behind the automatic save of these lyrics that may still be
            // queued, so the prompt names only files this save really changes — and, when the
            // lyrics go inside the audio file, lyrics already there that it replaces (#135).
            var changed = new List<string>();
            await EnqueueLyricsFileWork(() =>
            {
                changed = Services.Lyrics.LyricsWriter.FilesChangedBySave(trackPath, synced, ttml, mode);
                if (mode.WritesTags() && TagsHoldOtherLyrics(trackPath, plain, synced))
                    changed.Add(Localization.Loc.T("Lyrics.SaveReplaceTags"));
            });
            if (changed.Count > 0)
            {
                // macOS/Linux variants say "Trash": there is no Recycle Bin there.
                var trash = OperatingSystem.IsWindows() ? "" : "Trash";
                var message = Localization.Loc.T((changed.Count == 1 ? "Lyrics.SaveReplaceOne" : "Lyrics.SaveReplaceMany") + trash,
                    track.Title, string.Join(Localization.Loc.T("Lyrics.SaveFilesJoin"), changed));
                bool confirmed;
                try { confirmed = await ConfirmReplaceLyrics(message); } catch { confirmed = false; }
                if (!confirmed) return;
            }

            var writer = new Services.Lyrics.LyricsWriter(_metadata, TagWriter, SidecarRegistry, LyricsCacheDir)
            {
                TrashFile = path => TrashSidecarFile(path),
            };
            var outcome = Services.Lyrics.LyricsSaveOutcome.Nothing;
            // Off the UI thread (trashing can wait on the OS) and in order with every other lyrics write.
            await EnqueueLyricsFileWork(() =>
                outcome = writer.SaveDetailed(track, plain, synced, embedInTags: false, replaceForeignSidecar: true, ttml, mode));
            // The user confirmed this save: what it put in the tags is the page's to replace or remove later.
            if (outcome.TagsQueued) PageEmbeddedLyrics[trackPath] = synced;
            DebugLogger.Info(DebugLogger.Category.Lyrics, "SaveLyricsToFile",
                $"ttml={asTtml}, target={mode}, written={outcome.SidecarWritten}, tags={outcome.TagsQueued}, replacedForeign={outcome.ReplacedForeignSidecar}, keptForeign={outcome.KeptForeignSidecar}");

            // Show what is on disk now; the reload also ends the online state (and the Save item).
            if (ReferenceEquals(_currentTrack, track))
                LoadLyricsForTrack(track);
            ShowStatusText(
                outcome.KeptForeignSidecar ? Localization.Loc.T(OperatingSystem.IsWindows() ? "Lyrics.SaveKeptOld" : "Lyrics.SaveKeptOldTrash")
                : outcome.Saved ? Localization.Loc.T("Lyrics.Saved")
                : Localization.Loc.T("Lyrics.SaveFailed"),
                outcome.Saved && !outcome.KeptForeignSidecar ? 3000 : 5000);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "SaveLyricsToFile:Failed", ex.Message);
            ShowStatusText(Localization.Loc.T("Lyrics.SaveFailed"), 5000);
        }
    }

    /// <summary>
    /// Writes freshly fetched online lyrics to sidecar files next to the track and
    /// updates the in-memory track fields, so the Metadata editor's Plain/Synced
    /// tabs reflect them immediately and persistently. Best-effort; never throws.
    /// Word-synced (ELRC) text follows <see cref="Services.Lyrics.LyricsWriter"/>'s rule: the
    /// word timings go to an .elrc and the .lrc keeps a line-level projection, so players that
    /// only know LRC still read it.
    /// </summary>
    private void PersistOnlineLyricsToSidecar(LrcLibResult result, bool allowReplaceAppSidecar = false, Track? target = null)
    {
        var track = target ?? _currentTrack;
        if (track == null) return;

        var synced = SyncedToWrite(result);
        var plain = !string.IsNullOrWhiteSpace(result.PlainLyrics)
            ? result.PlainLyrics
            : LyricsTextHelper.StripTimestamps(synced);

        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
            return;

        var trackPath = track.FilePath;
        // A streamed song (Jellyfin, Subsonic) has no folder of its own: its FilePath is a
        // stream URL, so nothing may be written beside it (Save to File has the same rule,
        // CanSaveOnlineLyrics). This used to queue a sidecar write there anyway.
        if (string.IsNullOrWhiteSpace(trackPath) || track.SourceType != SourceType.Local)
        {
            // No sidecar can exist without a track path of its own — just reflect the lyrics
            // into the in-memory track fields for the Metadata editor.
            track.Lyrics = plain ?? string.Empty;
            track.SyncedLyrics = synced ?? string.Empty;
            return;
        }

        var lrcPath = Path.ChangeExtension(trackPath, ".lrc");
        var elrcPath = Path.ChangeExtension(trackPath, ".elrc");
        var canWriteSidecar = !string.IsNullOrWhiteSpace(synced);
        var isWordLevel = canWriteSidecar && EnhancedLrcParser.ContainsWordTags(synced);
        var lrcText = isWordLevel ? Services.Lyrics.LyricsWriter.LineLevelProjection(synced!) : synced;
        var stamp = Volatile.Read(ref _lyricsRemovalStamp);
        // Settings ▸ Lyrics ▸ Save lyrics to (GitHub #135): files beside the song, the audio
        // file's tags, or both. On an explicit switch, lyrics the page put in the tags for the
        // previous pick may be replaced; anything else in the tags is the song's own.
        var mode = SaveTargetSetting;
        var replaceableTagText = allowReplaceAppSidecar && PageEmbeddedLyrics.TryGetValue(trackPath, out var ownTagText) ? ownTagText : null;

        // Everything below runs on the FIFO writer lane, where File.Exists sees the
        // settled state: any earlier queued sidecar write has already landed or been
        // skipped. Deciding replace-vs-skip up front raced that queued write — a fast
        // "Try alternate" computed replaceAppSidecar=false against a not-yet-written
        // file, then its own queued write saw the file exist and skipped, so the
        // explicit switch never reached disk.
        //
        // An explicit "Try alternate" may replace a sidecar this app wrote itself
        // (allowReplaceAppSidecar + registered): without that, the primary result
        // on disk out-prioritized the alternate in the cache on the next play and
        // the user's choice silently reverted. A user's own sidecar is never
        // replaced, even on an explicit switch. This used to write both sidecars
        // unconditionally on every successful online fetch, so a user's own
        // hand-timed .lrc — or, worse, an unrelated Song.txt of liner notes, a file
        // this app never even reads as a lyrics source — was destroyed with no
        // consent, no prompt and no setting to turn it off. A .lrc that merely
        // failed to parse was enough to reach this path. The .txt is no longer
        // written at all here: the lyrics probe never reads it back.
        _ = EnqueueLyricsFileWork(() =>
        {
            try
            {
                // A RemoveLyrics landed after this persist was queued — writing now
                // would resurrect what the user just removed.
                if (Volatile.Read(ref _lyricsRemovalStamp) != stamp) return;

                if (!mode.WritesSidecars())
                {
                    PersistOnlineLyricsToTags(track, trackPath, plain, synced, allowReplaceAppSidecar, replaceableTagText, stamp);
                    return;
                }

                var sidecarExists = File.Exists(lrcPath);
                var replaceAppSidecar = canWriteSidecar && sidecarExists && allowReplaceAppSidecar
                    && SidecarRegistry.Contains(lrcPath);
                // An explicit switch on a song whose page already showed a file the probe reads
                // before .lrc — a .lyricsfile, a .ttml, or the user's own .elrc — must not write
                // here: the new files stayed hidden under it, cluttered the folder, and the track
                // fields advertised lyrics disk overrode on the next play (GitHub #115). Save
                // Lyrics to File is the explicit way to replace it.
                var hiddenByHigherSidecar = allowReplaceAppSidecar && canWriteSidecar && HigherSidecarHidesLrc(trackPath);
                // On an explicit switch that leaves an existing sidecar standing
                // (user-owned, or nothing synced to replace an app-written one with)
                // the track fields stay untouched: they must not advertise lyrics
                // that disk will override on the next play. The switched-to lyrics
                // still display (and cache) for this session.
                var blockedBySidecar = (sidecarExists && allowReplaceAppSidecar && !replaceAppSidecar) || hiddenByHigherSidecar;

                if (!blockedBySidecar)
                {
                    // The Metadata window reads these fields before falling back to
                    // sidecars, so an already-open editor reflects them. Re-checking
                    // the stamp on the UI thread keeps a queued update from putting
                    // the fields back after RemoveLyrics cleared them.
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Volatile.Read(ref _lyricsRemovalStamp) != stamp) return;
                        track.Lyrics = plain ?? string.Empty;
                        track.SyncedLyrics = synced ?? string.Empty;
                    });
                    // "Both": the tags too, where they hold no lyrics of the song's own.
                    if (mode.WritesTags())
                        QueueEmbeddedLyrics(trackPath, plain, synced, replaceableTagText, stamp);
                }

                if (!canWriteSidecar) return;
                if (hiddenByHigherSidecar)
                {
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "Sidecar.SkipHidden", trackPath);
                    return;
                }
                if (sidecarExists && !replaceAppSidecar)
                {
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "Sidecar.SkipExisting", lrcPath);
                    return;
                }

                File.WriteAllText(lrcPath, NormalizeLyricsForLrc(lrcText!), new UTF8Encoding(false));
                SidecarRegistry.Add(lrcPath);

                // The .elrc is only touched where the .lrc could be written: it outranks the
                // .lrc on the next play, so one beside a user's own .lrc would override it.
                var elrcExists = File.Exists(elrcPath);
                var elrcIsOurs = elrcExists && SidecarRegistry.Contains(elrcPath);
                if (isWordLevel)
                {
                    if (!elrcExists || (allowReplaceAppSidecar && elrcIsOurs))
                    {
                        File.WriteAllText(elrcPath, NormalizeLyricsForLrc(synced!), new UTF8Encoding(false));
                        SidecarRegistry.Add(elrcPath);
                    }
                }
                else if (elrcIsOurs && allowReplaceAppSidecar)
                {
                    // An explicit switch to line-synced lyrics: the app's own .elrc from the
                    // previous pick would out-rank the new .lrc and revert the choice.
                    if (TrashSidecarFile(elrcPath) || !File.Exists(elrcPath))
                        SidecarRegistry.Remove(elrcPath);
                }
            }
            catch { /* best effort — sidecar write is non-fatal */ }
        });
    }

    /// <summary>
    /// "Save lyrics to: Inside the audio file" (GitHub #135) for found online lyrics: no lyrics file
    /// is written; the lyrics go into the tags. Any lyrics file the page reads before the tags would
    /// hide them on the next play: the page's own from an earlier pick goes to the trash on an
    /// explicit switch, and one of the user's own leaves the song alone (fields and tags), as a
    /// user's .lrc does for the file-based save. Runs on the lyrics file lane.
    /// </summary>
    private void PersistOnlineLyricsToTags(Track track, string trackPath, string? plain, string? synced,
        bool explicitSwitch, string? replaceableTagText, int stamp)
    {
        foreach (var ext in new[] { ".lyricsfile", ".ttml", ".elrc", ".lrc" })
            foreach (var sidecar in Services.Lyrics.LyricsWriter.SidecarsOnDisk(trackPath, ext))
            {
                if (explicitSwitch && SidecarRegistry.Contains(sidecar)
                    && (TrashSidecarFile(sidecar) || !File.Exists(sidecar)))
                {
                    SidecarRegistry.Remove(sidecar);
                    continue;
                }
                DebugLogger.Info(DebugLogger.Category.Lyrics, "Tags.SkipHiddenBySidecar", sidecar);
                return;
            }

        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _lyricsRemovalStamp) != stamp) return;
            track.Lyrics = plain ?? string.Empty;
            track.SyncedLyrics = synced ?? string.Empty;
        });
        QueueEmbeddedLyrics(trackPath, plain, synced, replaceableTagText, stamp);
    }

    /// <summary>A file the lyrics page reads before .elrc/.lrc beside the song: any .lyricsfile or
    /// .ttml, or an .elrc this app didn't write (the app's own .elrc is replaced along with the .lrc).</summary>
    private static bool HigherSidecarHidesLrc(string trackPath)
    {
        if (File.Exists(Path.ChangeExtension(trackPath, ".lyricsfile"))
            || File.Exists(Path.ChangeExtension(trackPath, ".ttml")))
            return true;
        var elrcPath = Path.ChangeExtension(trackPath, ".elrc");
        return File.Exists(elrcPath) && !SidecarRegistry.Contains(elrcPath);
    }

    // Sidecars this app created itself. RemoveLyrics deletes only these, so a user's
    // own sidecar is never removed on their behalf. The registry is persisted (JSON
    // under the data root): the old in-memory HashSet emptied on every restart, so the
    // app's own auto-written sidecar looked user-owned, Remove skipped it, and the
    // probe resurrected the removed lyrics on the next play.
    private static AppWrittenSidecarRegistry SidecarRegistry => AppWrittenSidecarRegistry.Default;

    // ── Lyric-file writer lane ──
    //
    // Every mutation of the lyric files this view-model owns — the track-side .lrc
    // sidecar (auto-persist vs the manual Save command), the lyrics-cache files, and
    // RemoveLyrics' deletes — runs through one FIFO lane. FIFO, not just mutual
    // exclusion, matters twice over: a "Try alternate" persist queued after the
    // primary's must also RUN after it, so its replace-vs-skip decision sees the
    // primary's write settled (deciding from a pre-queue File.Exists snapshot was a
    // TOCTOU that silently dropped the user's explicit switch), and a Remove issued
    // after an in-flight write must delete what that write produced instead of racing
    // it (an unawaited cache write could land after the delete and resurrect the
    // removed lyrics on the next play). Every enqueue happens on the UI thread, so
    // lane order is exactly user-visible order.
    private static readonly object _lyricsWriteQueueLock = new();
    private static Task _lyricsWriteQueue = Task.CompletedTask;

    /// <summary>Appends work to the FIFO writer lane. Internal so tests can park the
    /// lane (a blocking first item) to pin a deterministic interleaving.</summary>
    internal static Task EnqueueLyricsFileWork(Action work)
    {
        lock (_lyricsWriteQueueLock)
        {
            var task = _lyricsWriteQueue.ContinueWith(
                _ => work(), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _lyricsWriteQueue = task;
            return task;
        }
    }

    // Bumped on the UI thread at the start of RemoveLyrics. Work queued before the
    // bump re-checks it before writing files or applying track fields, so nothing
    // already in flight when the user removed the lyrics can bring them back.
    private static int _lyricsRemovalStamp;

    // Trash-operation seam (same injection style as LibraryRemovalHelper's core);
    // tests substitute a failing trash to pin the registry-retention contract.
    internal Func<string, bool> TrashSidecarFile { get; set; } = Helpers.RecycleBin.TryMoveToTrash;

    /// <summary>Writes lyrics into audio files once they are not playing (GitHub #135); set by
    /// MainWindowViewModel. Null (tests): written right away, in order on the lyrics file lane.</summary>
    internal IDeferredTagWriter? TagWriter { get; set; }

    /// <summary>Settings ▸ Lyrics ▸ Save lyrics to.</summary>
    private LyricsSaveTarget SaveTargetSetting => LyricsSaveOptions.ParseTarget(_player.LyricsSaveTarget);

    // Lyrics this page put inside audio files (file path → the text written), so a later source
    // switch may replace them and Remove may take them back. Lyrics the file had otherwise — the
    // user's own — are never replaced or removed by the page.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PageEmbeddedLyrics =
        new(PathComparison.Comparer);

    /// <summary>Lyrics text as tags compare it: LF line ends, no trailing blank space.</summary>
    private static bool SameLyricsText(string? a, string? b) =>
        string.Equals(EmbeddedLyrics.Normalize(a), EmbeddedLyrics.Normalize(b), StringComparison.Ordinal);

    /// <summary>Puts lyrics inside the audio file, but only over no lyrics, or over lyrics this page wrote there
    /// (<paramref name="replaceable"/>): a song's own tag lyrics are never replaced by an automatic save.</summary>
    private void QueueEmbeddedLyrics(string path, string? plain, string? synced, string? replaceable, int stamp)
    {
        var text = LyricsTextHelper.ContainsTimestamps(synced) ? synced! : plain ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;
        PageEmbeddedLyrics[path] = text;
        QueueLyricsTagWrite(path, () =>
        {
            // A RemoveLyrics since this was queued: it must not bring them back.
            if (Volatile.Read(ref _lyricsRemovalStamp) != stamp) return;
            var ok = _metadata.WriteLyrics(path, plain, synced,
                current => current.IsEmpty || SameLyricsText(current.Text, text)
                           || (replaceable != null && SameLyricsText(current.Text, replaceable)));
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Lyrics.Embed", $"written={ok}");
        });
    }

    private void QueueLyricsTagWrite(string path, Action write)
    {
        if (TagWriter is { } deferred)
            deferred.Enqueue(path, "lyrics", write);
        else
            _ = EnqueueLyricsFileWork(() => { try { write(); } catch { } });
    }

    /// <summary>
    /// Removes the currently displayed online lyrics: clears the cached file,
    /// resets lyrics state, and shows the search button so the user can retry.
    /// </summary>
    [RelayCommand]
    private async Task RemoveLyrics()
    {
        // The lines on screen are an unsaved Studio preview — never delete real files for them.
        if (_currentTrack == null || IsPreviewActive) return;
        // Capture the track: awaiting the writer lane frees the UI thread, so
        // _currentTrack can change (or go null) before the deletes finish.
        var track = _currentTrack;

        // Remove every artifact this view-model created for the track.
        //
        // Deleting only {Id}.lrc left the {Id}.lyricsfile cache and any sidecar written
        // next to the track — both of which the probe reads at *higher* priority — so the
        // removed lyrics came straight back on the next play. A user's own sidecar is left
        // alone: only paths recorded in the app-written sidecar registry are eligible.
        //
        // The stamp bump voids any cache/sidecar write still queued on the writer lane,
        // and the deletes join the lane behind whatever is in flight, so an unawaited
        // write can never land after the delete and resurrect the removed files.
        Interlocked.Increment(ref _lyricsRemovalStamp);
        // Removed on purpose: the automatic download must not fetch them straight back.
        _autoDownloadTried.Add(track.Id);
        var trackId = track.Id;
        var trackPath = track.FilePath;
        await EnqueueLyricsFileWork(() =>
        {
            try
            {
                foreach (var ext in new[] { ".lrc", ".lyricsfile" })
                {
                    var cachePath = Path.Combine(LyricsCacheDir, $"{trackId}{ext}");
                    if (File.Exists(cachePath))
                        File.Delete(cachePath);
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrWhiteSpace(trackPath))
                {
                    // Unregister only once the file is actually gone: the trash move
                    // can fail (file locked/in use), and dropping the registry entry
                    // first left the app's own file on disk permanently looking
                    // user-owned — Remove would then never touch it again.
                    // .ttml: Lyrics Studio's "Also save as TTML" writes one, read before the rest.
                    foreach (var ext in new[] { ".ttml", ".elrc", ".lrc" })
                    {
                        var lrcPath = Path.ChangeExtension(trackPath, ext);
                        if (!SidecarRegistry.Contains(lrcPath)) continue;
                        if (!File.Exists(lrcPath) || TrashSidecarFile(lrcPath))
                            SidecarRegistry.Remove(lrcPath);
                        else
                            DebugLogger.Error(DebugLogger.Category.Error, "Lyrics.SidecarTrashFailed", lrcPath);
                    }

                    // Lyrics the page put inside the audio file (GitHub #135) come out of the tags
                    // again — only while the tags still hold exactly those, never the song's own.
                    if (PageEmbeddedLyrics.TryRemove(trackPath, out var embedded))
                        QueueLyricsTagWrite(trackPath, () => _metadata.WriteLyrics(trackPath, null, null,
                            current => !current.IsEmpty && SameLyricsText(current.Text, embedded)));
                }
            }
            catch { }
        });

        // Clear the in-memory copies too, otherwise the next probe finds them on the
        // Track and re-displays what was just removed.
        track.Lyrics = string.Empty;
        track.SyncedLyrics = string.Empty;

        // Reset state
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _lrcRedModelResult = null;
        ResetLrcRedSync(cancel: true);
        _timeline.Reset();
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        CanSaveToFile = false;
        CanSaveTtmlToFile = false;
        _localSyncedForSave = null;
        CanRemoveLyrics = false;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        AlternateLyricsLabel = string.Empty;
        _lyricsSyncTimer.Stop();
        _lyricsSyncTimer.Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs);

        LyricLines.Clear();
        UnsyncedLines.Clear();

        // Show "no lyrics" state with search button only
        ShowSearchButton = true;

        SaveStatusText = string.Empty;
    }

    // ── Marked as having no lyrics (GitHub #162) ──

    /// <summary>Probe source of a song marked as having no lyrics: nothing is read for it.</summary>
    private const string NoLyricsSource = "NoLyrics";

    /// <summary>
    /// Marks the song on the page as having no lyrics (an instrumental): no automatic lookup
    /// searches it again, and the lyrics it has from this app go as Remove Lyrics takes them —
    /// the cache, sidecars this app wrote, lyrics this page put in the tags. The user's own
    /// lyrics files and tags stay; while marked, the page shows none of them.
    /// </summary>
    [RelayCommand]
    private async Task MarkNoLyrics()
    {
        if (_currentTrack is not { } track) return;
        SetNoLyricsMark(track, true);
        // A search in flight must not land over the mark.
        ++_searchGeneration;
        IsSearching = false;
        SearchFailedMessage = string.Empty;
        if (!IsPreviewActive) await RemoveLyrics();
        if (!ReferenceEquals(_currentTrack, track)) return;
        ShowSearchButton = false;
        IsMarkedNoLyrics = !IsPreviewActive;
    }

    /// <summary>Takes the mark off again; the page offers Search Lyrics, but nothing searches on its own until the next session.</summary>
    [RelayCommand]
    private void UnmarkNoLyrics()
    {
        if (_currentTrack is not { } track) return;
        SetNoLyricsMark(track, false);
        // As after Remove Lyrics: the automatic download doesn't fetch the junk straight back.
        _autoDownloadTried.Add(track.Id);
        LoadLyricsForTrack(track);
    }

    /// <summary>Sets the mark on the song and on the library's instance of it (the one saved), and saves.</summary>
    internal void SetNoLyricsMark(Track track, bool marked)
    {
        var changed = track.NoLyrics != marked;
        track.NoLyrics = marked;
        if (_library.GetTrackById(track.Id) is { } libraryTrack && libraryTrack.NoLyrics != marked)
        {
            libraryTrack.NoLyrics = marked;
            changed = true;
        }
        if (ReferenceEquals(_currentTrack, track) && !marked) IsMarkedNoLyrics = false;
        if (!changed) return;
        DebugLogger.Info(DebugLogger.Category.Lyrics, "NoLyricsMark", $"marked={marked}, title={track.Title}");
        _ = SaveLibraryQuietlyAsync();
    }

    private async Task SaveLibraryQuietlyAsync()
    {
        try { await _library.SaveAsync(); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "NoLyricsMark:SaveFailed", ex.Message); }
    }

    private void ShowStatusText(string text, int durationMs = 3000)
    {
        _statusClearCts?.Cancel();
        _statusClearCts?.Dispose();
        SaveStatusText = text;
        var cts = _statusClearCts = new CancellationTokenSource();
        Task.Delay(durationMs, cts.Token).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => SaveStatusText = string.Empty),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private static Task SaveLyricsToCacheAsync(Guid trackId, string lyrics)
    {
        var stamp = Volatile.Read(ref _lyricsRemovalStamp);
        return EnqueueLyricsFileWork(() =>
        {
            try
            {
                // Queued before a RemoveLyrics that has since landed — writing now
                // would re-create the cache file the user just removed.
                if (Volatile.Read(ref _lyricsRemovalStamp) != stamp) return;
                Directory.CreateDirectory(LyricsCacheDir);
                var path = Path.Combine(LyricsCacheDir, $"{trackId}.lrc");
                File.WriteAllText(path, NormalizeLyricsForLrc(lyrics), new UTF8Encoding(false));
            }
            catch { }
        });
    }

    private static Task SaveLyricsfileToCacheAsync(Guid trackId, string yamlContent)
    {
        var stamp = Volatile.Read(ref _lyricsRemovalStamp);
        return EnqueueLyricsFileWork(() =>
        {
            try
            {
                if (Volatile.Read(ref _lyricsRemovalStamp) != stamp) return;
                Directory.CreateDirectory(LyricsCacheDir);
                var path = Path.Combine(LyricsCacheDir, $"{trackId}.lyricsfile");
                File.WriteAllText(path, yamlContent, new UTF8Encoding(false));
            }
            catch { }
        });
    }

    private static string NormalizeLyricsForLrc(string lyrics)
    {
        // Keep source timestamps intact when present, just normalize line endings.
        return lyrics
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd()
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    // ── Lyrics Studio preview ──
    // Unsaved Studio lyrics shown on this page in place of the track's real ones.
    // Memory only: nothing touches disk, the online cache or the Track's lyric fields,
    // so dropping the preview and reloading brings the real lyrics straight back.
    private const string PreviewSource = "Preview";
    private Guid? _previewTrackId;
    private string? _previewText;

    /// <summary>True while the lines on screen are a Lyrics Studio preview (not saved anywhere).</summary>
    [ObservableProperty]
    private bool _isPreviewActive;

    /// <summary>
    /// Shows <paramref name="syncedText"/> (LRC, or ELRC with word tags) whenever
    /// <paramref name="track"/> is the current track, ahead of every sidecar/embedded/online
    /// source. Works before or after the track starts; calling again replaces the text.
    /// Dropped by <see cref="ClearPreview"/> or when a different track becomes current.
    /// </summary>
    public void ShowPreview(Track track, string syncedText)
    {
        _previewTrackId = track.Id;
        _previewText = syncedText;
        if (_player.CurrentTrack is { } current && current.Id == track.Id)
            LoadLyricsForTrack(current);
    }

    /// <summary>
    /// The Lyrics Studio's live preview pane (10-14): like <see cref="ShowPreview"/>, but when this
    /// track's preview is already on screen the new text is applied in place — no fade, no disk
    /// probe — so a nudge shows at once. A preview of another song is dropped first: it would stay
    /// on screen (<see cref="ShowPreview"/> only reloads the track it is given).
    /// </summary>
    public void UpdatePreview(Track track, string syncedText)
    {
        if (_previewTrackId is { } other && other != track.Id) ClearPreview();
        if (!IsPreviewActive || _previewTrackId != track.Id || _currentTrack is not { } current
            || current.Id != track.Id || _player.CurrentTrack?.Id != track.Id)
        {
            ShowPreview(track, syncedText);
            return;
        }
        if (syncedText == _previewText) return;
        _previewText = syncedText;
        var lines = ParseLrcContent(syncedText);
        if (lines.Count == 0) return;
        // A probe still in flight (an earlier reload) must not land over these lines.
        ++_searchGeneration;
        _timeline.Reset();
        ActiveLineIndex = -1;
        ApplyLocalLyricsResult(current, new LocalLyricsProbe(lines, null, PreviewSource, FromCache: false));
    }

    /// <summary>Drops the preview; if it was for the loaded track, its real lyrics reload.</summary>
    public void ClearPreview()
    {
        var previewTrackId = _previewTrackId;
        DropPreview();
        if (previewTrackId != null && _currentTrack is { } current && current.Id == previewTrackId)
            LoadLyricsForTrack(current);
    }

    private void DropPreview()
    {
        _previewTrackId = null;
        _previewText = null;
        IsPreviewActive = false;
    }

    /// <summary>
    /// Called from context menus to search lyrics for a specific track.
    /// Loads the track first, and if no local lyrics found, triggers online search;
    /// a track that already has lyrics opens the Search Lyrics dialog to pick another source.
    /// </summary>
    public void SearchLyricsForTrack(Track track)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            LoadLyricsForTrack(track);

            // The local-lyric probe is async; ShowSearchButton only becomes
            // true once it completes and finds nothing. Checking it synchronously
            // here made the auto-search below dead code — await the probe first.
            var generation = _searchGeneration;
            try { await _localProbeTask; }
            catch { /* probe failures already surface via the load path */ }
            if (generation != _searchGeneration) return; // another track took over

            // If no local lyrics were found, trigger online search automatically
            if (ShowSearchButton)
                SearchLyricsCommand.Execute(null);
            else if (!IsSearching)
                await OpenLyricsSearchCommand.ExecuteAsync(null);
        });
    }

    private void DisplayOnlineLyrics(LrcLibResult result, bool userSwitchedSource = false)
    {
        IsPreviewActive = false;
        _currentOnlineResult = result;
        LyricLines.Clear();
        UnsyncedLines.Clear();
        _timeline.Reset();
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        _lyricsSyncTimer.Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs);

        List<LyricLine>? parsedLines = null;
        string? plainForUnsync = null;

        // Priority: Lyricsfile (word-level) > syncedLyrics (LRC) > plainLyrics
        if (result.HasLyricsfile)
        {
            var (lines, plain) = LyricsfileParser.Parse(result.Lyricsfile);
            if (lines != null && lines.Count > 0)
            {
                parsedLines = lines;
                plainForUnsync = plain;
            }
        }

        if (parsedLines == null && result.HasSyncedLyrics)
        {
            parsedLines = ParseLrcContent(result.SyncedLyrics!);
        }

        if (parsedLines != null)
        {
            _hasSyncedLyrics = parsedLines.Any(l => l.IsSynced);
            IsSynced = _hasSyncedLyrics;
            HasSyncedLyricsAvailable = _hasSyncedLyrics;

            if (_hasSyncedLyrics)
            {
                LrcParser.InsertIntroPlaceholderIfNeeded(parsedLines);
                LrcParser.InsertInterludePlaceholders(parsedLines);
            }

            List<LyricLine> synced = parsedLines;
            FillLyricCollections(
                () => LyricLines.ReplaceAll(synced),
                () =>
                {
                    if (!string.IsNullOrWhiteSpace(plainForUnsync))
                        PopulateUnsyncedFromPlainText(plainForUnsync);
                    else
                        PopulateUnsyncedLines(synced);
                });
        }
        else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
        {
            var rendered = new List<LyricLine>();
            var lines = LrcParser.SplitPlain(result.PlainLyrics);
            foreach (var line in lines)
            {
                var wrapped = LrcParser.SoftWrap(line);
                rendered.Add(new LyricLine { Text = wrapped, IsActive = true });
            }
            FillLyricCollections(
                () => LyricLines.ReplaceAll(rendered),
                () => UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true })));
        }
        else
        {
            AutoSelectTab();
        }

        RefreshActiveLyricPosition();
        CanSaveToFile = CanSaveOnlineLyrics(_currentTrack, result);
        _localSyncedForSave = null;
        CanSaveTtmlToFile = CanSaveToFile;
        CanRemoveLyrics = true;
        ShowSearchButton = false;

        // Cache the downloaded lyrics for offline use. Prefer the Lyricsfile (richer);
        // fall back to LRC/plain. Cache format is detected by content on the reload path.
        if (_currentTrack != null)
        {
            if (result.HasLyricsfile)
                _ = SaveLyricsfileToCacheAsync(_currentTrack.Id, result.Lyricsfile!);

            var lrcToCache = result.SyncedLyrics ?? result.PlainLyrics;
            if (!string.IsNullOrWhiteSpace(lrcToCache))
                _ = SaveLyricsToCacheAsync(_currentTrack.Id, lrcToCache);
        }

        // Persist to sidecars + the in-memory track so any found lyrics (manual or
        // auto search) show up and stay in the Metadata editor's Plain/Synced tabs
        // without requiring a separate "Save to File" click. An explicit source
        // switch may replace an app-written sidecar so the choice sticks.
        PersistOnlineLyricsToSidecar(result, allowReplaceAppSidecar: userSwitchedSource);
        RefreshLrcRedOffer();

        // Start sync timer if synced lyrics and playing
        if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
            _lyricsSyncTimer.Start();

        // New lines, as on a track change: views re-anchor once they are laid out (GitHub #155).
        // The ScrollLineIndex change above reached them before layout, so the scroll measured
        // lines not placed yet, landed at the top and counted as done — lyrics fetched late in
        // a song sat at their first lines, the playing one far below, until the next line.
        LyricsSwapped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears all lyrics state when no track is playing.</summary>
    private void ClearLyricsState()
    {
        _currentTrack = null;
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _lrcRedModelResult = null;
        ResetLrcRedSync(cancel: true);
        _timeline.Reset();
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        CanSaveToFile = false;
        CanSaveTtmlToFile = false;
        _localSyncedForSave = null;
        CanRemoveLyrics = false;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        AlternateLyricsLabel = string.Empty;
        ShowSearchButton = false;
        IsSearching = false;
        SaveStatusText = string.Empty;
        _lyricsSyncTimer.Stop();
        _lyricsSyncTimer.Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs);

        LyricLines.Clear();
        UnsyncedLines.Clear();
        // If a swap was mid-fade when the queue ended, views are sitting at
        // opacity 0 waiting for the apply that will never come — restore them.
        LyricsSwapped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Recomputes <see cref="WrittenByText"/> from the current track's Composer
    /// tag. Empty (footer collapsed) when there is no composer or no lyrics on screen.
    /// Multi-writer tags arrive as one string — ID3v2.3-style "/" or ";" separated —
    /// and are shown comma-joined, Apple Music style. Called from the collection-changed
    /// hooks in the constructor, so it runs on the UI thread with every lyrics apply.</summary>
    private void UpdateWrittenByCredit()
    {
        var composer = _currentTrack?.Composer;
        if (string.IsNullOrWhiteSpace(composer) ||
            (LyricLines.Count == 0 && UnsyncedLines.Count == 0))
        {
            WrittenByText = string.Empty;
            return;
        }

        WrittenByText = string.Join(", ",
            composer.Split(new[] { '/', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private void OnTrackStarted(object? sender, Track track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Unsubscribe from previous track's IsFavorite changes
            if (_currentTrack != null)
                _currentTrack.PropertyChanged -= OnCurrentTrackPropertyChanged;

            LoadLyricsForTrack(track);
            RequestAdaptiveBackground();

            // Subscribe to new track's IsFavorite changes for metadata heart
            track.PropertyChanged += OnCurrentTrackPropertyChanged;
            OnPropertyChanged(nameof(ShowMetadataFavoriteHeart));
            OnPropertyChanged(nameof(ShareAvailable));

            // Start sync timer only if synced lyrics exist and sync tab is active
            if (_hasSyncedLyrics && IsSyncTabSelected)
                _lyricsSyncTimer.Start();
            else
                _lyricsSyncTimer.Stop();
        });
    }

    private void OnCurrentTrackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Track.IsFavorite))
            OnPropertyChanged(nameof(ShowMetadataFavoriteHeart));
    }

    private void OnLibraryUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshHeaderCredits(); // the album's grouped track count can change with a scan
            if (_currentTrack == null) return;

            // Reload lyrics only if the track's lyrics content actually changed
            if (_currentTrack.Lyrics != _loadedLyrics ||
                _currentTrack.SyncedLyrics != _loadedSyncedLyrics)
            {
                LoadLyricsForTrack(_currentTrack);

                if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
                    _lyricsSyncTimer.Start();
                else
                    _lyricsSyncTimer.Stop();
            }

            // A metadata edit can change Composer without touching the lyrics text —
            // the collection hooks never fire then, so refresh the credit directly.
            UpdateWrittenByCredit();
        });
    }

    /// <summary>
    /// Called when the lyrics view becomes visible. Ensures lyrics are loaded
    /// for the currently playing track (handles the case where TrackStarted
    /// fired before the user navigated to this view).
    /// </summary>
    public void EnsureLyricsForCurrentTrack()
    {
        var track = _player.CurrentTrack;
        if (track == null) return;

        if (_currentTrack?.Id != track.Id)
        {
            // Different track — full reload
            LoadLyricsForTrack(track);
            if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
                _lyricsSyncTimer.Start();
        }
        else
        {
            // Same track — re-entering the lyrics view.
            // Always sync to current position immediately so lyrics are visible right away,
            // whether playing or paused.
            if (_hasSyncedLyrics && IsSyncTabSelected)
            {
                RefreshActiveLyricPosition();

                // Restart sync timer if playing
                if (_player.State == Models.PlaybackState.Playing && !_lyricsSyncTimer.IsEnabled)
                    _lyricsSyncTimer.Start();
            }
            else
            {
                OnPropertyChanged(nameof(ActiveLyricLines));
            }
        }
    }

    private void RefreshActiveLyricPosition()
    {
        if (_hasSyncedLyrics && IsSyncTabSelected && LyricLines.Count > 0)
        {
            _timeline.Rewind();
            UpdateActiveLine(GetPlaybackPosition());
            UpdateLineOpacities(ActiveLineIndex);
            OnPropertyChanged(nameof(ActiveLineIndex));
            OnPropertyChanged(nameof(ScrollLineIndex));
        }
        else
        {
            UpdateLineOpacities(-1);
        }

        OnPropertyChanged(nameof(ActiveLyricLines));
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
            RefreshHeaderCredits();

        if (e.PropertyName is nameof(PlayerViewModel.CurrentTrack) or nameof(PlayerViewModel.LyricsCenterWhenNoLyrics))
            UpdateNoLyricsCentered();

        // A different track took over: a Studio preview belongs to its own track only.
        if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) &&
            _previewTrackId != null && _player.CurrentTrack?.Id != _previewTrackId)
            DropPreview();

        // Clear lyrics when track becomes null (queue ended)
        if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) && _player.CurrentTrack == null)
        {
            // Unsubscribe from previous track
            if (_currentTrack != null)
                _currentTrack.PropertyChanged -= OnCurrentTrackPropertyChanged;
            ClearLyricsState();
            OnPropertyChanged(nameof(ShowMetadataFavoriteHeart));
            OnPropertyChanged(nameof(ShareAvailable));
            return;
        }

        // A new track was picked, but TrackStarted (which reloads lyrics) only fires
        // once playback actually starts — until then the PREVIOUS track's no-lyrics
        // state kept the "Search Lyrics" button visible for a beat while the rest of
        // the page already showed the new track. Hide it the moment the track changes;
        // the full lyric load still lands via OnTrackStarted.
        if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) &&
            _player.CurrentTrack is { } incomingTrack &&
            !ReferenceEquals(incomingTrack, _currentTrack) &&
            (ShowSearchButton || IsSearching))
        {
            ShowSearchButton = false;
            IsSearching = false;
            SearchFailedMessage = string.Empty;
        }

        // Manage the sync timer based on playback state changes.
        // Only run the timer when synced tab is active.
        if (e.PropertyName == nameof(PlayerViewModel.State))
        {
            if (_player.State == Models.PlaybackState.Playing && _hasSyncedLyrics && IsSyncTabSelected)
                _lyricsSyncTimer.Start();
            else
                _lyricsSyncTimer.Stop();
        }
        // Also update on Position PropertyChanged — but ONLY when the timer is NOT
        // running. This catches the final position at end-of-track (when playback
        // stops and the timer is no longer ticking) without duplicating work during
        // normal playback.
        else if (e.PropertyName == nameof(PlayerViewModel.Position) && _hasSyncedLyrics
                 && !_lyricsSyncTimer.IsEnabled)
        {
            UpdateActiveLine(GetPlaybackPosition());
        }
        // Update adaptive background when album art loads/changes
        else if (e.PropertyName == nameof(PlayerViewModel.AlbumArt))
        {
            RequestAdaptiveBackground();
        }
        // Fullscreen-focus setting flipped while lyrics are showing — re-dim in place.
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsFullScreenFocusEnabled))
        {
            OnPropertyChanged(nameof(IsLyricsFocusActive));
            RefreshFocusDimming();
        }
        // Glow & lift flipped: the running lines settle (off) or pick it up next frame (on).
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsGlowAndLift))
        {
            _timeline.WordMotion = _player.LyricsGlowAndLift;
        }
        // Minimum line opacity moved: re-floor the ramp in place.
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsMinLineOpacity))
        {
            RefreshFocusDimming();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsAutoDownload))
        {
            MaybeAutoDownloadLyrics();
        }
        // Word-joining flipped: it changes how the sidecar is parsed, not how it is
        // drawn, so the current track has to go back through the load path.
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsJoinSplitWords))
        {
            if (_currentTrack is { } track)
                LoadLyricsForTrack(track);
        }
        // Layer toggles only change what is drawn — re-evaluate the row bindings, no reload.
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsShowTranslations))
        {
            OnPropertyChanged(nameof(ShowTranslations));
        }
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsShowRomanization))
        {
            OnPropertyChanged(nameof(ShowRomanization));
        }
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsShowBackgroundVocals))
        {
            OnPropertyChanged(nameof(ShowBackgroundVocals));
        }
        // The engine just reported a music video's audio whose length is off the song's,
        // after these lyrics opened on Synced: move to Plain once (see OpensOnSyncedTab).
        else if (e.PropertyName == nameof(PlayerViewModel.MusicVideoAudioLengthDiffers) &&
                 _player.MusicVideoAudioLengthDiffers && IsSyncTabSelected && HasSyncedLyricsAvailable)
        {
            SelectUnsyncTab();
        }
    }

    /// <summary>Raised on the UI thread when a lyric reload has its result ready and
    /// is about to be applied; views fade their lyrics host out over
    /// <see cref="LyricsSwapFadeOutMs"/> so the wholesale swap lands off-screen.</summary>
    public event EventHandler? LyricsSwapPending;

    /// <summary>Raised on the UI thread right after the lyric collections were
    /// swapped (or cleared); views re-anchor their scroll and fade back in.</summary>
    public event EventHandler? LyricsSwapped;

    /// <summary>How long views get to fade out after <see cref="LyricsSwapPending"/>.</summary>
    public const int LyricsSwapFadeOutMs = 130;

    private void LoadLyricsForTrack(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LoadLyricsForTrack", $"title={track.Title}, id={track.Id}");
        // A model sync for another song stops; one for this song (a reload) may still land.
        ResetLrcRedSync(cancel: _lrcRedSyncTrackId is { } syncing && syncing != track.Id);
        _currentTrack = track;
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _lrcRedModelResult = null;
        ShowSearchButton = false;
        SearchFailedMessage = string.Empty;
        IsSearching = false;
        CanSaveToFile = false;
        CanSaveTtmlToFile = false;
        _localSyncedForSave = null;
        CanRemoveLyrics = false;
        IsMarkedNoLyrics = false;
        SaveStatusText = string.Empty;
        IsAutoFollowPaused = false;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        AlternateLyricsLabel = string.Empty;
        var generation = ++_searchGeneration;

        // Deliberately NOT clearing LyricLines/UnsyncedLines here: the previous
        // track's lines stay frozen on screen while the probe runs, and the apply
        // swaps them wholesale behind the views' fade — the upfront clear was the
        // blank flash in the track-change flicker. Every populated apply path uses
        // ReplaceAll; the no-lyrics path clears explicitly.
        _timeline.Reset();
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        _lyricsSyncTimer.Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs);

        // Fire-and-forget: all file I/O runs off the UI thread, result is posted back.
        // The task is kept so SearchLyricsForTrack can await the probe's outcome.
        _localProbeTask = LoadLocalLyricsAsync(track, generation,
            _previewTrackId == track.Id ? _previewText : null);
    }

    // Completes only after the probe result has been APPLIED on the UI thread —
    // not merely posted — so ShowSearchButton is trustworthy after awaiting it.
    private Task _localProbeTask = Task.CompletedTask;

    /// <summary>
    /// Probes local lyric sources in priority order off the UI thread, applying the result
    /// via <see cref="Dispatcher.UIThread.Post"/>. Guarded by <see cref="_searchGeneration"/>
    /// so stale results from a previous track can't overwrite the current track's lyrics.
    ///
    /// Priority: .lyricsfile sidecar → .ttml sidecar → .lrc sidecar → embedded tags → cache file.
    /// </summary>
    private async Task LoadLocalLyricsAsync(Track track, int generation, string? previewText = null)
    {
        // Read the parse-affecting setting here, on the UI thread that owns it.
        var joinSplitWords = _player.LyricsJoinSplitWords;
        // TTML files may carry several translations; the UI language picks one (issue #78).
        var uiLanguage = Localization.Loc.Instance.Culture.Name;

        // Fade the (still-visible) old lyrics out WHILE the probe runs, not after
        // it. The swap below — a full ItemsControl rebuild plus a scroll snap —
        // must land off-screen, but the fade needs no probe result; waiting for
        // one first (the old order) added the whole sidecar/store disk read to
        // the time the previous track's lyrics stayed up after the new track had
        // already started. Measured: LyricsTrackChangeTimingTests.
        var fadeOut = Task.CompletedTask;
        if (LyricsSwapPending != null)
        {
            if (Dispatcher.UIThread.CheckAccess())
                LyricsSwapPending?.Invoke(this, EventArgs.Empty);
            else
                await Dispatcher.UIThread.InvokeAsync(() => LyricsSwapPending?.Invoke(this, EventArgs.Empty));
            fadeOut = Task.Delay(LyricsSwapFadeOutMs);
        }

        var probeClock = System.Diagnostics.Stopwatch.StartNew();
        var probe = await Task.Run(() =>
        {
            // Track lyrics are store-backed and lazy: the first touch is a small
            // disk read, so capture the change-detection baselines here, off the
            // UI thread, instead of in LoadLyricsForTrack (this also warms the
            // store's LRU for the UI-thread reads in ApplyLocalLyricsResult).
            _loadedLyrics = track.Lyrics;
            _loadedSyncedLyrics = track.SyncedLyrics;
            // A Lyrics Studio preview outranks every real source; it goes through the
            // same parser as an .elrc/.lrc sidecar, so it renders exactly as saved would.
            if (previewText != null)
            {
                var previewLines = ParseLrcContent(previewText);
                if (previewLines.Count > 0)
                    return new LocalLyricsProbe(previewLines, null, PreviewSource, FromCache: false);
            }
            // Marked as having no lyrics (GitHub #162): whatever lies around for it is not shown.
            if (track.NoLyrics)
                return new LocalLyricsProbe(null, null, NoLyricsSource, FromCache: false);
            return ProbeLocalLyricSources(track, joinSplitWords, uiLanguage);
        });
        // Visible in Settings ▸ About ▸ Developer Mode ▸ Copy Logs: how long the
        // disk probe took on THIS machine (the fade hides up to LyricsSwapFadeOutMs of it).
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LocalProbe",
            $"ms={probeClock.ElapsedMilliseconds}, source={probe.Source}");

        if (generation != _searchGeneration) return;
        await fadeOut;
        if (generation != _searchGeneration) return;

        var applied = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (generation == _searchGeneration)
                {
                    // Developer Mode timing (same channel as LocalProbe above): the
                    // synchronous swap, then the wait until the layout pass that
                    // realizes the new line controls has run — Loaded-priority work
                    // runs after layout. Off → no stopwatch, no extra post.
                    var swapClock = DebugLogger.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                    ApplyLocalLyricsResult(track, probe);
                    LyricsSwapped?.Invoke(this, EventArgs.Empty);
                    if (swapClock != null)
                    {
                        var swapMs = swapClock.ElapsedMilliseconds;
                        var lines = IsSyncTabSelected ? LyricLines.Count : UnsyncedLines.Count;
                        Dispatcher.UIThread.Post(() =>
                            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsSwap",
                                $"swap ms={swapMs} layout ms={swapClock.ElapsedMilliseconds} lines={lines}"),
                            DispatcherPriority.Loaded);
                    }
                }
            }
            finally
            {
                applied.SetResult();
            }
        });
        await applied.Task;
    }

    /// <param name="SyncedText">The source as LRC/ELRC text, for "Save Lyrics to File ▸ TTML"; null for a .ttml or a preview.</param>
    internal readonly record struct LocalLyricsProbe(
        List<LyricLine>? Lines,
        string? UnsyncedPlain,
        string Source,
        bool FromCache,
        string? SyncedText = null);

    /// <summary>Synchronous probe helper — must only be called off the UI thread.</summary>
    internal static LocalLyricsProbe ProbeLocalLyricSources(Track track, bool joinSplitWords, string uiLanguage)
    {
        // Priority 1: .lyricsfile sidecar (word-level, LRCGET v2.0+).
        try
        {
            var sidecarYaml = TryReadSidecar(track.FilePath, new[] { ".lyricsfile", ".Lyricsfile", ".LYRICSFILE" });
            if (sidecarYaml != null)
            {
                var (lines, plain) = LyricsfileParser.Parse(sidecarYaml);
                if (lines != null && lines.Count > 0)
                    return new LocalLyricsProbe(lines, plain, "Sidecar:Lyricsfile", FromCache: false, LyricsfileParser.ToElrc(sidecarYaml));
            }
        }
        catch { }

        // Priority 2: .ttml sidecar (word- or line-level, Apple Music style).
        try
        {
            var sidecarTtml = TryReadSidecar(track.FilePath, new[] { ".ttml", ".TTML", ".Ttml" });
            if (sidecarTtml != null)
            {
                var (lines, plain) = TtmlParser.Parse(sidecarTtml, joinSplitWords, uiLanguage);
                if (lines != null && lines.Count > 0)
                    return new LocalLyricsProbe(lines, plain, "Sidecar:Ttml", FromCache: false);
            }
        }
        catch { }

        // Priority 3a: .elrc sidecar (enhanced LRC with word tags — what Lyrics Studio writes).
        try
        {
            var sidecarElrc = TryReadSidecar(track.FilePath, new[] { ".elrc", ".ELRC", ".Elrc" });
            if (sidecarElrc != null)
            {
                var lines = ParseLrcContent(sidecarElrc);
                if (lines.Count > 0)
                    return new LocalLyricsProbe(lines, null, "Sidecar:Elrc", FromCache: false, sidecarElrc);
            }
        }
        catch { }

        // Priority 3: .lrc sidecar (line-level).
        try
        {
            var sidecarLrc = TryReadSidecar(track.FilePath, new[] { ".lrc", ".LRC", ".Lrc" });
            if (sidecarLrc != null)
            {
                var lines = ParseLrcContent(sidecarLrc);
                if (lines.Count > 0)
                    return new LocalLyricsProbe(lines, null, "Sidecar:Lrc", FromCache: false, sidecarLrc);
            }
        }
        catch { }

        // Priority 4: the song's own lyrics. The row's fields first: the file's lyrics as scanned,
        // or lyrics saved for the song since.
        var hasSyncedField = !string.IsNullOrWhiteSpace(track.SyncedLyrics);
        var hasPlainField = !string.IsNullOrWhiteSpace(track.Lyrics);
        // A whole TTML document as the song's lyrics text (pasted into a tagger's lyrics field,
        // GitHub #132) is Apple's timed lyrics, not words to print: it showed as its markup.
        if (!hasSyncedField && ProbeTtmlText(track.Lyrics, joinSplitWords, uiLanguage) is { } ttmlField)
            return ttmlField;
        // Without timed lyrics there, the file's tags are read now (GitHub #135): a SYLT frame,
        // a SYNCEDLYRICS comment or an UNSYNCEDLYRICS alias was never scanned before #135, and
        // a library is not rescanned until its files change. Before the online cache, which
        // must not shadow the file's own lyrics.
        if (!hasSyncedField && !LrcParser.ContainsTimestamp(track.Lyrics)
            && ProbeEmbeddedTags(track, hasPlainField ? track.Lyrics : null, joinSplitWords, uiLanguage) is { } fromTags)
            return fromTags;
        if (hasSyncedField || hasPlainField)
            return new LocalLyricsProbe(null, null, "Embedded", FromCache: false);

        // Priority 5: online cache (Lyricsfile preferred, fall back to .lrc).
        try
        {
            var cachedYaml = TryReadCacheFile(track.Id, ".lyricsfile");
            if (cachedYaml != null)
            {
                var (lines, plain) = LyricsfileParser.Parse(cachedYaml);
                if (lines != null && lines.Count > 0)
                    return new LocalLyricsProbe(lines, plain, "Cache:Lyricsfile", FromCache: true, LyricsfileParser.ToElrc(cachedYaml));
            }

            var cachedLrc = TryReadCacheFile(track.Id, ".lrc");
            if (cachedLrc != null)
            {
                if (cachedLrc.Contains('[') && LrcParser.ContainsTimestamp(cachedLrc))
                {
                    var lines = ParseLrcContent(cachedLrc);
                    if (lines.Count > 0)
                        return new LocalLyricsProbe(lines, null, "Cache:Lrc", FromCache: true, cachedLrc);
                }
                return new LocalLyricsProbe(null, cachedLrc, "Cache:Plain", FromCache: true);
            }
        }
        catch { }

        return new LocalLyricsProbe(null, null, "None", FromCache: false);
    }

    /// <summary>
    /// The lyrics in the audio file's tags that the row's fields lack: timed lyrics (shown with
    /// <paramref name="rowPlain"/> on the Plain tab when given), or plain text when the row has
    /// none. Null when the file adds nothing, or is not a local file that exists.
    /// </summary>
    private static LocalLyricsProbe? ProbeEmbeddedTags(Track track, string? rowPlain, bool joinSplitWords, string uiLanguage)
    {
        var path = track.FilePath;
        if (track.SourceType != SourceType.Local || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var embedded = EmbeddedLyrics.ReadFile(path);
        if (embedded.IsEmpty) return null;

        if (string.IsNullOrWhiteSpace(rowPlain) && ProbeTtmlText(embedded.Text, joinSplitWords, uiLanguage) is { } ttml)
            return ttml;

        var timed = embedded.TimedText;
        if (!string.IsNullOrWhiteSpace(timed))
        {
            var lines = ParseLrcContent(timed);
            if (lines.Any(l => l.IsSynced))
            {
                var plain = !string.IsNullOrWhiteSpace(rowPlain) ? rowPlain : embedded.PlainText;
                return new LocalLyricsProbe(lines, string.IsNullOrWhiteSpace(plain) ? null : plain, "Embedded:Tags", FromCache: false, timed);
            }
        }
        if (!string.IsNullOrWhiteSpace(rowPlain)) return null;
        var text = embedded.PlainText;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var rows = LrcParser.SplitPlain(text).Select(l => new LyricLine { Text = LrcParser.SoftWrap(l), IsActive = true }).ToList();
        return rows.Count == 0 ? null : new LocalLyricsProbe(rows, text, "Embedded:Tags", FromCache: false);
    }

    /// <summary>
    /// Lyrics text that is a TTML document, read as a .ttml beside the song would be (word and
    /// line timing, singers, layers); its LRC form backs "Save Lyrics to File". Null when the
    /// text is not TTML or does not parse.
    /// </summary>
    private static LocalLyricsProbe? ProbeTtmlText(string? text, bool joinSplitWords, string uiLanguage)
    {
        if (!TtmlParser.LooksLikeTtml(text)) return null;
        var (lines, plain) = TtmlParser.Parse(text, joinSplitWords, uiLanguage);
        if (lines is not { Count: > 0 }) return null;
        return new LocalLyricsProbe(lines, plain, "Embedded:Ttml", FromCache: false,
            TtmlParser.ToLrc(text, joinSplitWords, uiLanguage));
    }

    private void ApplyLocalLyricsResult(Track track, LocalLyricsProbe probe)
    {
        IsPreviewActive = probe.Source == PreviewSource;
        if (probe.Lines != null && probe.Lines.Count > 0)
        {
            DebugLogger.Info(DebugLogger.Category.Lyrics, probe.Source, $"lines={probe.Lines.Count}");

            _hasSyncedLyrics = probe.Lines.Any(l => l.IsSynced);
            IsSynced = _hasSyncedLyrics;
            HasSyncedLyricsAvailable = _hasSyncedLyrics;

            if (!_hasSyncedLyrics)
            {
                foreach (var line in probe.Lines)
                    line.IsActive = true;
            }
            else
            {
                LrcParser.InsertIntroPlaceholderIfNeeded(probe.Lines);
                LrcParser.InsertInterludePlaceholders(probe.Lines);
            }

            List<LyricLine> synced = probe.Lines;
            var unsyncedPlain = probe.UnsyncedPlain;
            FillLyricCollections(
                () => LyricLines.ReplaceAll(synced),
                () =>
                {
                    if (!string.IsNullOrWhiteSpace(unsyncedPlain))
                        PopulateUnsyncedFromPlainText(unsyncedPlain);
                    else
                        PopulateUnsyncedLines(synced);
                });

            LyricsSourceName = string.Empty;
            if (probe.FromCache) CanRemoveLyrics = true;
            OfferTtmlSave(track, _hasSyncedLyrics ? probe.SyncedText : null);
            RefreshActiveLyricPosition();

            if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
                _lyricsSyncTimer.Start();
            return;
        }

        if (probe.Source == "Embedded")
        {
            LoadEmbeddedLyrics(track);
            OfferTtmlSave(track, _localSyncedForSave);
            if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
                _lyricsSyncTimer.Start();
            return;
        }

        if (probe.Source == "Cache:Plain" && !string.IsNullOrWhiteSpace(probe.UnsyncedPlain))
        {
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Source:CachePlain", $"trackId={track.Id}");
            var split = LrcParser.SplitPlain(probe.UnsyncedPlain);
            var rendered = new List<LyricLine>(split.Length);
            foreach (var line in split)
                rendered.Add(new LyricLine { Text = LrcParser.SoftWrap(line), IsActive = true });
            FillLyricCollections(
                () => LyricLines.ReplaceAll(rendered),
                () => UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true })));
            LyricsSourceName = string.Empty;
            CanRemoveLyrics = true;
            RefreshActiveLyricPosition();
            return;
        }

        // No lyrics found — the stale lines were left up during the probe
        // (deferred clear), so empty the collections here, then show search only.
        LyricLines.Clear();
        UnsyncedLines.Clear();
        if (probe.Source == NoLyricsSource)
        {
            // "Marked as no lyrics" with Undo instead of the search pill; no automatic search.
            IsMarkedNoLyrics = true;
            AutoSelectTab();
            RefreshActiveLyricPosition();
            return;
        }
        DebugLogger.Warn(DebugLogger.Category.Lyrics, "NoLyricsFound", $"title={track.Title}, artist={track.Artist}");
        // The automatic download may start right here; until it answers the lyrics are
        // still loading, so the centred layout waits for its outcome.
        _holdNoLyricsCentered = true;
        try
        {
            ShowSearchButton = true;
            AutoSelectTab();
            RefreshActiveLyricPosition();
            MaybeAutoDownloadLyrics();
        }
        finally
        {
            _holdNoLyricsCentered = false;
        }
        UpdateNoLyricsCentered();
    }

    // Songs the automatic download already searched for this session: a song with no lyrics
    // online is not searched again every time its lyrics are opened.
    private readonly HashSet<Guid> _autoDownloadTried = new();

    /// <summary>
    /// Settings ▸ Lyrics ▸ "Automatically download lyrics" (GitHub #135): when a lyrics view is
    /// open on a song for which nothing was found — no lyrics file, nothing in its tags, nothing
    /// cached — search online as the Search Lyrics button would. What it finds is saved per
    /// "Save lyrics to", by the same path the button's results take; that path never replaces
    /// lyrics the song already has. Once per song per session.
    /// </summary>
    private void MaybeAutoDownloadLyrics()
    {
        if (!_player.LyricsAutoDownload || !IsAnyLyricsSurfaceVisible) return;
        if (_currentTrack is not { } track || !ShowSearchButton || IsSearching || IsPreviewActive) return;
        if (track.NoLyrics) return;
        if (!_autoDownloadTried.Add(track.Id)) return;
        DebugLogger.Info(DebugLogger.Category.Lyrics, "AutoDownload", $"title={track.Title}");
        SearchLyricsCommand.Execute(null);
    }

    /// <summary>Applies embedded SyncedLyrics / Lyrics tags to the collections (in-memory, no I/O).</summary>
    private void LoadEmbeddedLyrics(Track track)
    {
        var hasSyncedField = !string.IsNullOrWhiteSpace(track.SyncedLyrics);
        var hasPlainField = !string.IsNullOrWhiteSpace(track.Lyrics);

        // Legacy check: plain Lyrics field may contain LRC timestamps
        var plainIsActuallyLrc = hasPlainField
                                 && !hasSyncedField
                                 && track.Lyrics.Contains('[')
                                 && LrcParser.ContainsTimestamp(track.Lyrics);

        DebugLogger.Info(DebugLogger.Category.Lyrics, "Source:Embedded",
            $"synced={hasSyncedField}, plain={hasPlainField}, lrcInPlain={plainIsActuallyLrc}");

        var syncedSource = hasSyncedField ? track.SyncedLyrics
                         : plainIsActuallyLrc ? track.Lyrics
                         : null;

        // Build first, fill once below (FillLyricCollections) — a null fill leaves
        // that collection untouched, as before.
        List<LyricLine>? syncedLines = null;
        Action? fillUnsynced = null;

        if (!string.IsNullOrWhiteSpace(syncedSource))
        {
            var parsedLines = ParseLrcContent(syncedSource);
            _hasSyncedLyrics = parsedLines.Any(l => l.IsSynced);
            IsSynced = _hasSyncedLyrics;
            HasSyncedLyricsAvailable = _hasSyncedLyrics;
            if (_hasSyncedLyrics) _localSyncedForSave = syncedSource;

            if (_hasSyncedLyrics)
            {
                LrcParser.InsertIntroPlaceholderIfNeeded(parsedLines);
                LrcParser.InsertInterludePlaceholders(parsedLines);
            }

            syncedLines = parsedLines;
            fillUnsynced = () => PopulateUnsyncedLines(parsedLines);
        }

        if (hasPlainField && !plainIsActuallyLrc)
        {
            var split = LrcParser.SplitPlain(track.Lyrics);
            if (!hasSyncedField)
            {
                var rendered = new List<LyricLine>(split.Length);
                foreach (var line in split)
                    rendered.Add(new LyricLine { Text = LrcParser.SoftWrap(line), IsActive = true });
                syncedLines = rendered;
                fillUnsynced = () => UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true }));
            }
            else
            {
                var unsynced = new List<LyricLine>(split.Length);
                foreach (var line in split)
                    unsynced.Add(new LyricLine { Text = LrcParser.SoftWrap(line), IsActive = true });
                fillUnsynced = () => UnsyncedLines.ReplaceAll(unsynced);
            }
        }

        FillLyricCollections(
            () => { if (syncedLines != null) LyricLines.ReplaceAll(syncedLines); },
            () => fillUnsynced?.Invoke());
        LyricsSourceName = string.Empty;
        RefreshActiveLyricPosition();
    }

    /// <summary>Reads the first matching sidecar file for a track; returns null on any failure or no match.</summary>
    private static string? TryReadSidecar(string trackFilePath, string[] extensions)
    {
        var dir = Path.GetDirectoryName(trackFilePath);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(trackFilePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(nameWithoutExt)) return null;

        foreach (var ext in extensions)
        {
            var path = Path.Combine(dir, nameWithoutExt + ext);
            if (File.Exists(path))
                return ReadTextDetectingEncoding(path);
        }
        return null;
    }

    private static string? TryReadCacheFile(Guid trackId, string extension)
    {
        try
        {
            var path = Path.Combine(LyricsCacheDir, trackId + extension);
            if (File.Exists(path))
                return ReadTextDetectingEncoding(path);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Reads a lyrics file, falling back off UTF-8 when the bytes aren't valid UTF-8.
    ///
    /// File.ReadAllText assumes UTF-8 (with BOM sniffing). Shift-JIS / GB18030 / CP1251
    /// .lrc files are extremely common in the wild, and their bytes decode to U+FFFD —
    /// which CleanDisplayText then strips, so instead of mojibake the user got
    /// timestamped but completely empty lines, with no error anywhere.
    /// </summary>
    private static string ReadTextDetectingEncoding(string path)
        => LyricsTextDecoder.Decode(File.ReadAllBytes(path));

    private void PopulateUnsyncedLines(List<LyricLine> sourceLyrics)
    {
        var batch = new List<LyricLine>(sourceLyrics.Count);
        foreach (var line in sourceLyrics)
        {
            // Skip the "..." intro / interlude placeholders
            if (line.IsIntroPlaceholder || line.Timestamp == TimeSpan.Zero && line.Text == "...") continue;
            // The romanization / translation layers (TTML, or LRC lines sharing a timestamp —
            // GitHub #116) are the user's text too; the Plain tab renders them as static rows.
            batch.Add(new LyricLine
            {
                Text = LyricsTextHelper.CleanDisplayText(line.Text),
                Transliteration = line.Transliteration,
                Translation = line.Translation,
                IsActive = true,
            });
        }
        UnsyncedLines.ReplaceAll(batch);
    }

    /// <summary>Populates the Unsync tab from a Lyricsfile's `plain` block (preserves blank-line spacing).</summary>
    private void PopulateUnsyncedFromPlainText(string plain)
    {
        var split = LrcParser.SplitPlain(plain);
        var batch = new List<LyricLine>(split.Length);
        foreach (var line in split)
            batch.Add(new LyricLine { Text = LrcParser.SoftWrap(line), IsActive = true });
        UnsyncedLines.ReplaceAll(batch);
    }

    /// <summary>
    /// Fills both lyric collections with ONE rebuild of the visible list. Views bind
    /// <see cref="ActiveLyricLines"/> (one collection at a time) and a ReplaceAll on
    /// the bound one rebuilds every line control. Filling both and THEN flipping the
    /// tab rebuilt twice on a synced↔plain change (old tab's list, then the new one);
    /// flipping first would rebuild the new tab's STALE lines, then the fresh ones.
    /// So: fill the collection the new content will show, flip (AutoSelectTab — reads
    /// only <see cref="OpensOnSyncedTab"/>, from <see cref="_hasSyncedLyrics"/>, which
    /// callers set for the NEW content first), then fill the other one once it's off-screen.
    /// </summary>
    private void FillLyricCollections(Action fillSynced, Action fillUnsynced)
    {
        if (OpensOnSyncedTab)
        {
            fillSynced();
            AutoSelectTab();
            fillUnsynced();
        }
        else
        {
            fillUnsynced();
            AutoSelectTab();
            fillSynced();
        }
    }

    /// <summary>Synced lyrics open on the Synced tab, unless a music video's audio is playing
    /// whose length is off the song's: its timings would drift, so Plain is the default then
    /// (Synced stays one click away).</summary>
    private bool OpensOnSyncedTab => _hasSyncedLyrics && !_player.MusicVideoAudioLengthDiffers;

    private void AutoSelectTab()
    {
        if (OpensOnSyncedTab)
        {
            IsSyncTabSelected = true;
            IsUnsyncTabSelected = false;
        }
        else
        {
            IsSyncTabSelected = false;
            IsUnsyncTabSelected = true;
        }
    }

    // Internal for tests (InternalsVisibleTo Noctis.Tests); the rules live in Core so the
    // phone parses LRC identically.
    internal static List<LyricLine> ParseLrcContent(string content) => LrcParser.Parse(content);

    /// <summary>Whether the lyric share-card entry point should be visible.</summary>
    public bool ShareAvailable => _player.CurrentTrack != null && !IsSearching && !ShowSearchButton && !IsMarkedNoLyrics;

    partial void OnShowSearchButtonChanged(bool value)
    {
        OnPropertyChanged(nameof(ShareAvailable));
        UpdateNoLyricsCentered();
    }
    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShareAvailable));
        OnPropertyChanged(nameof(NoLyricsSearchTip));
    }

    /// <summary>
    /// Opens the share-card dialog with the current lyrics, pre-selecting the
    /// active line so the snapshot starts where the song currently is.
    /// </summary>
    [RelayCommand]
    private async Task ShareLyrics()
    {
        var track = _player.CurrentTrack;
        if (track == null) return;

        // Prefer the synced lines whenever they carry timestamps so the share dialog's
        // playback-sync (follow-along) works, even if the plain tab is currently showing.
        bool hasSynced = LyricLines.Count > 0 && LyricLines.Any(l => l.Timestamp.HasValue);
        var source = hasSynced ? (IEnumerable<LyricLine>)LyricLines : UnsyncedLines;
        var shareable = source
            .Where(l => !l.IsIntroPlaceholder && !string.IsNullOrWhiteSpace(l.Text))
            .ToList();
        if (shareable.Count == 0) return;

        int preselect = _timeline.ActiveLine is { } active ? shareable.IndexOf(active) : 0;
        if (preselect < 0) preselect = 0;

        var vm = new LyricShareViewModel(
            track,
            shareable.Select(l => l.Text).ToList(),
            shareable.Select(l => l.Timestamp).ToList(),
            _player,
            shareable.Select(l => l.Words).ToList(),
            shareable.Select(l => l.EndTimestamp).ToList(),
            preselect);
        await Views.LyricShareDialog.ShowAsync(vm);
    }

    /// <summary>
    /// Opens the LRC sync editor for the current track: tap-to-sync timestamps
    /// while the song plays, plus per-line nudge buttons. Reloads lyrics on save.
    /// </summary>
    [RelayCommand]
    private async Task OpenLrcEditor()
    {
        var track = _player.CurrentTrack;
        if (track == null) return;

        // Seed from what is actually on screen. _loadedSyncedLyrics and track.SyncedLyrics
        // both come from the track's embedded tags, and the probe never writes a
        // .lrc/.ttml/.lyricsfile sidecar back onto the Track — so for a sidecar-sourced
        // track the editor opened with *unsynced* text, and its save then replaced a
        // complete (possibly word-timed) sidecar with however many lines got stamped.
        var synced = BuildSeedFromLoadedLines()
                     ?? (!string.IsNullOrWhiteSpace(_loadedSyncedLyrics) ? _loadedSyncedLyrics : track.SyncedLyrics);
        var plain = !string.IsNullOrWhiteSpace(_loadedLyrics) ? _loadedLyrics : track.Lyrics;
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
        {
            ShowStatusText("No lyrics to sync — search lyrics first", 4000);
            return;
        }

        var vm = new LrcEditorViewModel(track, _player, _metadata, synced, plain) { TagWriter = TagWriter };

        vm.Saved += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_player.CurrentTrack == track)
                LoadLyricsForTrack(track);
        });
        await Views.LrcEditorDialog.ShowAsync(vm);
    }

    /// <summary>
    /// Serializes the currently displayed synced lines back to LRC so the editor can
    /// retime them, whatever source they came from. Returns null when nothing synced is
    /// loaded. Display text carries the soft-wrap newline SoftWrapText inserted, which is
    /// unfolded back to the space it replaced.
    /// </summary>
    internal string? BuildSeedFromLoadedLines()
    {
        if (!_hasSyncedLyrics || LyricLines.Count == 0) return null;

        var lines = LyricLines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.Text != "...")
            .ToList();

        // Written back in the LRC shape each layer loads from: word tags, "v2:"/"v3:" voices,
        // the "[bg: …]" vocal, romanization / translation at the line's own timestamp (GitHub
        // #116). Plain "[mm:ss.xx]text" lost all but the last, so a save from the editor —
        // a line untouched, even — turned a word-timed sidecar into line timing.
        return lines.Any(l => l.Timestamp.HasValue)
            ? TtmlParser.ToLrc(lines)
            : null;
    }

    /// <summary>
    /// Seeks playback to the timestamp of a clicked lyric line.
    /// </summary>
    [RelayCommand]
    private void SeekToLine(LyricLine? line)
    {
        if (line?.Timestamp == null || _player.Duration.TotalSeconds <= 0) return;
        // Clicking a line is an explicit "go here" — resume auto-follow so the list
        // snaps to the new active line. Without this, a prior mouse-wheel scroll leaves
        // IsAutoFollowPaused=true and the seek looks like it did nothing.
        IsAutoFollowPaused = false;
        // Land where the line is shown: with a lyrics offset that is its timestamp plus the
        // offset (the seek clamps a result before 0:00 or past the end).
        _player.SeekToPositionCommand.Execute(
            (line.Timestamp.Value + LyricsOffset).TotalSeconds / _player.Duration.TotalSeconds);
    }

    // ── Per-track lyrics offset (GitHub #102) ──
    // Ctrl/⌘+wheel over the lyrics shifts them half a second per notch: wheel up shows
    // them earlier, down later. Kept on the Track (library.json, like VolumeAdjust) and
    // applied only where the timeline is read (UpdateActiveLine / SeekToLine), so it
    // works for every source and neither lyric files nor parsed timestamps change.
    internal const int LyricsOffsetStepMs = 500;
    internal const int MaxLyricsOffsetMs = 30_000;
    private const int LyricsOffsetSaveDebounceMs = 1000;
    private CancellationTokenSource? _lyricsOffsetSaveCts;
    private double _lyricsOffsetWheelRemainder;

    /// <summary>Shows a transient app-wide notice (the glass pill); set by MainWindowViewModel.</summary>
    internal Action<string>? ShowNotice { get; set; }

    private TimeSpan LyricsOffset => TimeSpan.FromMilliseconds(_currentTrack?.LyricsOffsetMs ?? 0);

    /// <summary>
    /// Shifts the current track's lyrics one step per wheel notch (positive delta = wheel
    /// up = earlier). Returns false, leaving the wheel to scroll, when no synced lyrics are
    /// on screen to shift.
    /// </summary>
    internal bool NudgeLyricsOffset(double wheelDelta)
    {
        if (_currentTrack is not { } track || !_hasSyncedLyrics || !IsSyncTabSelected || wheelDelta == 0)
            return false;

        // Hi-res wheels and touchpads report fractions of a notch: step once per whole one.
        if (Math.Sign(wheelDelta) != Math.Sign(_lyricsOffsetWheelRemainder))
            _lyricsOffsetWheelRemainder = 0;
        _lyricsOffsetWheelRemainder += wheelDelta;
        var notches = (int)_lyricsOffsetWheelRemainder;
        if (notches == 0) return true;
        _lyricsOffsetWheelRemainder -= notches;

        var offset = Math.Clamp(track.LyricsOffsetMs - notches * LyricsOffsetStepMs,
            -MaxLyricsOffsetMs, MaxLyricsOffsetMs);
        if (offset != track.LyricsOffsetMs)
        {
            track.LyricsOffsetMs = offset;
            // The queue can hold a pre-reload instance of the song; the library's is the one saved.
            if (_library.GetTrackById(track.Id) is { } libraryTrack)
                libraryTrack.LyricsOffsetMs = offset;
            UpdateActiveLine(GetPlaybackPosition());
            QueueLyricsOffsetSave();
        }

        ShowNotice?.Invoke(offset switch
        {
            0 => Localization.Loc.T("Lyrics.OffsetInSync"),
            < 0 => Localization.Loc.T("Lyrics.OffsetEarlier", -offset / 1000.0),
            _ => Localization.Loc.T("Lyrics.OffsetLater", offset / 1000.0),
        });
        return true;
    }

    private void QueueLyricsOffsetSave()
    {
        _lyricsOffsetSaveCts?.Cancel();
        _lyricsOffsetSaveCts?.Dispose();
        var cts = _lyricsOffsetSaveCts = new CancellationTokenSource();
        _ = SaveLyricsOffsetDebouncedAsync(cts.Token);
    }

    private async Task SaveLyricsOffsetDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LyricsOffsetSaveDebounceMs, token);
            await _library.SaveAsync();
        }
        catch (OperationCanceledException) { /* superseded by a newer notch */ }
    }

    // Word-level lookahead: small lead so the sweep matches the vocal instead of trailing
    // UI dispatch latency. AMLL feels in-sync around 80ms — bigger leads start to read
    // as the colour racing ahead of the voice.
    private const int WordLookaheadMs = 80;
    private static readonly TimeSpan WordLookahead = TimeSpan.FromMilliseconds(WordLookaheadMs);

    /// <summary>
    /// Lead for lines WITHOUT word timings. A plain line has no sweep, so its only cue
    /// is the glide bringing it to the anchor — activate it early enough that the glide
    /// has covered half its travel (the moment the eye reads it as "arrived") when the
    /// vocal starts. Derived from the shared line-motion curve, plus the same small
    /// dispatch lead the word clock uses; it was a fixed 350ms that pre-dated the
    /// rate-locked clock and switched plain lines a beat early.
    ///
    /// LyricsTimeline (Core) walks a monotonic cursor that advances forward per
    /// tick — O(1) amortized instead of scanning every line. Resets on seek-backwards.
    /// </summary>
    private static readonly TimeSpan LyricsLookahead =
        TimeSpan.FromMilliseconds(WordLookaheadMs + Helpers.LineMotion.HalfTravelMs);

    private void UpdateActiveLine(TimeSpan position)
    {
        // Every caller passes the audio position; the track's lyrics offset (GitHub #102)
        // moves the lyrics, not the audio, so read the timeline that much behind it.
        position -= LyricsOffset;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        var step = _timeline.Update(position);
        // The set also changes without a line change: an overlapping (duet) line that
        // stays lit under the active one finishes, so the ramp re-centres on what's left.
        if (!step.ActiveSetChanged)
        {
            ScrollLineIndex = _timeline.Lead(position, ScrollLead);
            return;
        }
        if (DebugLog.VlcBridgeEnabled)
        {
            var lit = _timeline.ActiveIndices.Count > 1 ? $" lit [{string.Join(",", _timeline.ActiveIndices)}]" : "";
            Views.LyricsView.Trace(step.LineChanged
                ? $"active {ActiveLineIndex}→{step.ActiveIndex} at {position.TotalMilliseconds:0}ms" +
                  (step.ActiveLine?.Timestamp is { } ts ? $" (line starts {ts.TotalMilliseconds:0}ms)" : "") + lit
                : $"lit set [{string.Join(",", _timeline.ActiveIndices)}] at {position.TotalMilliseconds:0}ms (active {step.ActiveIndex})");
        }
        ActiveLineIndex = step.ActiveIndex;
        ScrollLineIndex = _timeline.Lead(position, ScrollLead);
        UpdateLineOpacities(step.ActiveIndex);
        UpdateWordClockSubscription();
    }

    private static readonly TimeSpan ScrollLead = TimeSpan.FromMilliseconds(Helpers.LineMotion.ScrollLeadMs);

    /// <summary>
    /// Continuous playback clock for the sweep and line engine. The raw position
    /// arrives in coarse steps; <see cref="LyricsPlaybackClock"/> turns them into a
    /// smooth, rate-locked estimate (small disagreements bend the rate, seeks snap).
    /// The engine's reported position is where the output buffer has been FILLED to,
    /// which leads the speaker by the buffer depth — subtracted here so the sweep
    /// lines up with what is audible, not with what has been queued.
    /// </summary>
    private TimeSpan GetPlaybackPosition()
    {
        var raw = _player.Position;
        if (_player.State != Models.PlaybackState.Playing)
        {
            // Not advancing — forget the clock so resume re-anchors fresh (state held
            // across a pause would otherwise add the pause length on resume).
            _clock.Reset();
            return raw;
        }

        // While the timeline is being dragged, Position is a target the user is steering,
        // not a clock that is running — VLC has not been told to seek yet, that happens on
        // release. Advancing it forward actively fought a backward drag: hold the slider
        // still and the estimate crept ahead, pulling the active line back down the list,
        // then snapped up again when the drag resumed. Follow the raw value verbatim and
        // re-anchor the moment the drag ends.
        if (_player.IsSeeking)
        {
            _clock.Reset();
            return raw;
        }

        var nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var estimate = _clock.Sample(raw.TotalMilliseconds, nowMs) - _player.OutputLatency.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Max(0, estimate));
    }

    /// <summary>
    /// Starts a RequestAnimationFrame loop on the main window while a word-synced
    /// line is actively playing (one callback per rendered frame → frame-smooth
    /// sweep). The loop stops itself the moment word-level rendering goes idle, so
    /// there is no per-frame cost for line-only lyrics or paused playback; the 100ms
    /// sync timer restarts it when a word-synced line becomes active again.
    /// </summary>
    // The word clock rides the main window's render loop. While the mini player's
    // lyrics form is the visible surface, the main window is hidden and pumps no
    // frames — the mini player registers itself as the frame source instead.
    private Avalonia.Controls.TopLevel? _wordClockHostOverride;

    /// <summary>Alternative TopLevel to drive the word clock (the mini player's lyrics
    /// form); null falls back to the main window.</summary>
    public void SetWordClockHost(Avalonia.Controls.TopLevel? host)
    {
        _wordClockHostOverride = host;
        if (host != null)
            UpdateWordClockSubscription();
    }

    private Avalonia.Controls.TopLevel? GetWordClockHost()
    {
        if (_wordClockHostOverride != null) return _wordClockHostOverride;
        return Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    private void UpdateWordClockSubscription()
    {
        if (!WantsWordClock || _wordClockRunning) return;
        if (GetWordClockHost() is not { } topLevel) return;

        _wordClockRunning = true;
        topLevel.RequestAnimationFrame(OnWordClockFrame);
    }

    // Number of lyrics surfaces currently attached (the full page and/or the side panel).
    // The word clock re-registers RequestAnimationFrame every frame, which keeps the
    // compositor's frame loop hot — doing that for output nobody can see (playing a
    // word-timed track while sitting on Home with the panel closed) burned CPU and
    // battery indefinitely, because nothing stopped the timer on navigation away.
    private int _visibleLyricsSurfaces;

    /// <summary>Called by LyricsView / LyricsPanelView on attach and detach.</summary>
    public void SetLyricsSurfaceVisible(bool visible)
    {
        _visibleLyricsSurfaces = Math.Max(0, _visibleLyricsSurfaces + (visible ? 1 : -1));
        if (_visibleLyricsSurfaces == 0)
            _lyricsSyncTimer.Stop();
        else if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
            _lyricsSyncTimer.Start();
        // A lyrics view opening on a song already found to have none.
        if (visible) MaybeAutoDownloadLyrics();
    }

    private bool IsAnyLyricsSurfaceVisible => _visibleLyricsSurfaces > 0;

    private bool WantsWordClock =>
        _hasSyncedLyrics
        && IsAnyLyricsSurfaceVisible
        && _player.State == Models.PlaybackState.Playing
        && _lyricsSyncTimer.IsEnabled
        && AnyActiveLineHasWordLayer();

    // Any lit line, not just the active one: an overlapping duet line keeps sweeping
    // under a plain active line and would freeze if only the active line counted.
    private bool AnyActiveLineHasWordLayer()
    {
        // A held note finishing its letter animation on a line that just went inactive
        // still needs frames, even when the new line is plain text.
        if (_timeline.HasEmphasisInFlight) return true;
        foreach (var line in _timeline.ActiveLines)
            if (line.HasWords || line.HasBackgroundWords || line.HasTransliterationWords
                || line.IsIntroPlaceholder)   // the dots fill with the gap's progress
                return true;
        return false;
    }

    private void OnWordClockFrame(TimeSpan _)
    {
        if (!WantsWordClock)
        {
            _wordClockRunning = false;
            return;
        }

        // Note: UpdateActiveLine re-enters UpdateWordClockSubscription on line change;
        // the _wordClockRunning flag prevents a second concurrent loop.
        UpdateActiveLine(GetPlaybackPosition());

        if (GetWordClockHost() is { } topLevel)
        {
            topLevel.RequestAnimationFrame(OnWordClockFrame);
        }
        else
        {
            _wordClockRunning = false;
        }
    }

    /// <summary>
    /// Sets LineOpacity on each lyric line based on distance from the nearest lit line
    /// (the active line or an overlapping duet line still being sung, see
    /// <see cref="LyricsTimeline.ActiveIndices"/>).
    /// Active=1.0, adjacent lines fade gradually over ±9 lines, rest=0.0 (hidden);
    /// fullscreen focus (opt-in) tightens the ramp to the active line ±2.
    /// Pass activeIndex=-1 to restore all lines to full opacity (e.g. unsynced or reset).
    /// </summary>
    private void UpdateLineOpacities(int activeIndex)
    {
        if (activeIndex < 0)
        {
            for (int i = 0; i < LyricLines.Count; i++)
            {
                var line = LyricLines[i];
                line.LineOpacity = 1.0;
                line.IsClickable = true;
                if (line.BlurRadius != 0.0) line.BlurRadius = 0.0;
                if (_windowWordLayers) SetWordLayerLive(line, i <= WordLayerWindowLines);
            }
            return;
        }

        // Fullscreen focus (opt-in): only the active line and ±2 neighbours stay visible
        // while the lyrics page fills a fullscreen window. The side panel shares these
        // lines but can never be open with the page up, so the tight ramp never leaks
        // into it.
        var focus = IsLyricsFocusActive;
        // Settings floor (percent): lines the ramp would hide stay at least this
        // visible, and therefore clickable, so any part of the song can be reached
        // from its lyrics. 0 keeps the ramp as designed.
        var floor = Math.Clamp(_player.LyricsMinLineOpacity, 0, 100) / 100.0;

        // Distance to the NEAREST lit line: overlapping duet lines all sit at full focus.
        // While the list leads into the next line (ScrollLineIndex), focus is on that line
        // alone — the lead only starts once every lit line has finished being sung.
        IReadOnlyList<int> lit = _timeline.ActiveIndices;
        if (ScrollLineIndex == activeIndex + 1)
        {
            activeIndex = ScrollLineIndex;
            lit = Array.Empty<int>();
        }
        for (int i = 0; i < LyricLines.Count; i++)
        {
            var absDist = LyricsTimeline.DistanceToNearestActive(i, activeIndex, lit);
            var opacity = focus
                ? absDist switch
                {
                     0 => 1.0,
                     1 => 0.5,
                     2 => 0.22,
                     _ => 0.0
                }
                : absDist switch
                {
                     0 => 1.0,
                     1 => 0.55,
                     2 => 0.32,
                     3 => 0.18,
                     4 => 0.12,
                     5 => 0.08,
                     6 => 0.06,
                     7 => 0.04,
                     8 => 0.03,
                     9 => 0.02,
                     _ => 0.0
                };
            if (absDist != 0 && opacity < floor)
                opacity = floor;
            // Apple Music–style depth: active crisp, neighbours softly blurred.
            var blur = absDist switch
            {
                0 => 0.0,
                1 => 4.0,
                2 => 6.0,
                3 => 8.0,
                _ => 10.0,
            };
            var line = LyricLines[i];
            // Only set if changed — avoids unnecessary PropertyChanged notifications and re-renders
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (line.LineOpacity != opacity)
                line.LineOpacity = opacity;
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (line.BlurRadius != blur)
                line.BlurRadius = blur;
            var clickable = opacity > 0.0;
            if (line.IsClickable != clickable)
                line.IsClickable = clickable;
            if (_windowWordLayers)
                SetWordLayerLive(line, absDist <= WordLayerWindowLines);
        }
    }

    // ── Word layers of very long lyrics (word-synced audiobooks, 10-28) ──
    // A word cell is ~8 visuals and ~20 bindings. A song's few hundred words never notice;
    // a 1.5 h audiobook chapter's 14,238 were 118,559 visuals and a 1.4 GB heap, built in a
    // 14-17 s UI freeze (3.3 GB allocated, 195 gen-1 collections) whose GC pauses stopped the
    // managed audio render thread: the sound came in 20-50 ms bursts every 250 ms until the
    // page was built (Discord, nutf!xx). Past this many words only lines near the sung one
    // keep the word layer; the rest are drawn at rest by KaraokeRestingWords, which lays them
    // out identically, so moving the window moves nothing on screen.
    internal const int WordLayerWindowMinWords = 2000;
    // Lines either side of the lit ones that keep it: the line glided into ahead of time
    // (IsLeading), and the one just sung while its float and held note settle.
    internal const int WordLayerWindowLines = 2;
    private bool _windowWordLayers;

    private void OnLyricLinesChangedForWordLayers()
    {
        var words = 0;
        foreach (var line in LyricLines)
            words += line.Words?.Count ?? 0;
        _windowWordLayers = words > WordLayerWindowMinWords;
        var center = Math.Max(0, ActiveLineIndex);
        for (int i = 0; i < LyricLines.Count; i++)
            SetWordLayerLive(LyricLines[i], !_windowWordLayers || Math.Abs(i - center) <= WordLayerWindowLines);
    }

    private static void SetWordLayerLive(LyricLine line, bool live)
    {
        if (line.IsWordLayerLive != live)
            line.IsWordLayerLive = live;
    }

    // The focus gate flips mid-line (fullscreen entered/left, or the Settings toggle),
    // so the ramp re-runs in place rather than waiting for the next line change. Mirrors
    // RefreshActiveLyricPosition's guards: the synced tab re-dims around the active line,
    // everything else restores full opacity.
    private void RefreshFocusDimming()
    {
        if (_hasSyncedLyrics && IsSyncTabSelected && LyricLines.Count > 0)
            UpdateLineOpacities(ActiveLineIndex);
        else
            UpdateLineOpacities(-1);
    }

    private void OnLyricsWordEffectChanged(object? sender, EventArgs e) => ApplyLineDirections();

    /// <summary>
    /// Plugin API 1.5: asks the lyrics effect in use (if any) which of the song's lines read
    /// right to left, and marks them (LyricLine.SetRightToLeft); without an effect every line is
    /// laid out from the left, as always. Once per lyrics load, never per frame. An effect that
    /// throws here is reported like one that throws while drawing.
    /// </summary>
    internal void ApplyLineDirections()
    {
        var count = LyricLines.Count;
        var effect = Controls.LyricsWordEffect.Current;
        if (count == 0) return;
        var rtl = new bool[count];
        if (effect is not null)
        {
            var texts = new string[count];
            for (var i = 0; i < count; i++) texts[i] = LyricLines[i].Text ?? "";
            try { effect.LineDirections(texts, rtl); }
            catch (Exception ex)
            {
                Array.Clear(rtl);
                Controls.KaraokeWordText.ReportEffectFailure(effect, ex);
            }
        }
        for (var i = 0; i < count; i++)
            if (LyricLines[i].IsRightToLeft != rtl[i]) LyricLines[i].SetRightToLeft(rtl[i]);
    }

    public void Dispose()
    {
        Controls.LyricsWordEffect.Changed -= OnLyricsWordEffectChanged;
        // Stop and dispose timer to prevent memory leak. This also ends the word-sweep
        // RequestAnimationFrame loop: WantsWordClock goes false, so the next frame
        // callback exits without re-registering.
        _lyricsSyncTimer.Stop();

        // Unsubscribe from current track's property changes
        if (_currentTrack != null)
            _currentTrack.PropertyChanged -= OnCurrentTrackPropertyChanged;

        // Dispose status clear timer
        _statusClearCts?.Cancel();
        _statusClearCts?.Dispose();

        // Unsubscribe from player events to prevent memory leak
        _player.TrackStarted -= OnTrackStarted;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _library.LibraryUpdated -= OnLibraryUpdated;
        if (_accentHandler != null) App.AccentApplied -= _accentHandler;
    }
}
