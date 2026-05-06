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

public record ColorSwatch(string Key, string Name, IBrush Preview, bool IsAuto = false);

/// <summary>
/// ViewModel for the Lyrics view that displays synchronized lyrics
/// alongside album art and playback controls.
/// Supports: embedded lyrics, .lrc files with timestamp syncing.
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

    [ObservableProperty] private bool _isColorModeSolid = true;
    [ObservableProperty] private bool _isColorModeGradient;
    [ObservableProperty] private string _activeSwatchKey = "";

    private static readonly List<ColorSwatch> _solidSwatches = BuildSolidSwatches();
    private static readonly List<ColorSwatch> _gradientSwatches = BuildGradientSwatches();

    public List<ColorSwatch> SolidSwatches => _solidSwatches;
    public List<ColorSwatch> GradientSwatches => _gradientSwatches;

    private static List<ColorSwatch> BuildSolidSwatches()
    {
        var auto = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#8B5CF6"), 0),
                new GradientStop(Color.Parse("#3B82F6"), 0.5),
                new GradientStop(Color.Parse("#10B981"), 1),
            }
        };

        return new List<ColorSwatch>
        {
            new("", "Auto", auto, IsAuto: true),
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
    // Interval adapts at runtime: line-only lyrics use LineSyncIntervalMs; word-level
    // lyrics bump to WordSyncIntervalMs only while the active line has word timings,
    // so idle cost stays the same.
    private readonly DispatcherTimer _lyricsSyncTimer;

    private const int LineSyncIntervalMs = 100;
    private const int WordSyncIntervalMs = 33;

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

    /// <summary>Index of the currently active lyric line (for auto-scroll).</summary>
    [ObservableProperty]
    private int _activeLineIndex = -1;

    /// <summary>Album metadata line (e.g. "2021 · Alternative · 17 tracks").</summary>
    [ObservableProperty]
    private string _albumInfoText = string.Empty;

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

    // ── Adaptive foreground colors (react to background luminance) ──

    [ObservableProperty] private IBrush _lyricsPrimaryFg = Brushes.White;
    [ObservableProperty] private IBrush _lyricsSecondaryFg = new SolidColorBrush(Color.Parse("#B0FFFFFF"));
    [ObservableProperty] private IBrush _lyricsAccentFg = new SolidColorBrush(Color.Parse("#E74856"));
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
            LyricsAccentFg = new SolidColorBrush(Color.Parse("#B91C2C"));
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
            LyricsAccentFg = new SolidColorBrush(Color.Parse("#FF6B7A"));
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
            LyricsAccentFg = new SolidColorBrush(Color.Parse("#E74856"));
            LyricsSubtleFg = new SolidColorBrush(Color.Parse("#999999"));
            LyricsSliderFilled = new SolidColorBrush(Color.Parse("#CCFFFFFF"));
            LyricsSliderUnfilled = new SolidColorBrush(Color.Parse("#33FFFFFF"));
            LyricsControlFill = Brushes.White;
            LyricsBtnBg = new SolidColorBrush(Color.Parse("#33FFFFFF"));
            LyricsBtnBgHover = new SolidColorBrush(Color.Parse("#55FFFFFF"));
            LyricsSliderThumb = new SolidColorBrush(Color.Parse("#EEFFFFFF"));
        }
    }

    private void ResetForegroundsToDefault()
    {
        LyricsPrimaryFg = Brushes.White;
        LyricsSecondaryFg = new SolidColorBrush(Color.Parse("#B0FFFFFF"));
        LyricsAccentFg = new SolidColorBrush(Color.Parse("#E74856"));
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

    public LyricsViewModel(PlayerViewModel player, ILrcLibService lrcLib, INetEaseService netEase, IMetadataService metadata, IPersistenceService persistence, ILibraryService library)
    {
        _player = player;
        _lrcLib = lrcLib;
        _netEase = netEase;
        _metadata = metadata;
        _persistence = persistence;
        _library = library;

        // Dedicated sync timer — polls player position and drives both line and word highlighting.
        // Default cadence is 100ms (line-level). The Tick handler adapts Interval down to ~33ms
        // when the active line has word timings, and back to 100ms when it doesn't.
        _lyricsSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs) };
        _lyricsSyncTimer.Tick += (_, _) =>
        {
            if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
                UpdateActiveLine(_player.Position);
        };

        // Subscribe to track changes to update lyrics
        _player.TrackStarted += OnTrackStarted;

        // Reload lyrics when metadata is edited (e.g. synced lyrics toggled off)
        _library.LibraryUpdated += OnLibraryUpdated;

        // Subscribe to state changes to start/stop the sync timer
        _player.PropertyChanged += OnPlayerPropertyChanged;

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
    private void UpdateAdaptiveBackground(Bitmap? albumArt)
    {
        // Don't override when a custom background color is selected
        if (_selectedColorHex != null) return;

        if (albumArt == null)
        {
            LeftPanelBrush = CreateDefaultGradient();
            LyricsBackgroundBrush = CreateDefaultSubduedGradient();
            FullBackgroundBrush = CreateDefaultUnifiedBrush();
            return;
        }

        try
        {
            var (dominant, secondary) = DominantColorExtractor.ExtractColorPalette(albumArt);
            var (left, right) = DominantColorExtractor.GenerateAdaptiveBrushes(dominant, secondary);
            LeftPanelBrush = left;
            LyricsBackgroundBrush = right;
            FullBackgroundBrush = DominantColorExtractor.GenerateUnifiedBrush(dominant, secondary);
        }
        catch
        {
            LeftPanelBrush = CreateDefaultGradient();
            LyricsBackgroundBrush = CreateDefaultSubduedGradient();
            FullBackgroundBrush = CreateDefaultUnifiedBrush();
        }
    }

    [RelayCommand]
    private void ResumeAutoFollow()
    {
        IsAutoFollowPaused = false;
    }

    [RelayCommand]
    private void SelectColorModeSolid()
    {
        IsColorModeSolid = true;
        IsColorModeGradient = false;
    }

    [RelayCommand]
    private void SelectColorModeGradient()
    {
        IsColorModeSolid = false;
        IsColorModeGradient = true;
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
        if (track != null)
            _viewAlbumAction?.Invoke(track);
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

            LrcLibResult? lrcLibResult = null;
            LrcLibResult? netEaseResult = null;

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

            await Task.WhenAll(tasks);

            async Task FetchLrcLibAsync()
            {
                try
                {
                    var result = await _lrcLib.GetLyricsAsync(artist, title, duration);
                    if (result == null || !result.HasLyrics)
                    {
                        var results = await _lrcLib.SearchLyricsAsync(artist, title);
                        // Prefer word-level (lyricsfile) > synced > any
                        result = results.FirstOrDefault(r => r.HasLyricsfile)
                              ?? results.FirstOrDefault(r => r.HasSyncedLyrics)
                              ?? results.FirstOrDefault(r => r.HasLyrics);
                    }
                    lrcLibResult = result;
                }
                catch (Exception ex)
                {
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
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:NotFound");
                LyricLines.Clear();
                UnsyncedLines.Clear();
                SearchFailedMessage = "No Lyrics found.";
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

        DisplayOnlineLyrics(_alternateOnlineResult);
        LyricsSourceName = _alternateSource;

        _alternateOnlineResult = prevResult;
        _alternateSource = prevSource;
        HasAlternateLyrics = prevResult != null && prevResult.HasLyrics;
        AlternateLyricsLabel = $"Try {prevSource}";
    }

    [RelayCommand]
    private void SaveLyricsToFile()
    {
        if (_currentTrack == null || _currentOnlineResult == null) return;

        var syncedToSave = _currentOnlineResult.SyncedLyrics;
        var plainToSave = !string.IsNullOrWhiteSpace(_currentOnlineResult.PlainLyrics)
            ? _currentOnlineResult.PlainLyrics
            : LyricsTextHelper.StripTimestamps(syncedToSave);

        if (string.IsNullOrWhiteSpace(syncedToSave) && string.IsNullOrWhiteSpace(plainToSave)) return;

        // Route plain text into Lyrics, synced text into SyncedLyrics — never mix them.
        _currentTrack.Lyrics = plainToSave ?? string.Empty;
        _currentTrack.SyncedLyrics = syncedToSave ?? string.Empty;

        try
        {
            // Root cause fix: writing embedded tags can fail while the media file is in use.
            // Save an LRC sidecar (synced) and a TXT sidecar (plain) next to the track.
            var trackPath = _currentTrack.FilePath;
            if (string.IsNullOrWhiteSpace(trackPath))
            {
                ShowStatusText("Save failed", 5000);
                return;
            }

            if (!string.IsNullOrWhiteSpace(syncedToSave))
            {
                var lrcPath = Path.ChangeExtension(trackPath, ".lrc");
                File.WriteAllText(lrcPath, NormalizeLyricsForLrc(syncedToSave), new UTF8Encoding(false));
            }

            if (!string.IsNullOrWhiteSpace(plainToSave))
            {
                var txtPath = Path.ChangeExtension(trackPath, ".txt");
                File.WriteAllText(txtPath, NormalizeLyricsForLrc(plainToSave), new UTF8Encoding(false));
            }

            // Best-effort metadata write (non-blocking for save success).
            try { _metadata.WriteTrackMetadata(_currentTrack); } catch { }

            CanSaveToFile = false;
            ShowStatusText("Saved Lyrics");
        }
        catch
        {
            ShowStatusText("Save failed — check file permissions", 5000);
        }
    }

    /// <summary>
    /// Removes the currently displayed online lyrics: clears the cached file,
    /// resets lyrics state, and shows the search button so the user can retry.
    /// </summary>
    [RelayCommand]
    private void RemoveLyrics()
    {
        if (_currentTrack == null) return;

        // Remove cached lyrics file
        try
        {
            var cachePath = Path.Combine(LyricsCacheDir, $"{_currentTrack.Id}.lrc");
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch { }

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
        return Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(LyricsCacheDir);
                var path = Path.Combine(LyricsCacheDir, $"{trackId}.lrc");
                File.WriteAllText(path, NormalizeLyricsForLrc(lyrics), new UTF8Encoding(false));
            }
            catch { }
        });
    }

    private static Task SaveLyricsfileToCacheAsync(Guid trackId, string yamlContent)
    {
        return Task.Run(() =>
        {
            try
            {
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
        Dispatcher.UIThread.Post(() =>
        {
            LoadLyricsForTrack(track);

            // If no local lyrics were found, trigger online search automatically
            if (ShowSearchButton)
                SearchLyricsCommand.Execute(null);
        });
    }

    private void DisplayOnlineLyrics(LrcLibResult result)
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
            var lines = result.PlainLyrics.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                var wrapped = SoftWrapText(line);
                rendered.Add(new LyricLine { Text = wrapped, IsActive = true });
            }
            LyricLines.ReplaceAll(rendered);
            UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true }));
        }

        AutoSelectTab();
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
        AlbumInfoText = string.Empty;
        _lyricsSyncTimer.Stop();
        _lyricsSyncTimer.Interval = TimeSpan.FromMilliseconds(LineSyncIntervalMs);

        LyricLines.Clear();
        UnsyncedLines.Clear();
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
                // Force full refresh: reset tracked state so UpdateActiveLine treats the
                // current line as a new match (fires PropertyChanged + UpdateLineOpacities).
                _currentActiveLine = null;
                ActiveLineIndex = -1;
                UpdateActiveLine(_player.Position);

                // Restart sync timer if playing
                if (_player.State == Models.PlaybackState.Playing && !_lyricsSyncTimer.IsEnabled)
                    _lyricsSyncTimer.Start();
            }
        }
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
            return;
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
            UpdateActiveLine(_player.Position);
        }
        // Update adaptive background when album art loads/changes
        else if (e.PropertyName == nameof(PlayerViewModel.AlbumArt))
        {
            UpdateAdaptiveBackground(_player.AlbumArt);
        }
    }

    private void LoadLyricsForTrack(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LoadLyricsForTrack", $"title={track.Title}, id={track.Id}");
        _currentTrack = track;
        _loadedLyrics = track.Lyrics;
        _loadedSyncedLyrics = track.SyncedLyrics;
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

        // Build album info text: "Genre · Year · N tracks"
        var infoParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Genre)) infoParts.Add(track.Genre);
        if (track.Year > 0) infoParts.Add(track.Year.ToString());
        if (track.TrackCount > 0) infoParts.Add($"{track.TrackCount} tracks");
        AlbumInfoText = string.Join(" \u00B7 ", infoParts);

        // Fire-and-forget: all file I/O runs off the UI thread, result is posted back.
        _ = LoadLocalLyricsAsync(track, generation);
    }

    /// <summary>
    /// Probes local lyric sources in priority order off the UI thread, applying the result
    /// via <see cref="Dispatcher.UIThread.Post"/>. Guarded by <see cref="_searchGeneration"/>
    /// so stale results from a previous track can't overwrite the current track's lyrics.
    ///
    /// Priority: .lyricsfile sidecar → .lrc sidecar → embedded tags → cache file.
    /// </summary>
    private async Task LoadLocalLyricsAsync(Track track, int generation)
    {
        var probe = await Task.Run(() => ProbeLocalLyricSources(track));

        if (generation != _searchGeneration) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _searchGeneration) return;
            ApplyLocalLyricsResult(track, probe);
        });
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

        // Priority 2: .lrc sidecar (line-level).
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

        // Priority 3: embedded metadata is pure in-memory — defer to the UI-thread handler.
        var hasSyncedField = !string.IsNullOrWhiteSpace(track.SyncedLyrics);
        var hasPlainField = !string.IsNullOrWhiteSpace(track.Lyrics);
        if (hasSyncedField || hasPlainField)
            return new LocalLyricsProbe(null, null, "Embedded", FromCache: false);

        // Priority 4: online cache (Lyricsfile preferred, fall back to .lrc).
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
            var split = probe.UnsyncedPlain.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var rendered = new List<LyricLine>(split.Length);
            foreach (var line in split)
                rendered.Add(new LyricLine { Text = SoftWrapText(line), IsActive = true });
            LyricLines.ReplaceAll(rendered);
            UnsyncedLines.ReplaceAll(rendered.Select(r => new LyricLine { Text = r.Text, IsActive = true }));
            AutoSelectTab();
            LyricsSourceName = string.Empty;
            CanRemoveLyrics = true;
            return;
        }

        // No lyrics found — show search button only
        DebugLogger.Warn(DebugLogger.Category.Lyrics, "NoLyricsFound", $"title={track.Title}, artist={track.Artist}");
        ShowSearchButton = true;
        AutoSelectTab();
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
            var split = track.Lyrics.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
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
                return File.ReadAllText(path);
        }
        return null;
    }

    private static string? TryReadCacheFile(Guid trackId, string extension)
    {
        try
        {
            var path = Path.Combine(LyricsCacheDir, trackId + extension);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        catch { }
        return null;
    }

    private void PopulateUnsyncedLines(List<LyricLine> sourceLyrics)
    {
        var batch = new List<LyricLine>(sourceLyrics.Count);
        foreach (var line in sourceLyrics)
        {
            // Skip intro placeholder "..."
            if (line.Timestamp == TimeSpan.Zero && line.Text == "...") continue;
            batch.Add(new LyricLine { Text = line.Text, IsActive = true });
        }
        UnsyncedLines.ReplaceAll(batch);
    }

    /// <summary>Populates the Unsync tab from a Lyricsfile's `plain` block (preserves blank-line spacing).</summary>
    private void PopulateUnsyncedFromPlainText(string plain)
    {
        var split = plain.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
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
    private static List<LyricLine> ParseLrcContent(string content)
    {
        var lines = new List<LyricLine>();
        var rawLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var offsetMs = ParseLrcOffsetMilliseconds(rawLines);

        foreach (var rawLine in rawLines)
        {
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
                // Get the text after all timestamps
                var lastMatch = matches[^1];
                var text = trimmed[(lastMatch.Index + lastMatch.Length)..].Trim();

                // Skip empty timestamp lines — LRC files often end with
                // [03:24.00] (no text) as an end marker. If parsed, this empty
                // line becomes the "active" line and deactivates the previous
                // real lyric, making lyrics appear to stop early.
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Create a LyricLine for each timestamp (handles multi-timestamp lines)
                foreach (Match match in matches)
                {
                    var timestamp = ParseLrcTimestamp(match.Value);
                    if (timestamp.HasValue)
                    {
                        var adjusted = timestamp.Value + TimeSpan.FromMilliseconds(offsetMs);
                        if (adjusted < TimeSpan.Zero)
                            adjusted = TimeSpan.Zero;

                        lines.Add(new LyricLine
                        {
                            Timestamp = adjusted,
                            Text = SoftWrapText(text)
                        });
                    }
                }
            }
            else
            {
                // No timestamp — add as unsynced line
                lines.Add(new LyricLine { Text = SoftWrapText(trimmed) });
            }
        }

        // Sort by timestamp for synced lyrics
        lines.Sort((a, b) =>
        {
            if (a.Timestamp == null && b.Timestamp == null) return 0;
            if (a.Timestamp == null) return 1;
            if (b.Timestamp == null) return -1;
            return a.Timestamp.Value.CompareTo(b.Timestamp.Value);
        });

        return lines;
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

    /// <summary>
    /// Seeks playback to the timestamp of a clicked lyric line.
    /// </summary>
    [RelayCommand]
    private void SeekToLine(LyricLine? line)
    {
        if (line?.Timestamp == null || _player.Duration.TotalSeconds <= 0) return;
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

    // Word-level lookahead is smaller: word timings come from real audio alignment, so we only
    // compensate UI dispatch latency, not polling jitter as with line-level.
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

        var adjusted = position + LyricsLookahead;

        // Clamp cursor into range (collection may have shrunk).
        if (_lineCursor >= LyricLines.Count) _lineCursor = LyricLines.Count - 1;
        if (_lineCursor < 0) _lineCursor = 0;

        // Advance forward while the next synced line's timestamp has been reached.
        while (_lineCursor + 1 < LyricLines.Count)
        {
            var next = LyricLines[_lineCursor + 1];
            if (next.Timestamp.HasValue && next.Timestamp.Value <= adjusted)
                _lineCursor++;
            else
                break;
        }

        var candidate = LyricLines[_lineCursor];
        LyricLine? bestMatch = null;
        int bestIndex = -1;
        if (candidate.Timestamp.HasValue && candidate.Timestamp.Value <= adjusted)
        {
            bestMatch = candidate;
            bestIndex = _lineCursor;
        }

        // Safety: if no match found but we're past the start and have a current line,
        // keep the current line active (prevents "all dimmed" state from transient glitches).
        if (bestMatch == null && _currentActiveLine != null && position.TotalSeconds > 1)
        {
            UpdateActiveWord(position);
            return;
        }

        if (bestMatch != _currentActiveLine)
        {
            // Deactivate previous line — clear its word cursor so re-entry rebuilds from scratch.
            if (_currentActiveLine != null)
            {
                _currentActiveLine.IsActive = false;
                if (_currentActiveLine.HasWords)
                    _currentActiveLine.CurrentWordIndex = -1;
            }

            // Activate new line
            if (bestMatch != null)
                bestMatch.IsActive = true;

            _currentActiveLine = bestMatch;
            ActiveLineIndex = bestIndex;
            UpdateLineOpacities(bestIndex);
            AdjustSyncCadence();
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
        if (line == null || !line.HasWords) return;

        var words = line.Words!;
        var adjusted = position + WordLookahead;

        // Past the line's end → last word remains highlighted until the line changes.
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

        if (line.CurrentWordIndex != target)
            line.CurrentWordIndex = target;
    }

    /// <summary>
    /// Bumps the sync timer cadence up when we're inside a word-synced line, back down otherwise.
    /// Keeps cost proportional to what's actually on screen.
    /// </summary>
    private void AdjustSyncCadence()
    {
        var wantsFast = _currentActiveLine?.HasWords == true;
        var targetMs = wantsFast ? WordSyncIntervalMs : LineSyncIntervalMs;
        if ((int)_lyricsSyncTimer.Interval.TotalMilliseconds != targetMs)
            _lyricsSyncTimer.Interval = TimeSpan.FromMilliseconds(targetMs);
    }

    /// <summary>
    /// Sets LineOpacity on each lyric line based on distance from the active line.
    /// Active=1.0, adjacent lines fade gradually over ±9 lines, rest=0.0 (hidden).
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
            }
            return;
        }

        for (int i = 0; i < LyricLines.Count; i++)
        {
            var dist = i - activeIndex;
            var absDist = Math.Abs(dist);
            var opacity = absDist switch
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
            var line = LyricLines[i];
            // Only set if changed — avoids unnecessary PropertyChanged notifications and re-renders
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (line.LineOpacity != opacity)
                line.LineOpacity = opacity;
            var clickable = opacity > 0.0;
            if (line.IsClickable != clickable)
                line.IsClickable = clickable;
        }
    }

    [GeneratedRegex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"^\[(ar|ti|al|by|offset|re|ve|length|id):")]
    private static partial Regex MetadataTagRegex();

    [GeneratedRegex(@"^\[offset:(?<offset>[+-]?\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTagRegex();

    public void Dispose()
    {
        // Stop and dispose timer to prevent memory leak
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
    }
}
