using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    private static readonly List<ColorSwatch> _solidColorSwatches = BuildSolidSwatches();
    private static readonly List<ColorSwatch> _gradientSwatches = BuildGradientSwatches();

    /// <summary>"Auto" swatch — its preview tracks the current track's artwork-derived color.</summary>
    private readonly ColorSwatch _autoSwatch =
        new("", "Auto", new SolidColorBrush(DefaultAdaptiveColor), isAuto: true);

    public IReadOnlyList<ColorSwatch> SolidSwatches { get; }
    public List<ColorSwatch> GradientSwatches => _gradientSwatches;

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
    private LyricLine? _currentActiveLine;
    private bool _hasSyncedLyrics;
    private Track? _currentTrack;
    private string _loadedLyrics = string.Empty;
    private string _loadedSyncedLyrics = string.Empty;
    private LrcLibResult? _currentOnlineResult;
    private LrcLibResult? _alternateOnlineResult;
    private string? _alternateSource;
    private int _searchGeneration;

    private static readonly string LyricsCacheDir = Path.Combine(
        Helpers.AppPaths.DataRoot, "lyrics_cache");

    private static readonly Color DefaultAdaptiveColor = Color.FromRgb(0x0D, 0x1B, 0x2A);

    // Dedicated lyrics sync timer — bypasses the fragile PropertyChanged chain.
    // Runs at a fixed 100ms cadence for line-level sync. Word-level sweep smoothness
    // does NOT come from this timer: while the active line has word timings we
    // subscribe to Avalonia's global animation clock (one tick per rendered frame)
    // so the karaoke sweep is frame-synced instead of stepping every 33ms.
    private readonly DispatcherTimer _lyricsSyncTimer;

    private const int LineSyncIntervalMs = 100;

    // ── Extrapolated playback clock ──
    // LibVLC only refreshes MediaPlayer.Time every ~150-300ms, so raw Position reads
    // move in coarse steps — far too chunky for the word-level colour sweep. Anchor
    // each fresh raw value against Stopwatch time and extrapolate between updates,
    // with a monotonic guard so re-anchor jitter never drags the sweep backwards.
    private long _clockRawMs = -1;
    private long _clockAnchorMs;
    private long _clockAnchorTimestamp;
    private double _clockLastMs;

    // True while the RequestAnimationFrame loop driving the word sweep is running.
    // Managed by UpdateWordClockSubscription() / OnWordClockFrame().
    private bool _wordClockRunning;

    // Monotonic line cursor — avoids re-scanning every tick.
    private int _lineCursor;
    private TimeSpan _lastSyncPosition = TimeSpan.MinValue;

    public PlayerViewModel Player => _player;

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
    /// deeper (45%) while this is on, since the dimmed-away lines leave the lower
    /// viewport empty at the default 22% anchor.</summary>
    public bool IsLyricsFocusActive => IsFullScreenPageActive && Player.LyricsFullScreenFocusEnabled;

    /// <summary>Plain text lyrics without timestamps for the Unsync tab.</summary>
    public BulkObservableCollection<LyricLine> UnsyncedLines { get; } = new();

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
            LyricsPrimaryFg = new SolidColorBrush(Color.Parse("#111111"));
            LyricsSecondaryFg = new SolidColorBrush(Color.Parse("#55111111"));
            LyricsAccentFg = ResolveAccentBrush("AccentColorBrushDark1");
            LyricsSubtleFg = new SolidColorBrush(Color.Parse("#555555"));
            LyricsSliderFilled = new SolidColorBrush(Color.Parse("#CC111111"));
            LyricsSliderUnfilled = new SolidColorBrush(Color.Parse("#33111111"));
            LyricsControlFill = new SolidColorBrush(Color.Parse("#222222"));
            LyricsBtnBg = new SolidColorBrush(Color.Parse("#22000000"));
            LyricsBtnBgHover = new SolidColorBrush(Color.Parse("#33000000"));
            LyricsSliderThumb = new SolidColorBrush(Color.Parse("#DD111111"));
        }
        else if (lum > 0.35) // Medium background — boost contrast
        {
            LyricsPrimaryFg = Brushes.White;
            LyricsSecondaryFg = new SolidColorBrush(Color.Parse("#DDFFFFFF"));
            LyricsAccentFg = ResolveAccentBrush("AccentColorBrushLight1");
            LyricsSubtleFg = new SolidColorBrush(Color.Parse("#CCCCCC"));
            LyricsSliderFilled = new SolidColorBrush(Color.Parse("#EEFFFFFF"));
            LyricsSliderUnfilled = new SolidColorBrush(Color.Parse("#44FFFFFF"));
            LyricsControlFill = Brushes.White;
            LyricsBtnBg = new SolidColorBrush(Color.Parse("#44000000"));
            LyricsBtnBgHover = new SolidColorBrush(Color.Parse("#55000000"));
            LyricsSliderThumb = new SolidColorBrush(Color.Parse("#FFFFFFFF"));
        }
        else // Dark background
        {
            LyricsPrimaryFg = Brushes.White;
            LyricsSecondaryFg = new SolidColorBrush(Color.Parse("#B0FFFFFF"));
            LyricsAccentFg = ResolveAccentBrush();
            LyricsSubtleFg = new SolidColorBrush(Color.Parse("#999999"));
            LyricsSliderFilled = new SolidColorBrush(Color.Parse("#CCFFFFFF"));
            LyricsSliderUnfilled = new SolidColorBrush(Color.Parse("#33FFFFFF"));
            LyricsControlFill = Brushes.White;
            LyricsBtnBg = new SolidColorBrush(Color.Parse("#33FFFFFF"));
            LyricsBtnBgHover = new SolidColorBrush(Color.Parse("#55FFFFFF"));
            LyricsSliderThumb = new SolidColorBrush(Color.Parse("#EEFFFFFF"));
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
        LyricsSecondaryFg = new SolidColorBrush(Color.Parse("#B0FFFFFF"));
        LyricsAccentFg = ResolveAccentBrush();
        LyricsSubtleFg = new SolidColorBrush(Color.Parse("#999999"));
        LyricsSliderFilled = new SolidColorBrush(Color.Parse("#CCFFFFFF"));
        LyricsSliderUnfilled = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        LyricsControlFill = Brushes.White;
        LyricsBtnBg = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        LyricsBtnBgHover = new SolidColorBrush(Color.Parse("#55FFFFFF"));
        LyricsSliderThumb = new SolidColorBrush(Color.Parse("#EEFFFFFF"));
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

    /// <summary>Whether lyrics can be removed (true for online-fetched or cached service lyrics).</summary>
    [ObservableProperty]
    private bool _canRemoveLyrics;

    /// <summary>Status text for save operation feedback.</summary>
    [ObservableProperty]
    private string _saveStatusText = string.Empty;

    /// <summary>Whether auto-follow has been paused by user manual scroll.</summary>
    [ObservableProperty]
    private bool _isAutoFollowPaused;

    /// <summary>Name of the lyrics source currently displayed (e.g. "LRCLIB", "NetEase", "Local").</summary>
    [ObservableProperty]
    private string _lyricsSourceName = string.Empty;

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
    /// The lyrics view's code-behind subscribes and calls Flyout.ShowAt on the hidden host button.
    /// </summary>
    public event Action? OpenBackgroundColorRequested;

    public LyricsViewModel(PlayerViewModel player, ILrcLibService lrcLib, INetEaseService netEase, IMetadataService metadata, IPersistenceService persistence, ILibraryService library)
    {
        _player = player;
        _lrcLib = lrcLib;
        _netEase = netEase;
        _metadata = metadata;
        _persistence = persistence;
        _library = library;

        // "Auto" first, then the fixed color swatches.
        var solid = new List<ColorSwatch>(_solidColorSwatches.Count + 1) { _autoSwatch };
        solid.AddRange(_solidColorSwatches);
        SolidSwatches = solid;

        // Dedicated sync timer — polls player position and drives line highlighting.
        // Fixed 100ms cadence; word-level sweep is frame-driven via the render-clock
        // subscription (see UpdateWordClockSubscription), for which this tick doubles
        // as the self-healing re-subscribe check after pause/resume or timer restarts.
        _lyricsSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs) };
        _lyricsSyncTimer.Tick += (_, _) =>
        {
            if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
                UpdateActiveLine(GetPlaybackPosition());
            UpdateWordClockSubscription();
        };

        // Subscribe to track changes to update lyrics
        _player.TrackStarted += OnTrackStarted;

        // Reload lyrics when metadata is edited (e.g. synced lyrics toggled off)
        _library.LibraryUpdated += OnLibraryUpdated;

        // Subscribe to state changes to start/stop the sync timer
        _player.PropertyChanged += OnPlayerPropertyChanged;

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
    /// (Rectangle Fill="#66000000" in LyricsView.axaml). Keep in sync if that changes —
    /// it shifts the visible luminance enough to matter for the readability threshold.</summary>
    private const double ArtworkScrimAlpha = 0x66 / 255.0;

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
            FullBackgroundBrush = IsColorModeSolid && vibrant is { } v
                ? new SolidColorBrush(v)
                : DominantColorExtractor.GenerateUnifiedBrush(dominant, secondary);
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
        if (IsColorModeArtwork && _averageArtworkColor is Color avg)
        {
            // Scrim is pure black at ArtworkScrimAlpha opacity, so the visible color is
            // a straight linear interpolation of avg toward black.
            double k = 1.0 - ArtworkScrimAlpha;
            var visible = Color.FromRgb(
                (byte)Math.Clamp(avg.R * k, 0, 255),
                (byte)Math.Clamp(avg.G * k, 0, 255),
                (byte)Math.Clamp(avg.B * k, 0, 255));
            UpdateForegroundsForBackground(new SolidColorBrush(visible));
        }
        else
        {
            UpdateForegroundsForBackground(FullBackgroundBrush);
        }
    }

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
        await PersistArtworkBackgroundPreferenceAsync(false);
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
        await PersistArtworkBackgroundPreferenceAsync(false);
    }

    private async Task PersistArtworkBackgroundPreferenceAsync(bool showArtwork)
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            if (settings.LyricsShowArtworkBackground == showArtwork) return;
            settings.LyricsShowArtworkBackground = showArtwork;
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
                // Color mode but no sub-mode persisted: default to Solid. With no saved
                // swatch the background stays on Auto — recompute it for Solid mode
                // (vibrant solid) since the constructor derived it in artwork mode.
                IsColorModeSolid = true;
                if (string.IsNullOrEmpty(settings.LyricsBackgroundColorHex))
                    UpdateAdaptiveBackground(_player.AlbumArt);
            }

            if (!string.IsNullOrEmpty(settings.LyricsBackgroundColorHex))
            {
                _selectedColorHex = settings.LyricsBackgroundColorHex;
                ActiveSwatchKey = _selectedColorHex;

                if (_selectedColorHex.StartsWith("grad:"))
                {
                    var parts = _selectedColorHex[5..].Split(',');
                    if (parts.Length >= 2)
                    {
                        var c1 = Color.Parse(parts[0]);
                        var c2 = Color.Parse(parts[1]);
                        FullBackgroundBrush = DominantColorExtractor.GenerateGradientBrush(c1, c2);
                        LyricsBackgroundBrush = FullBackgroundBrush;
                        IsColorModeSolid = false;
                        IsColorModeGradient = true;
                    }
                    else
                    {
                        _selectedColorHex = null;
                        ActiveSwatchKey = "";
                    }
                }
                else
                {
                    var color = Color.Parse(_selectedColorHex);
                    var brush = new SolidColorBrush(color);
                    FullBackgroundBrush = brush;
                    LyricsBackgroundBrush = brush;
                    UpdateForegroundsForBackground(brush);
                }
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

        try
        {
            // Load settings to check which providers are enabled
            var settings = await _persistence.LoadSettingsAsync();
            var lrcLibEnabled = settings.LrcLibEnabled;
            var netEaseEnabled = settings.NetEaseEnabled;

            var artist = track.Artist ?? "";
            var title = track.Title ?? "";
            var duration = track.Duration.TotalSeconds;

            // "Unknown Artist" is the library's placeholder default, not a name —
            // sending it verbatim guarantees a /get miss and poisons /search relevance.
            var hasKnownArtist = !LyricsSearchSelector.IsUnknownArtist(artist);

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

            // With both providers switched off, Task.WhenAll on an empty list completed
            // instantly and the user got "No Lyrics found." — indistinguishable from a
            // genuine miss, with no hint that the providers are disabled in Settings.
            if (tasks.Count == 0)
            {
                LyricLines.Clear();
                UnsyncedLines.Clear();
                SearchFailedMessage = "Lyrics providers are turned off in Settings.";
                ShowSearchButton = false;
                return;
            }

            await Task.WhenAll(tasks);

            async Task FetchLrcLibAsync()
            {
                try
                {
                    // /get needs an exact artist match — pointless with the placeholder.
                    var result = hasKnownArtist
                        ? await _lrcLib.GetLyricsAsync(artist, title, duration)
                        : null;

                    if (result != null && result.Instrumental)
                    {
                        // The exact match says this track is instrumental — that is an
                        // answer, not a miss. Falling through to fuzzy /search would
                        // surface a different song's lyrics.
                        lrcLibInstrumental = true;
                        return;
                    }

                    if (result == null || !result.HasLyrics)
                    {
                        // /search is fuzzy and relevance-ordered; validate candidates
                        // against the local track before preferring richer formats.
                        var results = await _lrcLib.SearchLyricsAsync(hasKnownArtist ? artist : "", title);
                        result = LyricsSearchSelector.PickFromSearchResults(results, artist, title, duration);
                    }
                    lrcLibResult = result;
                }
                catch (Exception ex)
                {
                    lrcLibErrored = true;
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "LRCLIB:Error", ex.Message);
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
                }
            }

            // Race condition guard
            if (generation != _searchGeneration) return;

            // Pick best result: prefer synced over unsynced, LRCLIB over NetEase when equal
            var (primary, primarySource, alternate, altSource) = PickBestResult(lrcLibResult, netEaseResult);

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
                    AlternateLyricsLabel = $"Try {altSource}";
                }
            }
            else
            {
                LyricLines.Clear();
                UnsyncedLines.Clear();
                if (lrcLibInstrumental)
                {
                    // A definitive "instrumental" answer outranks any provider error.
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:Instrumental");
                    SearchFailedMessage = "This track is instrumental.";
                }
                else if (lrcLibErrored || netEaseErrored)
                {
                    // No provider produced results and at least one errored — an
                    // offline user (or a provider outage serving 5xx) must not read
                    // this as "this track has no lyrics".
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:ProviderError");
                    SearchFailedMessage = "Search failed — check your internet connection.";
                }
                else
                {
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:NotFound");
                    SearchFailedMessage = "No Lyrics found.";
                }
                ShowSearchButton = true;
            }
        }
        catch
        {
            if (generation == _searchGeneration)
            {
                LyricLines.Clear();
                UnsyncedLines.Clear();
                SearchFailedMessage = "Search failed — check your internet connection.";
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
    /// Picks the best lyrics result from the two providers.
    /// Prefers synced over unsynced. When both have equal quality, prefers LRCLIB (curated).
    /// Returns (primary, primarySource, alternate, alternateSource).
    /// </summary>
    private static (LrcLibResult? Primary, string PrimarySource, LrcLibResult? Alternate, string? AlternateSource)
        PickBestResult(LrcLibResult? lrcLib, LrcLibResult? netEase)
    {
        var lrcLibHas = lrcLib != null && lrcLib.HasLyrics;
        var netEaseHas = netEase != null && netEase.HasLyrics;

        if (lrcLibHas && netEaseHas)
        {
            // Both have results — pick the one with synced lyrics, or LRCLIB if equal
            if (lrcLib!.HasSyncedLyrics && !netEase!.HasSyncedLyrics)
                return (lrcLib, "LRCLIB", netEase, "NetEase");
            if (!lrcLib.HasSyncedLyrics && netEase!.HasSyncedLyrics)
                return (netEase, "NetEase", lrcLib, "LRCLIB");
            // Both synced or both unsynced — prefer LRCLIB (community curated)
            return (lrcLib, "LRCLIB", netEase, "NetEase");
        }

        if (lrcLibHas)
            return (lrcLib, "LRCLIB", null, null);
        if (netEaseHas)
            return (netEase, "NetEase", null, null);

        return (null, "", null, null);
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
        AlternateLyricsLabel = $"Try {prevSource}";
    }

    [RelayCommand]
    private async Task SaveLyricsToFile()
    {
        if (_currentTrack == null || _currentOnlineResult == null) return;

        var track = _currentTrack;
        var syncedToSave = _currentOnlineResult.SyncedLyrics;
        var plainToSave = !string.IsNullOrWhiteSpace(_currentOnlineResult.PlainLyrics)
            ? _currentOnlineResult.PlainLyrics
            : LyricsTextHelper.StripTimestamps(syncedToSave);

        if (string.IsNullOrWhiteSpace(syncedToSave) && string.IsNullOrWhiteSpace(plainToSave)) return;

        // Route plain text into Lyrics, synced text into SyncedLyrics — never mix them.
        track.Lyrics = plainToSave ?? string.Empty;
        track.SyncedLyrics = syncedToSave ?? string.Empty;

        // Root cause fix: writing embedded tags can fail while the media file is in use.
        // Save an LRC sidecar (synced) and a TXT sidecar (plain) next to the track.
        var trackPath = track.FilePath;
        if (string.IsNullOrWhiteSpace(trackPath))
        {
            ShowStatusText("Save failed", 5000);
            return;
        }

        try
        {
            // File I/O runs off the UI thread, on the writer lane so it never races
            // the auto-persist writer on the same .lrc path.
            await EnqueueLyricsFileWork(() =>
            {
                if (!string.IsNullOrWhiteSpace(syncedToSave))
                {
                    var lrcPath = Path.ChangeExtension(trackPath, ".lrc");
                    File.WriteAllText(lrcPath, NormalizeLyricsForLrc(syncedToSave), new UTF8Encoding(false));
                    // Register so RemoveLyrics can delete what this save created.
                    SidecarRegistry.Add(lrcPath);
                }

                if (!string.IsNullOrWhiteSpace(plainToSave))
                {
                    // Never overwrite an existing .txt — same rule as the auto-persist
                    // path: Song.txt may be the user's own liner notes, a file this
                    // view never reads as a lyrics source.
                    var txtPath = Path.ChangeExtension(trackPath, ".txt");
                    if (!File.Exists(txtPath))
                        File.WriteAllText(txtPath, NormalizeLyricsForLrc(plainToSave), new UTF8Encoding(false));
                }
            });

            // Best-effort metadata write (non-blocking for save success, and the
            // TagLib rewrite stays off the shared writer lane).
            await Task.Run(() => { try { _metadata.WriteTrackMetadata(track); } catch { } });

            CanSaveToFile = false;
            ShowStatusText("Saved Lyrics");
        }
        catch
        {
            ShowStatusText("Save failed — check file permissions", 5000);
        }
    }

    /// <summary>
    /// Writes freshly fetched online lyrics to sidecar files next to the track and
    /// updates the in-memory track fields, so the Metadata editor's Plain/Synced
    /// tabs reflect them immediately and persistently. Best-effort; never throws.
    /// </summary>
    private void PersistOnlineLyricsToSidecar(LrcLibResult result, bool allowReplaceAppSidecar = false)
    {
        var track = _currentTrack;
        if (track == null) return;

        var synced = result.SyncedLyrics;
        var plain = !string.IsNullOrWhiteSpace(result.PlainLyrics)
            ? result.PlainLyrics
            : LyricsTextHelper.StripTimestamps(synced);

        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
            return;

        var trackPath = track.FilePath;
        if (string.IsNullOrWhiteSpace(trackPath))
        {
            // No sidecar can exist without a track path — just reflect the lyrics
            // into the in-memory track fields for the Metadata editor.
            track.Lyrics = plain ?? string.Empty;
            track.SyncedLyrics = synced ?? string.Empty;
            return;
        }

        var lrcPath = Path.ChangeExtension(trackPath, ".lrc");
        var canWriteSidecar = !string.IsNullOrWhiteSpace(synced);
        var stamp = Volatile.Read(ref _lyricsRemovalStamp);

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

                var sidecarExists = File.Exists(lrcPath);
                var replaceAppSidecar = canWriteSidecar && sidecarExists && allowReplaceAppSidecar
                    && SidecarRegistry.Contains(lrcPath);
                // On an explicit switch that leaves an existing sidecar standing
                // (user-owned, or nothing synced to replace an app-written one with)
                // the track fields stay untouched: they must not advertise lyrics
                // that disk will override on the next play. The switched-to lyrics
                // still display (and cache) for this session.
                var blockedBySidecar = sidecarExists && allowReplaceAppSidecar && !replaceAppSidecar;

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
                }

                if (!canWriteSidecar) return;
                if (sidecarExists && !replaceAppSidecar)
                {
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "Sidecar.SkipExisting", lrcPath);
                    return;
                }

                File.WriteAllText(lrcPath, NormalizeLyricsForLrc(synced!), new UTF8Encoding(false));
                SidecarRegistry.Add(lrcPath);
            }
            catch { /* best effort — sidecar write is non-fatal */ }
        });
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

    /// <summary>
    /// Removes the currently displayed online lyrics: clears the cached file,
    /// resets lyrics state, and shows the search button so the user can retry.
    /// </summary>
    [RelayCommand]
    private void RemoveLyrics()
    {
        if (_currentTrack == null) return;

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
        var trackId = _currentTrack.Id;
        var trackPath = _currentTrack.FilePath;
        EnqueueLyricsFileWork(() =>
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
                    var lrcPath = Path.ChangeExtension(trackPath, ".lrc");
                    // Unregister only once the file is actually gone: the trash move
                    // can fail (file locked/in use), and dropping the registry entry
                    // first left the app's own file on disk permanently looking
                    // user-owned — Remove would then never touch it again.
                    if (SidecarRegistry.Contains(lrcPath))
                    {
                        if (!File.Exists(lrcPath) || TrashSidecarFile(lrcPath))
                            SidecarRegistry.Remove(lrcPath);
                        else
                            DebugLogger.Error(DebugLogger.Category.Error, "Lyrics.SidecarTrashFailed", lrcPath);
                    }
                }
            }
            catch { }
        }).Wait();

        // Clear the in-memory copies too, otherwise the next probe finds them on the
        // Track and re-displays what was just removed.
        _currentTrack.Lyrics = string.Empty;
        _currentTrack.SyncedLyrics = string.Empty;

        // Reset state
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        _lineCursor = 0;
        _lastSyncPosition = TimeSpan.MinValue;
        CanSaveToFile = false;
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

    /// <summary>
    /// Called from context menus to search lyrics for a specific track.
    /// Loads the track first, and if no local lyrics found, triggers online search.
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
        });
    }

    private void DisplayOnlineLyrics(LrcLibResult result, bool userSwitchedSource = false)
    {
        _currentOnlineResult = result;
        LyricLines.Clear();
        UnsyncedLines.Clear();
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        _lineCursor = 0;
        _lastSyncPosition = TimeSpan.MinValue;
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
                InsertIntroPlaceholderIfNeeded(parsedLines);

            LyricLines.ReplaceAll(parsedLines);

            if (!string.IsNullOrWhiteSpace(plainForUnsync))
                PopulateUnsyncedFromPlainText(plainForUnsync);
            else
                PopulateUnsyncedLines(parsedLines);
        }
        else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
        {
            var rendered = new List<LyricLine>();
            var lines = SplitPlainLyrics(result.PlainLyrics);
            foreach (var line in lines)
            {
                var wrapped = SoftWrapText(line);
                rendered.Add(new LyricLine { Text = wrapped, IsActive = true });
            }
            LyricLines.ReplaceAll(rendered);
            UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true }));
        }

        AutoSelectTab();
        RefreshActiveLyricPosition();
        CanSaveToFile = true;
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

        // Start sync timer if synced lyrics and playing
        if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
            _lyricsSyncTimer.Start();
    }

    /// <summary>Clears all lyrics state when no track is playing.</summary>
    private void ClearLyricsState()
    {
        _currentTrack = null;
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        _lineCursor = 0;
        _lastSyncPosition = TimeSpan.MinValue;
        CanSaveToFile = false;
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

    private void OnTrackStarted(object? sender, Track track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Unsubscribe from previous track's IsFavorite changes
            if (_currentTrack != null)
                _currentTrack.PropertyChanged -= OnCurrentTrackPropertyChanged;

            LoadLyricsForTrack(track);
            UpdateAdaptiveBackground(_player.AlbumArt);

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
            _lineCursor = 0;
            _lastSyncPosition = TimeSpan.MinValue;
            UpdateActiveLine(GetPlaybackPosition());
            UpdateLineOpacities(ActiveLineIndex);
            OnPropertyChanged(nameof(ActiveLineIndex));
        }
        else
        {
            UpdateLineOpacities(-1);
        }

        OnPropertyChanged(nameof(ActiveLyricLines));
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
            UpdateAdaptiveBackground(_player.AlbumArt);
        }
        // Fullscreen-focus setting flipped while lyrics are showing — re-dim in place.
        else if (e.PropertyName == nameof(PlayerViewModel.LyricsFullScreenFocusEnabled))
        {
            OnPropertyChanged(nameof(IsLyricsFocusActive));
            RefreshFocusDimming();
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
        _currentTrack = track;
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        ShowSearchButton = false;
        SearchFailedMessage = string.Empty;
        IsSearching = false;
        CanSaveToFile = false;
        CanRemoveLyrics = false;
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
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        _lineCursor = 0;
        _lastSyncPosition = TimeSpan.MinValue;
        _lyricsSyncTimer.Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs);

        // Fire-and-forget: all file I/O runs off the UI thread, result is posted back.
        // The task is kept so SearchLyricsForTrack can await the probe's outcome.
        _localProbeTask = LoadLocalLyricsAsync(track, generation);
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
    private async Task LoadLocalLyricsAsync(Track track, int generation)
    {
        var probe = await Task.Run(() =>
        {
            // Track lyrics are store-backed and lazy: the first touch is a small
            // disk read, so capture the change-detection baselines here, off the
            // UI thread, instead of in LoadLyricsForTrack (this also warms the
            // store's LRU for the UI-thread reads in ApplyLocalLyricsResult).
            _loadedLyrics = track.Lyrics;
            _loadedSyncedLyrics = track.SyncedLyrics;
            return ProbeLocalLyricSources(track);
        });

        if (generation != _searchGeneration) return;

        // Give attached views one beat to fade the (still-visible) old lyrics
        // out, so the swap below — a full ItemsControl rebuild plus a scroll
        // snap — happens off-screen instead of as a visible flash+jump.
        if (LyricsSwapPending != null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => LyricsSwapPending?.Invoke(this, EventArgs.Empty));
            await Task.Delay(LyricsSwapFadeOutMs);
            if (generation != _searchGeneration) return;
        }

        var applied = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (generation == _searchGeneration)
                {
                    ApplyLocalLyricsResult(track, probe);
                    LyricsSwapped?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                applied.SetResult();
            }
        });
        await applied.Task;
    }

    private readonly record struct LocalLyricsProbe(
        List<LyricLine>? Lines,
        string? UnsyncedPlain,
        string Source,
        bool FromCache);

    /// <summary>Synchronous probe helper — must only be called off the UI thread.</summary>
    private static LocalLyricsProbe ProbeLocalLyricSources(Track track)
    {
        // Priority 1: .lyricsfile sidecar (word-level, LRCGET v2.0+).
        try
        {
            var sidecarYaml = TryReadSidecar(track.FilePath, new[] { ".lyricsfile", ".Lyricsfile", ".LYRICSFILE" });
            if (sidecarYaml != null)
            {
                var (lines, plain) = LyricsfileParser.Parse(sidecarYaml);
                if (lines != null && lines.Count > 0)
                    return new LocalLyricsProbe(lines, plain, "Sidecar:Lyricsfile", FromCache: false);
            }
        }
        catch { }

        // Priority 2: .ttml sidecar (word- or line-level, Apple Music style).
        try
        {
            var sidecarTtml = TryReadSidecar(track.FilePath, new[] { ".ttml", ".TTML", ".Ttml" });
            if (sidecarTtml != null)
            {
                var (lines, plain) = TtmlParser.Parse(sidecarTtml);
                if (lines != null && lines.Count > 0)
                    return new LocalLyricsProbe(lines, plain, "Sidecar:Ttml", FromCache: false);
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
                    return new LocalLyricsProbe(lines, null, "Sidecar:Lrc", FromCache: false);
            }
        }
        catch { }

        // Priority 4: embedded metadata is pure in-memory — defer to the UI-thread handler.
        var hasSyncedField = !string.IsNullOrWhiteSpace(track.SyncedLyrics);
        var hasPlainField = !string.IsNullOrWhiteSpace(track.Lyrics);
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
                    return new LocalLyricsProbe(lines, plain, "Cache:Lyricsfile", FromCache: true);
            }

            var cachedLrc = TryReadCacheFile(track.Id, ".lrc");
            if (cachedLrc != null)
            {
                if (cachedLrc.Contains('[') && LrcTimestampRegex().IsMatch(cachedLrc))
                {
                    var lines = ParseLrcContent(cachedLrc);
                    if (lines.Count > 0)
                        return new LocalLyricsProbe(lines, null, "Cache:Lrc", FromCache: true);
                }
                return new LocalLyricsProbe(null, cachedLrc, "Cache:Plain", FromCache: true);
            }
        }
        catch { }

        return new LocalLyricsProbe(null, null, "None", FromCache: false);
    }

    private void ApplyLocalLyricsResult(Track track, LocalLyricsProbe probe)
    {
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
                InsertIntroPlaceholderIfNeeded(probe.Lines);
            }

            LyricLines.ReplaceAll(probe.Lines);

            if (!string.IsNullOrWhiteSpace(probe.UnsyncedPlain))
                PopulateUnsyncedFromPlainText(probe.UnsyncedPlain);
            else
                PopulateUnsyncedLines(probe.Lines);

            AutoSelectTab();
            LyricsSourceName = string.Empty;
            if (probe.FromCache) CanRemoveLyrics = true;
            RefreshActiveLyricPosition();

            if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
                _lyricsSyncTimer.Start();
            return;
        }

        if (probe.Source == "Embedded")
        {
            LoadEmbeddedLyrics(track);
            if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
                _lyricsSyncTimer.Start();
            return;
        }

        if (probe.Source == "Cache:Plain" && !string.IsNullOrWhiteSpace(probe.UnsyncedPlain))
        {
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Source:CachePlain", $"trackId={track.Id}");
            var split = SplitPlainLyrics(probe.UnsyncedPlain);
            var rendered = new List<LyricLine>(split.Length);
            foreach (var line in split)
                rendered.Add(new LyricLine { Text = SoftWrapText(line), IsActive = true });
            LyricLines.ReplaceAll(rendered);
            UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true }));
            AutoSelectTab();
            LyricsSourceName = string.Empty;
            CanRemoveLyrics = true;
            RefreshActiveLyricPosition();
            return;
        }

        // No lyrics found — the stale lines were left up during the probe
        // (deferred clear), so empty the collections here, then show search only.
        DebugLogger.Warn(DebugLogger.Category.Lyrics, "NoLyricsFound", $"title={track.Title}, artist={track.Artist}");
        LyricLines.Clear();
        UnsyncedLines.Clear();
        ShowSearchButton = true;
        AutoSelectTab();
        RefreshActiveLyricPosition();
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
                                 && LrcTimestampRegex().IsMatch(track.Lyrics);

        DebugLogger.Info(DebugLogger.Category.Lyrics, "Source:Embedded",
            $"synced={hasSyncedField}, plain={hasPlainField}, lrcInPlain={plainIsActuallyLrc}");

        var syncedSource = hasSyncedField ? track.SyncedLyrics
                         : plainIsActuallyLrc ? track.Lyrics
                         : null;

        if (!string.IsNullOrWhiteSpace(syncedSource))
        {
            var parsedLines = ParseLrcContent(syncedSource);
            _hasSyncedLyrics = parsedLines.Any(l => l.IsSynced);
            IsSynced = _hasSyncedLyrics;
            HasSyncedLyricsAvailable = _hasSyncedLyrics;

            if (_hasSyncedLyrics)
                InsertIntroPlaceholderIfNeeded(parsedLines);

            LyricLines.ReplaceAll(parsedLines);
            PopulateUnsyncedLines(parsedLines);
        }

        if (hasPlainField && !plainIsActuallyLrc)
        {
            var split = SplitPlainLyrics(track.Lyrics);
            if (!hasSyncedField)
            {
                var rendered = new List<LyricLine>(split.Length);
                foreach (var line in split)
                    rendered.Add(new LyricLine { Text = SoftWrapText(line), IsActive = true });
                LyricLines.ReplaceAll(rendered);
                UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true }));
            }
            else
            {
                var unsynced = new List<LyricLine>(split.Length);
                foreach (var line in split)
                    unsynced.Add(new LyricLine { Text = SoftWrapText(line), IsActive = true });
                UnsyncedLines.ReplaceAll(unsynced);
            }
        }

        AutoSelectTab();
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
    {
        var bytes = File.ReadAllBytes(path);

        // A BOM is authoritative — let the framework handle it.
        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
             (bytes[0] == 0xFE && bytes[1] == 0xFF) ||
             (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)))
        {
            return File.ReadAllText(path);
        }

        // Strict UTF-8 first: throwOnInvalidBytes turns "not UTF-8" into a signal rather
        // than a string full of replacement characters.
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8. Use the OS default ANSI code page, which is the right guess for
            // a file authored on the user's own machine; Latin1 elsewhere so every byte
            // maps to something rather than being dropped.
            try
            {
                var ansi = System.Text.Encoding.GetEncoding(0);
                return ansi.GetString(bytes);
            }
            catch
            {
                return System.Text.Encoding.Latin1.GetString(bytes);
            }
        }
    }

    private void PopulateUnsyncedLines(List<LyricLine> sourceLyrics)
    {
        var batch = new List<LyricLine>(sourceLyrics.Count);
        foreach (var line in sourceLyrics)
        {
            // Skip intro placeholder "..."
            if (line.Timestamp == TimeSpan.Zero && line.Text == "...") continue;
            batch.Add(new LyricLine { Text = LyricsTextHelper.CleanDisplayText(line.Text), IsActive = true });
        }
        UnsyncedLines.ReplaceAll(batch);
    }

    /// <summary>Populates the Unsync tab from a Lyricsfile's `plain` block (preserves blank-line spacing).</summary>
    private void PopulateUnsyncedFromPlainText(string plain)
    {
        var split = SplitPlainLyrics(plain);
        var batch = new List<LyricLine>(split.Length);
        foreach (var line in split)
            batch.Add(new LyricLine { Text = SoftWrapText(line), IsActive = true });
        UnsyncedLines.ReplaceAll(batch);
    }

    private void AutoSelectTab()
    {
        if (_hasSyncedLyrics)
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

    /// <summary>
    /// If the first synced lyric starts after 2 seconds, inserts a "…" placeholder
    /// at timestamp zero. This matches Apple Music's "waiting for lyrics" behavior
    /// during intros — the placeholder becomes the active line until the first
    /// real lyric is reached.
    /// </summary>
    private static void InsertIntroPlaceholderIfNeeded(List<LyricLine> lines)
    {
        var firstSynced = lines.FirstOrDefault(l => l.IsSynced);
        if (firstSynced?.Timestamp != null && firstSynced.Timestamp.Value.TotalSeconds > 2)
        {
            lines.Insert(0, new LyricLine
            {
                Timestamp = TimeSpan.Zero,
                Text = "...",
                IsIntroPlaceholder = true
            });
        }
    }

    /// <summary>
    /// Splits a long lyric line into balanced halves at the word boundary closest to the midpoint.
    /// Recursively applies to each half if still too long. Produces clean, cinematic two-line wraps.
    /// </summary>
    private static string SoftWrapText(string text, int maxWidth = 25)
    {
        // Strip exotic Unicode (NBSP, separators, zero-width, replacement) that render
        // as empty boxes; this is the common funnel for every displayed lyric line.
        text = LyricsTextHelper.CleanDisplayText(text);
        if (text.Length <= maxWidth) return text;

        // Find the space closest to the midpoint for two balanced halves
        var mid = text.Length / 2;
        int bestSpace = -1;
        var bestDist = int.MaxValue;

        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != ' ') continue;
            var dist = Math.Abs(i - mid);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestSpace = i;
            }
        }

        if (bestSpace <= 0) return text;

        // Single split only — never more than 2 lines per lyric
        var line1 = text[..bestSpace];
        var line2 = text[(bestSpace + 1)..];

        // If either half is still too long for the active font size, split it too
        if (line1.Length > maxWidth)
            line1 = SoftWrapText(line1, maxWidth);
        if (line2.Length > maxWidth)
            line2 = SoftWrapText(line2, maxWidth);

        return line1 + "\n" + line2;
    }

    /// <summary>
    /// Parses LRC format content into LyricLine objects.
    /// Supports: [mm:ss.xx] text, [mm:ss] text, multiple timestamps per line.
    /// Ignores metadata tags like [ar:], [ti:], [al:], etc.
    /// </summary>
    /// <summary>
    /// Upper bound on lines produced from one file. The lyrics list is not virtualized —
    /// every line is realized, and a word-timed line is ~7 controls per word plus a
    /// BlurEffect — so an oversized or hostile sidecar (a 1 MB .lrc, or one line carrying
    /// thousands of stacked [mm:ss.xx] tags, since each tag emits its own LyricLine) would
    /// build tens of thousands of controls in a single UI-thread pass. No real song comes
    /// close to this.
    /// </summary>
    private const int MaxLyricLines = 3000;

    /// <summary>Splits plain lyrics into display lines, bounded by <see cref="MaxLyricLines"/>.</summary>
    private static string[] SplitPlainLyrics(string text)
    {
        var split = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        return split.Length <= MaxLyricLines ? split : split[..MaxLyricLines];
    }

    private static List<LyricLine> ParseLrcContent(string content)
    {
        var lines = new List<LyricLine>();
        var rawLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var offsetMs = ParseLrcOffsetMilliseconds(rawLines);

        foreach (var rawLine in rawLines)
        {
            if (lines.Count >= MaxLyricLines) break;

            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Offset is handled once globally before parsing timestamps.
            if (OffsetTagRegex().IsMatch(trimmed))
                continue;

            // Skip metadata tags like [ar:Artist], [ti:Title], [al:Album], [offset:], [length:]
            if (MetadataTagRegex().IsMatch(trimmed))
                continue;

            // Extract all timestamps from the line
            var matches = LrcTimestampRegex().Matches(trimmed);
            if (matches.Count > 0)
            {
                // Get the text after all timestamps. For enhanced ("A2") LRC this
                // body carries inline <mm:ss.xx> word tags, which we split into
                // per-word karaoke timings and strip from the displayed text.
                var lastMatch = matches[^1];
                var body = trimmed[(lastMatch.Index + lastMatch.Length)..];
                var (text, words) = EnhancedLrcParser.ParseLine(body);

                // Skip empty timestamp lines — LRC files often end with
                // [03:24.00] (no text) as an end marker. If parsed, this empty
                // line becomes the "active" line and deactivates the previous
                // real lyric, making lyrics appear to stop early.
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Word timings are absolute; attaching them to a multi-timestamp
                // (compressed) line would misalign the later occurrences, so only
                // carry word-level data when the line has a single timestamp.
                var lineWords = matches.Count == 1 ? words : null;

                // Create a LyricLine for each timestamp (handles multi-timestamp lines)
                foreach (Match match in matches)
                {
                    if (lines.Count >= MaxLyricLines) break;

                    var timestamp = ParseLrcTimestamp(match.Value);
                    if (timestamp.HasValue)
                    {
                        var adjusted = timestamp.Value + TimeSpan.FromMilliseconds(offsetMs);
                        if (adjusted < TimeSpan.Zero)
                            adjusted = TimeSpan.Zero;

                        var line = new LyricLine
                        {
                            Timestamp = adjusted,
                            Text = SoftWrapText(text)
                        };

                        if (lineWords != null)
                        {
                            var shifted = offsetMs == 0 ? lineWords : ShiftWords(lineWords, offsetMs);
                            // End before Words: the Words setter computes held-note
                            // emphasis, and the last word's span needs the line end.
                            line.EndTimestamp = shifted[^1].End;
                            line.Words = shifted;
                        }

                        lines.Add(line);
                    }
                }
            }
            else
            {
                // No timestamp — add as unsynced line
                lines.Add(new LyricLine { Text = SoftWrapText(trimmed) });
            }
        }

        // Sort by timestamp for synced lyrics. Stable (OrderBy) so lines sharing a
        // timestamp — e.g. an adlib synced to the same instant as its main line —
        // keep their file order, which the background fold below relies on.
        var sorted = lines
            .OrderBy(l => l.Timestamp == null ? 1 : 0)
            .ThenBy(l => l.Timestamp ?? TimeSpan.Zero)
            .ToList();
        lines.Clear();
        lines.AddRange(sorted);

        // Fold parenthesized adlib lines into the preceding line's background layer
        // (Apple Music-style background vocals).
        EnhancedLrcParser.FoldBackgroundLines(lines);

        return lines;
    }

    /// <summary>Applies the global LRC offset to absolute word timings.</summary>
    private static List<WordTiming> ShiftWords(List<WordTiming> words, int offsetMs)
    {
        var delta = TimeSpan.FromMilliseconds(offsetMs);
        var shifted = new List<WordTiming>(words.Count);
        foreach (var w in words)
        {
            var start = w.Start + delta;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            TimeSpan? end = w.End.HasValue ? w.End.Value + delta : null;
            if (end < TimeSpan.Zero) end = TimeSpan.Zero;
            shifted.Add(new WordTiming { Text = w.Text, Start = start, End = end });
        }
        return shifted;
    }

    private static int ParseLrcOffsetMilliseconds(string[] rawLines)
    {
        foreach (var rawLine in rawLines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var match = OffsetTagRegex().Match(trimmed);
            if (match.Success &&
                int.TryParse(match.Groups["offset"].Value, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    /// <summary>
    /// Parses a single LRC timestamp like [01:23.45] or [01:23] into a TimeSpan.
    /// </summary>
    private static TimeSpan? ParseLrcTimestamp(string timestamp)
    {
        // Remove brackets
        var inner = timestamp.Trim('[', ']').Replace(',', '.');
        var parts = inner.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return null;

        if (!int.TryParse(parts[0], out var minutes)) return null;

        if (parts.Length == 2)
        {
            // Seconds can be "23.45", "23,45", or "23"
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return null;

            return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }

        // Supports mm:ss:ff and mm:ss:fff variants.
        if (!int.TryParse(parts[1], out var wholeSeconds)) return null;
        if (!int.TryParse(parts[2], out var fractionalUnit)) return null;

        var divisor = Math.Pow(10, parts[2].Length);
        var fractionalSeconds = fractionalUnit / divisor;
        return TimeSpan.FromMinutes(minutes) +
               TimeSpan.FromSeconds(wholeSeconds + fractionalSeconds);
    }

    /// <summary>Whether the lyric share-card entry point should be visible.</summary>
    public bool ShareAvailable => _player.CurrentTrack != null && !IsSearching && !ShowSearchButton;

    partial void OnShowSearchButtonChanged(bool value) => OnPropertyChanged(nameof(ShareAvailable));
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShareAvailable));

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

        int preselect = _currentActiveLine != null ? shareable.IndexOf(_currentActiveLine) : 0;
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

        var vm = new LrcEditorViewModel(track, _player, _metadata, synced, plain);

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
    private string? BuildSeedFromLoadedLines()
    {
        if (!_hasSyncedLyrics || LyricLines.Count == 0) return null;

        var lines = LyricLines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.Text != "...")
            .Select(l => (l.Timestamp, Text: l.Text.Replace("\r\n", " ").Replace('\n', ' ').Trim()))
            .ToList();

        return lines.Any(l => l.Timestamp.HasValue)
            ? LrcEditorViewModel.BuildLrcPreservingUntimed(lines)
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
        _player.SeekToPositionCommand.Execute(
            line.Timestamp.Value.TotalSeconds / _player.Duration.TotalSeconds);
    }

    /// <summary>
    /// Updates the currently active (highlighted) lyric line based on playback position.
    /// Called from OnPlayerPropertyChanged which fires on UI thread, so no extra dispatch needed.
    /// A 350ms lookahead compensates for VLC position polling latency + UI dispatch delay,
    /// ensuring lyrics highlight at the moment the vocal begins rather than after.
    ///
    /// Uses a monotonic cursor (_lineCursor) that advances forward per tick — O(1) amortized
    /// instead of scanning every line on every tick. Resets to 0 on seek-backwards.
    /// </summary>
    private static readonly TimeSpan LyricsLookahead = TimeSpan.FromMilliseconds(350);

    // Word-level lookahead: small lead so the sweep matches the vocal instead of trailing
    // UI dispatch latency. AMLL feels in-sync around 80ms — bigger leads start to read
    // as the colour racing ahead of the voice.
    private static readonly TimeSpan WordLookahead = TimeSpan.FromMilliseconds(80);

    private void UpdateActiveLine(TimeSpan position)
    {
        if (LyricLines.Count == 0) return;

        // Seek-backwards detection: rewind the cursor so we don't miss earlier lines.
        // 750ms threshold tolerates small non-monotonic jitter from the player position poll.
        if (_lastSyncPosition != TimeSpan.MinValue &&
            position + TimeSpan.FromMilliseconds(750) < _lastSyncPosition)
        {
            _lineCursor = 0;
        }
        _lastSyncPosition = position;

        // Per-line lookahead: word-timed lines activate on the word clock's small lead.
        // The 350ms line-level lead would switch lines early, force-completing the
        // outgoing line's final word sweep ~270ms before its end and leaving the
        // incoming words dark — a visible jump-then-pause at every line boundary.
        TimeSpan AdjustedFor(LyricLine l) =>
            position + (l.HasWords || l.HasBackgroundWords ? WordLookahead : LyricsLookahead);

        // Clamp cursor into range (collection may have shrunk).
        if (_lineCursor >= LyricLines.Count) _lineCursor = LyricLines.Count - 1;
        if (_lineCursor < 0) _lineCursor = 0;

        // Advance forward while the next synced line's timestamp has been reached.
        while (_lineCursor + 1 < LyricLines.Count)
        {
            var next = LyricLines[_lineCursor + 1];
            if (next.Timestamp.HasValue && next.Timestamp.Value <= AdjustedFor(next))
                _lineCursor++;
            else
                break;
        }

        // Mirror walk backwards. The forward walk above has no threshold, but rewinding
        // used to depend solely on the 750ms reset — and a timeline drag never produces
        // a drop that large in one sample, because this runs every 100ms off the sync
        // timer and every rendered frame off the word clock. The cursor stayed parked on
        // a later line, no candidate matched, and the safety branch below held the stale
        // line: dragging backwards froze the lyrics until release finally delivered a big
        // enough discontinuity. Stepping back per sample makes both directions resolve on
        // the same tick, and still costs nothing during normal forward playback.
        var rewound = false;
        while (_lineCursor > 0)
        {
            var current = LyricLines[_lineCursor];
            if (current.Timestamp.HasValue && current.Timestamp.Value > AdjustedFor(current))
            {
                _lineCursor--;
                rewound = true;
            }
            else
                break;
        }

        var candidate = LyricLines[_lineCursor];
        LyricLine? bestMatch = null;
        int bestIndex = -1;
        if (candidate.Timestamp.HasValue && candidate.Timestamp.Value <= AdjustedFor(candidate))
        {
            bestMatch = candidate;
            bestIndex = _lineCursor;
        }

        // Safety: if no match found but we're past the start and have a current line,
        // keep the current line active (prevents "all dimmed" state from transient glitches).
        // Skipped when the walk above rewound to the very first line and even that one is
        // still ahead: the position genuinely sits before the first lyric, so holding the
        // stale line there would re-freeze exactly what the backward walk exists to fix.
        if (bestMatch == null && !rewound && _currentActiveLine != null && position.TotalSeconds > 1)
        {
            UpdateActiveWord(position);
            return;
        }

        if (bestMatch != _currentActiveLine)
        {
            // Deactivate previous line — leave it fully swept (index past the end) so the
            // bright overlay keeps covering the words while the base layer fades back to
            // full opacity. Snapping to -1 here blanked the overlay instantly, which read
            // as the finished line dimming for a beat. Re-entry recomputes the real index.
            if (_currentActiveLine != null)
            {
                _currentActiveLine.IsActive = false;
                if (_currentActiveLine.HasWords)
                    _currentActiveLine.CurrentWordIndex = _currentActiveLine.Words!.Count;
                if (_currentActiveLine.HasBackgroundWords)
                    _currentActiveLine.BackgroundWordIndex = _currentActiveLine.BackgroundWords!.Count;
            }

            // Activate new line
            if (bestMatch != null)
                bestMatch.IsActive = true;

            _currentActiveLine = bestMatch;
            ActiveLineIndex = bestIndex;
            UpdateLineOpacities(bestIndex);
            UpdateWordClockSubscription();
        }
        else if (bestMatch != null && !bestMatch.IsActive)
        {
            // Safety: ensure the active line stays active even if something reset it
            bestMatch.IsActive = true;
        }

        UpdateActiveWord(position);
    }

    /// <summary>
    /// Advances CurrentWordIndex on the active line when word-level timings are present.
    /// No-op when the active line has no words — existing line-level highlight is all that renders.
    /// </summary>
    private void UpdateActiveWord(TimeSpan position)
    {
        var line = _currentActiveLine;
        if (line == null || (!line.HasWords && !line.HasBackgroundWords)) return;

        var adjusted = position + WordLookahead;

        if (line.HasWords)
            DriveWordLayer(line.Words!, adjusted, line.EndTimestamp,
                line.CurrentWordIndex, i => line.CurrentWordIndex = i);

        // Background vocals (adlibs) run as an independent layer with their own clock.
        if (line.HasBackgroundWords)
            DriveWordLayer(line.BackgroundWords!, adjusted, line.BackgroundEndTimestamp,
                line.BackgroundWordIndex, i => line.BackgroundWordIndex = i);
    }

    /// <summary>Advances one word layer's current-word index and sweeps the active word.</summary>
    private void DriveWordLayer(
        IReadOnlyList<WordTiming> words, TimeSpan adjusted, TimeSpan? layerEnd,
        int currentIndex, Action<int> setIndex)
    {
        // Past the layer's end → last word remains highlighted until the line changes.
        int target;
        if (adjusted < words[0].Start)
        {
            target = -1;
        }
        else
        {
            target = words.Count - 1;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var end = w.End ?? (i + 1 < words.Count ? words[i + 1].Start : TimeSpan.MaxValue);
                if (adjusted < end)
                {
                    target = i;
                    break;
                }
            }
        }

        if (currentIndex != target)
            setIndex(target);

        // Drive the AMLL-style sweep on the current word AND its immediate
        // neighbours, on the same lookahead-adjusted clock as the index above.
        // BandProgress keeps moving a little past both ends of each word, so the
        // feathered edge finishes crossing the previous token while it is already
        // entering the next — clamping at [0,1] here is what used to park the band
        // at every token boundary of a slow passage. Before the line starts
        // (target -1) this pre-rolls word 0; past the layer end the last word
        // settles at the inert-past sentinel (fully lit) until the line changes.
        var first = Math.Max(0, target - 1);
        var last = Math.Min(words.Count - 1, target + 1);
        for (int i = first; i <= last; i++)
        {
            var w = words[i];
            // Last word of a start-tag-only layer has no end anywhere — bound it by
            // the next line's start (capped) so it sweeps instead of snapping to lit.
            var end = w.End ?? (i + 1 < words.Count
                ? words[i + 1].Start
                : layerEnd ?? KaraokeSweep.ResolveOpenLastWordEnd(w.Start, NextSyncedLineStart()));
            var progress = KaraokeSweep.BandProgress(
                w.Start.TotalSeconds, end.TotalSeconds, adjusted.TotalSeconds);
            if (w.Progress != progress)
                w.Progress = progress;
        }
    }

    /// <summary>Start of the first synced line after the active one; null when none.</summary>
    private TimeSpan? NextSyncedLineStart()
    {
        if (ActiveLineIndex < 0) return null;
        for (int i = ActiveLineIndex + 1; i < LyricLines.Count; i++)
        {
            var ts = LyricLines[i].Timestamp;
            if (ts.HasValue) return ts;
        }
        return null;
    }

    /// <summary>
    /// Continuous playback clock. LibVLC refreshes its cached Time only every
    /// ~150-300ms, so raw reads move in coarse steps that make the karaoke sweep
    /// visibly jump. Extrapolates with a Stopwatch between raw updates while playing;
    /// small backward re-anchors are held (monotonic), real seeks pass through.
    /// </summary>
    private TimeSpan GetPlaybackPosition()
    {
        var raw = _player.Position;
        if (_player.State != Models.PlaybackState.Playing)
        {
            // Not advancing — drop the anchor so resume re-anchors fresh (an anchor
            // held across a pause would otherwise add the pause length on resume).
            _clockRawMs = -1;
            _clockLastMs = raw.TotalMilliseconds;
            return raw;
        }

        // While the timeline is being dragged, Position is a target the user is steering,
        // not a clock that is running — VLC has not been told to seek yet, that happens on
        // release. Extrapolating it forward actively fought a backward drag: hold the
        // slider still and the estimate crept ahead by up to a second (the stall-guard
        // cap), pulling the active line back down the list, then snapped up again when the
        // drag resumed. A forward drag hid this completely because the creep pointed the
        // same way the user was going. Re-anchor on the raw value so extrapolation resumes
        // cleanly the moment the drag ends.
        if (_player.IsSeeking)
        {
            _clockRawMs = (long)raw.TotalMilliseconds;
            _clockAnchorMs = _clockRawMs;
            _clockAnchorTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _clockLastMs = raw.TotalMilliseconds;
            return raw;
        }

        var rawMs = (long)raw.TotalMilliseconds;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        // A raw value below the PREVIOUS raw value is the player itself moving backwards —
        // a seek, or the timeline slider writing its target while the user drags. The
        // monotonic guard below exists for VLC republishing a time behind our extrapolation,
        // which never lowers the raw value, so the two cases are distinguishable. Without
        // this, a slow drag backwards was smoothed away 300ms at a time and the position
        // handed to UpdateActiveLine never actually moved back.
        var rawMovedBack = _clockRawMs >= 0 && rawMs < _clockRawMs;
        if (rawMs != _clockRawMs)
        {
            _clockRawMs = rawMs;
            _clockAnchorMs = rawMs;
            _clockAnchorTimestamp = now;
        }

        var elapsedMs = (now - _clockAnchorTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // Stall guard: if VLC stops publishing time (buffering hiccup), stop
        // extrapolating past 1s rather than running away from the real position.
        if (elapsedMs > 1000) elapsedMs = 1000;
        var estimate = _clockAnchorMs + elapsedMs;

        // Monotonic guard: a fresh raw value slightly behind our extrapolation would
        // step the sweep backwards — hold instead. Larger drops are real seeks.
        if (!rawMovedBack && estimate < _clockLastMs && _clockLastMs - estimate < 300)
            estimate = _clockLastMs;
        _clockLastMs = estimate;
        return TimeSpan.FromMilliseconds(estimate);
    }

    /// <summary>
    /// Starts a RequestAnimationFrame loop on the main window while a word-synced
    /// line is actively playing (one callback per rendered frame → frame-smooth
    /// sweep). The loop stops itself the moment word-level rendering goes idle, so
    /// there is no per-frame cost for line-only lyrics or paused playback; the 100ms
    /// sync timer restarts it when a word-synced line becomes active again.
    /// </summary>
    private void UpdateWordClockSubscription()
    {
        if (!WantsWordClock || _wordClockRunning) return;
        if (Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } topLevel) return;

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
    }

    private bool IsAnyLyricsSurfaceVisible => _visibleLyricsSurfaces > 0;

    private bool WantsWordClock =>
        _hasSyncedLyrics
        && IsAnyLyricsSurfaceVisible
        && _player.State == Models.PlaybackState.Playing
        && _lyricsSyncTimer.IsEnabled
        && (_currentActiveLine?.HasWords == true || _currentActiveLine?.HasBackgroundWords == true);

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

        if (Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } topLevel)
        {
            topLevel.RequestAnimationFrame(OnWordClockFrame);
        }
        else
        {
            _wordClockRunning = false;
        }
    }

    /// <summary>
    /// Sets LineOpacity on each lyric line based on distance from the active line.
    /// Active=1.0, adjacent lines fade gradually over ±9 lines, rest=0.0 (hidden);
    /// fullscreen focus (opt-in) tightens the ramp to the active line ±2.
    /// Pass activeIndex=-1 to restore all lines to full opacity (e.g. unsynced or reset).
    /// </summary>
    private void UpdateLineOpacities(int activeIndex)
    {
        if (activeIndex < 0)
        {
            foreach (var line in LyricLines)
            {
                line.LineOpacity = 1.0;
                line.IsClickable = true;
                if (line.BlurRadius != 0.0) line.BlurRadius = 0.0;
            }
            return;
        }

        // Fullscreen focus (opt-in): only the active line and ±2 neighbours stay visible
        // while the lyrics page fills a fullscreen window. The side panel shares these
        // lines but can never be open with the page up, so the tight ramp never leaks
        // into it.
        var focus = IsLyricsFocusActive;

        for (int i = 0; i < LyricLines.Count; i++)
        {
            var dist = i - activeIndex;
            var absDist = Math.Abs(dist);
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
        }
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

    [GeneratedRegex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"^\[(ar|ti|al|by|offset|re|ve|length|id):")]
    private static partial Regex MetadataTagRegex();

    [GeneratedRegex(@"^\[offset:(?<offset>[+-]?\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTagRegex();

    public void Dispose()
    {
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
