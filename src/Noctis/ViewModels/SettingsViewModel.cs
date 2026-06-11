using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Loon;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the unified Settings page (tabbed modal with search).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IPersistenceService _persistence;
    private readonly ILibraryService _library;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private IAudioPlayer? _audioPlayer;
    private PlayerViewModel? _player;
    private IDiscordPresenceService? _discord;
    private LoonClient? _loon;
    private ILastFmService? _lastFm;
    private IListenBrainzService? _listenBrainz;
    private ArtistImageService? _artistImageService;
    private UpdateService? _updateService;
    private CancellationTokenSource? _updateCts;
    private string? _downloadedInstallerPath;
    private CancellationTokenSource? _lastFmAuthCts;
    private bool _settingsLoaded;
    private bool _suspendSettingPersistence;
    private CancellationTokenSource? _eqSaveDebounceCts;
    private CancellationTokenSource? _scanStatusClearCts;

    [ObservableProperty] private int _mediaFoldersScrollRequest;

    // ── Tabs ──
    // The Settings modal is split into named tabs; exactly one tab panel is visible at a time.
    public const string TabGeneral = "General";
    public const string TabAppearance = "Appearance";
    public const string TabAudio = "Audio";
    public const string TabLibrary = "Library";
    public const string TabIntegrations = "Integrations";
    public const string TabAbout = "About";

    [ObservableProperty] private string _selectedSettingsTab = TabGeneral;

    public bool IsGeneralTabSelected => SelectedSettingsTab == TabGeneral;
    public bool IsAppearanceTabSelected => SelectedSettingsTab == TabAppearance;
    public bool IsAudioTabSelected => SelectedSettingsTab == TabAudio;
    public bool IsLibraryTabSelected => SelectedSettingsTab == TabLibrary;
    public bool IsIntegrationsTabSelected => SelectedSettingsTab == TabIntegrations;
    public bool IsAboutTabSelected => SelectedSettingsTab == TabAbout;

    public bool IsGeneralTabVisible => IsGeneralTabSelected;
    public bool IsAppearanceTabVisible => IsAppearanceTabSelected;
    public bool IsAudioTabVisible => IsAudioTabSelected;
    public bool IsLibraryTabVisible => IsLibraryTabSelected;
    public bool IsIntegrationsTabVisible => IsIntegrationsTabSelected;
    public bool IsAboutTabVisible => IsAboutTabSelected;

    partial void OnSelectedSettingsTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsGeneralTabSelected));
        OnPropertyChanged(nameof(IsAppearanceTabSelected));
        OnPropertyChanged(nameof(IsAudioTabSelected));
        OnPropertyChanged(nameof(IsLibraryTabSelected));
        OnPropertyChanged(nameof(IsIntegrationsTabSelected));
        OnPropertyChanged(nameof(IsAboutTabSelected));
        OnPropertyChanged(nameof(IsGeneralTabVisible));
        OnPropertyChanged(nameof(IsAppearanceTabVisible));
        OnPropertyChanged(nameof(IsAudioTabVisible));
        OnPropertyChanged(nameof(IsLibraryTabVisible));
        OnPropertyChanged(nameof(IsIntegrationsTabVisible));
        OnPropertyChanged(nameof(IsAboutTabVisible));
    }

    [RelayCommand]
    private void SelectSettingsTab(string tab) => SelectedSettingsTab = tab;

    // ── Profile ──
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _profileUsername = string.Empty;
    [ObservableProperty] private string _profileAvatarPath = string.Empty;

    partial void OnProfileNameChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnProfileUsernameChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnProfileAvatarPathChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }

    private AppSettings _settings;

    public void RequestMediaFoldersSection()
    {
        // The media-folders card lives on the Library tab; switch there so the
        // scroll anchor is actually in the visual tree before the view scrolls.
        SelectSettingsTab(TabLibrary);
        MediaFoldersScrollRequest++;
    }

    // ── Appearance ──
    // Five theme buttons (Gray is the default) plus System (auto-pick Gray vs Light from OS).
    // Exactly one of these is true at any time so the Settings UI can highlight the active card.

    [ObservableProperty] private bool _isGrayTheme = true;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private bool _isSystemTheme;
    [ObservableProperty] private bool _isMidnightTheme;

    /// <summary>User-created themes shown in the Themes row alongside the built-ins.</summary>
    public ObservableCollection<CustomThemeTile> CustomThemes { get; } = new();

    [ObservableProperty] private string? _activeCustomThemeId;

    // ── Accent colour ──

    /// <summary>Ten curated swatches; the active one is highlighted in the UI.</summary>
    public ObservableCollection<AccentSwatch> AccentSwatches { get; } = new();

    [ObservableProperty] private string _activeAccentHex = "#E74856";
    [ObservableProperty] private string _activeAccentName = "Crimson";
    [ObservableProperty] private string _customAccentHex = "#E74856";
    [ObservableProperty] private bool _isCustomAccentSelected;

    /// <summary>Drives the custom colour-picker flyout.</summary>
    [ObservableProperty] private Avalonia.Media.Color _pickerColor = Avalonia.Media.Color.Parse("#E74856");

    public event EventHandler<string>? AccentChanged;

    partial void OnPickerColorChanged(Avalonia.Media.Color value)
    {
        if (_suppressPickerSync) return;
        // Live-preview the colour as the user drags inside the custom picker.
        var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        if (!string.Equals(hex, ActiveAccentHex, StringComparison.OrdinalIgnoreCase))
            CustomAccentHex = hex;
    }

    partial void OnCustomAccentHexChanged(string value)
    {
        if (_suppressCustomHexHandler) return;
        if (!_settingsLoaded || _suspendSettingPersistence)
            return;

        var hex = NormalizeAccentHex(value);
        if (hex == null)
            return;

        try
        {
            var parsed = Avalonia.Media.Color.Parse(hex);
            if (!_suppressPickerSync && parsed != PickerColor)
            {
                _suppressPickerSync = true;
                try { PickerColor = parsed; }
                finally { _suppressPickerSync = false; }
            }

            ApplyAccent(hex, "Custom");
        }
        catch
        {
            // Ignore incomplete input while the user is still typing.
        }
    }

    // ── Audio Playback ──

    // Back-compat mirror only: no UI binds this; SongTransitions* are the source of truth.
    [ObservableProperty] private bool _crossfadeEnabled;
    [ObservableProperty] private double _crossfadeDuration = 6;

    // ── Song Transitions (Apple-style; drives the player's AutoMix engine) ──
    [ObservableProperty] private bool _songTransitionsEnabled;
    [ObservableProperty] private string _transitionStyle = "Crossfade";
    [ObservableProperty] private string _songTransitionStrength = "Balanced";
    [ObservableProperty] private bool _songTransitionBeatMatch = true;
    public bool IsCrossfadeStyle { get => string.Equals(TransitionStyle, "Crossfade", StringComparison.OrdinalIgnoreCase); set { if (value) TransitionStyle = "Crossfade"; } }
    public bool IsAutoMixStyle { get => string.Equals(TransitionStyle, "AutoMix", StringComparison.OrdinalIgnoreCase); set { if (value) TransitionStyle = "AutoMix"; } }

    [ObservableProperty] private bool _soundCheckEnabled;
    [ObservableProperty] private bool _trackTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _artistMarqueeEnabled = true;
    [ObservableProperty] private bool _coverFlowMarqueeEnabled = true;
    [ObservableProperty] private bool _coverFlowArtistMarqueeEnabled = true;
    [ObservableProperty] private bool _coverFlowAlbumMarqueeEnabled = true;
    [ObservableProperty] private bool _lyricsTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _lyricsArtistMarqueeEnabled = true;
    [ObservableProperty] private bool _miniPlayerTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _miniPlayerAlbumMarqueeEnabled = true;
    [ObservableProperty] private bool _enableAnimatedCovers = true;

    /// <summary>Waveform seekbar in the playback bar (requires ffmpeg).</summary>
    [ObservableProperty] private bool _waveformSeekbarEnabled;

    partial void OnWaveformSeekbarEnabledChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveAsync();
        _player?.RefreshWaveformMode();
    }

    /// <summary>Minimize hides the main window to the system tray.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    /// <summary>Close hides the main window to the system tray instead of exiting.</summary>
    [ObservableProperty] private bool _closeToTray;

    partial void OnMinimizeToTrayChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnCloseToTrayChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }

    // ── Songs page optional columns ──

    [ObservableProperty] private bool _showRatingColumn = true;
    [ObservableProperty] private bool _showBpmColumn;
    [ObservableProperty] private bool _showBitrateColumn;
    [ObservableProperty] private bool _showSampleRateColumn;

    partial void OnShowRatingColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowBpmColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowBitrateColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowSampleRateColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }

    // ── Web remote ──

    private WebRemoteServer? _webRemote;

    /// <summary>Local-network web remote (phone control page). Off by default.</summary>
    [ObservableProperty] private bool _webRemoteEnabled;

    /// <summary>Display URL for the running remote, or empty when off.</summary>
    [ObservableProperty] private string _webRemoteUrl = string.Empty;

    partial void OnWebRemoteEnabledChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveAsync();
        UpdateWebRemoteState();
    }

    private void UpdateWebRemoteState()
    {
        if (WebRemoteEnabled && _player != null)
        {
            try
            {
                _webRemote ??= new WebRemoteServer(_player);
                if (!_webRemote.IsRunning)
                    _webRemote.Start(_settings.WebRemotePort);
                var ip = WebRemoteServer.GetLocalAddress() ?? "<this-pc-ip>";
                WebRemoteUrl = $"http://{ip}:{_settings.WebRemotePort}";
            }
            catch (Exception ex)
            {
                WebRemoteUrl = $"Failed to start: {ex.Message}";
                DebugLogger.Error(DebugLogger.Category.Error, "WebRemote.StartFailed", ex.Message);
            }
        }
        else
        {
            _webRemote?.Stop();
            WebRemoteUrl = string.Empty;
        }
    }
    [ObservableProperty] private double _playbackBarBackgroundOpacity = 0.4;
    [ObservableProperty] private bool _sidebarHoverExpand = true;
    [ObservableProperty] private bool _collapseAlbumEditions;

    // ── Lyrics Providers ──

    [ObservableProperty] private bool _lrcLibEnabled = true;
    [ObservableProperty] private bool _netEaseEnabled = true;

    [ObservableProperty] private string _ffmpegPath = string.Empty;
    [ObservableProperty] private string _ffmpegStatus = string.Empty;

    public string[] ReplayGainModeOptions { get; } = { "Off", "Track", "Album", "Auto" };
    [ObservableProperty] private string _replayGainMode = "Off";
    [ObservableProperty] private double _replayGainPreampDb;
    /// <summary>On/off mirror of <see cref="ReplayGainMode"/> for the Settings toggle.</summary>
    [ObservableProperty] private bool _replayGainEnabled;
    private string _lastActiveReplayGainMode = "Auto";
    private bool _suppressRgNotify;

    [ObservableProperty] private bool _gaplessPlaybackEnabled = true;

    // ── Audio analysis (background BPM/key detection) ──

    [ObservableProperty] private bool _bpmKeyAnalysisEnabled = true;
    [ObservableProperty] private bool _writeAnalysisToTags;

    // ── Exclusive mode (Windows WASAPI) ──

    public bool IsExclusiveAudioSupported => OperatingSystem.IsWindows();
    [ObservableProperty] private bool _exclusiveAudioEnabled;
    /// <summary>Live output-path status from the player ("Exclusive output active — 44.1 kHz / 24-bit",
    /// "Exclusive mode unavailable (device in use) — using shared output", ...).</summary>
    [ObservableProperty] private string _exclusiveAudioStatus = "";

    // ── Equalizer ──

    [ObservableProperty] private bool _equalizerEnabled = true;
    [ObservableProperty] private int _selectedEqPresetIndex = 1; // 0 = Custom, 1 = Flat, 2+ = VLC preset
    [ObservableProperty] private string _selectedEqPresetName = "Flat";

    /// <summary>Preset names shown in the dropdown. The list stays stable so the open popup does not re-layout.</summary>
    public ObservableCollection<string> VisibleEqPresets { get; } = CreateDefaultVisiblePresets();
    private static ObservableCollection<string> CreateDefaultVisiblePresets()
    {
        var list = new ObservableCollection<string>();
        for (int i = 0; i < EqPresetNames.Length; i++)
            list.Add(EqPresetNames[i]);
        return list;
    }
    /// <summary>Editable parametric EQ bands (frequency / gain / Q).</summary>
    public ObservableCollection<EqBandViewModel> EqBands { get; } = new();

    public bool CanAddEqBand => EqBands.Count < ParametricEqMath.MaxBands;
    public bool CanRemoveEqBand => EqBands.Count > ParametricEqMath.MinBands;

    private bool _suppressEqNotify;
    private const int EqSaveDebounceMs = 280;

    /// <summary>EQ preset names. Index 0 = Custom, 1-18 = VLC built-in presets.</summary>
    public static readonly string[] EqPresetNames =
    {
        "Custom", "Flat", "Classical", "Club", "Dance",
        "Full Bass", "Full Bass + Treble", "Full Treble",
        "Headphones", "Large Hall", "Live", "Party",
        "Pop", "Reggae", "Rock", "Ska",
        "Soft", "Soft Rock", "Techno"
    };

    // ── Accounts / Integrations ──

    [ObservableProperty] private bool _discordRichPresenceEnabled;
    [ObservableProperty] private bool _lastFmScrobblingEnabled;
    [ObservableProperty] private string _lastFmUsername = "";
    [ObservableProperty] private bool _isLastFmConnected;
    [ObservableProperty] private string _lastFmStatusText = "Not connected";

    // ── ListenBrainz ──
    [ObservableProperty] private bool _listenBrainzScrobblingEnabled = true;
    [ObservableProperty] private string _listenBrainzToken = "";
    [ObservableProperty] private string _listenBrainzUsername = "";
    [ObservableProperty] private bool _isListenBrainzConnected;
    [ObservableProperty] private string _listenBrainzStatusText = "Not connected";
    [ObservableProperty] private string _listenBrainzError = "";

    // ── Preferences ──

    [ObservableProperty] private bool _scanOnStartup = true;

    [ObservableProperty] private bool _watchFoldersEnabled = true;

    [ObservableProperty] private string _organizePattern = "{AlbumArtist}/{Album}/{TrackNo} {Title}";
    [ObservableProperty] private string _organizeTargetRoot = string.Empty;
    [ObservableProperty] private string _acoustIdApiKey = string.Empty;
    [ObservableProperty] private string _fpcalcPath = string.Empty;

    // ── Library overview stats ──

    [ObservableProperty] private int _totalSongs;
    [ObservableProperty] private int _totalArtists;
    [ObservableProperty] private int _totalAlbums;
    [ObservableProperty] private int _totalPlaylists;

    [ObservableProperty] private string _totalFileSize = "0 MB";
    [ObservableProperty] private string _totalListeningTime = "0 min";

    // ── Audio quality ──

    [ObservableProperty] private int _losslessCount;
    [ObservableProperty] private int _lossyCount;
    [ObservableProperty] private int _hiResCount;
    [ObservableProperty] private double _losslessPercentage;
    [ObservableProperty] private string _losslessPercentageText = "0%";
    [ObservableProperty] private string _lossyPercentageText = "0%";
    [ObservableProperty] private string _hiResPercentageText = "0%";

    // ── Storage ──

    [ObservableProperty] private string _storageLibraryData = "0 B";
    [ObservableProperty] private string _storageArtwork = "0 B";
    [ObservableProperty] private string _storagePlaylists = "0 B";
    [ObservableProperty] private string _storageSettings = "0 B";
    [ObservableProperty] private string _storageTotal = "0 B";

    // ── Files / Scan ──

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _scanProgress;
    [ObservableProperty] private string _scanStatusText = "";
    [ObservableProperty] private bool _isResetConfirmVisible;

    /// <summary>Configured music folder paths.</summary>
    public ObservableCollection<string> MusicFolders { get; } = new();

    /// <summary>Persistent include/exclude scan rules.</summary>
    public ObservableCollection<FolderRule> FolderRules { get; } = new();

    /// <summary>Formatted display of the current media folder path.</summary>
    public string MediaFolderDisplay => MusicFolders.Count > 0
        ? string.Join(", ", MusicFolders.Select(FormatFolderDisplay))
        : "No folder selected";

    private static string FormatFolderDisplay(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return folderPath;

        // Trim trailing separators so "C:\\Music\\" doesn't become "C: >".
        var normalized = folderPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized))
            return folderPath;

        var parts = normalized.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2)
            return $"{parts[0]} > {parts[^1]}";

        return normalized;
    }

    // ── About ──

    public string AppVersion => UpdateService.CurrentVersionDisplay;

    /// <summary>True when the installed build is a pre-release — drives the
    /// "Pre-release" badge next to the version in the About section.</summary>
    public bool IsPrereleaseBuild => UpdateService.IsPrereleaseBuild;

    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCheckForUpdatesButton))]
    private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCheckForUpdatesButton))]
    private bool _isReadyToInstall;
    [ObservableProperty] private string _latestVersionTag = "";
    [ObservableProperty] private bool _isLatestPrerelease;
    [ObservableProperty] private bool _includePrereleaseUpdates;

    public bool ShowCheckForUpdatesButton => !IsUpdateAvailable && !IsReadyToInstall;

    // ── Events ──

    /// <summary>Fires when the theme changes so the App can update. Payload is the theme key.</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>Fires after a full settings reset so the shell can reload playlists, etc.</summary>
    public event EventHandler? SettingsReset;

    /// <summary>Fires when a media folder is added or removed so the Folders view can rebuild its tree.</summary>
    public event EventHandler? MusicFoldersChanged;

    public SettingsViewModel(IPersistenceService persistence, ILibraryService library)
    {
        _persistence = persistence;
        _library = library;
        _settings = new AppSettings();

        _library.ScanProgress += (_, count) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ScanProgress = count;
                ScanStatusText = "Scanning Library";
            });
        };

        _library.LibraryUpdated += (_, _) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var settings = await _persistence.LoadSettingsAsync();
                    var currentFolders = new HashSet<string>(settings.MusicFolders, StringComparer.OrdinalIgnoreCase);
                    var displayedFolders = new HashSet<string>(MusicFolders, StringComparer.OrdinalIgnoreCase);
                    if (!currentFolders.SetEquals(displayedFolders))
                    {
                        MusicFolders.Clear();
                        foreach (var folder in settings.MusicFolders)
                            MusicFolders.Add(folder);
                        _settings.MusicFolders = settings.MusicFolders;
                        OnPropertyChanged(nameof(MediaFolderDisplay));
                    }
                }
                catch { }
            });
        };

        if (Avalonia.Application.Current is Noctis.App app)
        {
            app.CustomThemeResolver = id =>
            {
                var t = CustomThemes.FirstOrDefault(x => x.Id == id);
                if (t == null) return null;
                return new CustomThemeDefinition
                {
                    Id = t.Id,
                    Name = t.Name,
                    BaseMode = t.BaseMode,
                    MainBackgroundHex = t.MainHex,
                    SidebarBackgroundHex = t.SidebarHex,
                    AccentHex = t.AccentHex,
                };
            };
        }
    }

    /// <summary>Sets the audio player reference for applying audio settings.</summary>
    public void SetAudioPlayer(IAudioPlayer audioPlayer)
    {
        _audioPlayer = audioPlayer;
        audioPlayer.OutputModeChanged += (_, status) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ExclusiveAudioStatus = status);
        ApplyAudioSettings();
    }

    /// <summary>Sets the player reference for applying playback UI settings.</summary>
    public void SetPlayer(PlayerViewModel player)
    {
        _player = player;
        ApplyPlayerSettings();
    }

    /// <summary>Sets the Discord presence service reference.</summary>
    public void SetDiscordPresence(IDiscordPresenceService discord) => _discord = discord;

    /// <summary>Sets the loon client reference for Discord cover art.</summary>
    public void SetLoonClient(LoonClient loon) => _loon = loon;

    /// <summary>Sets the Last.fm service reference.</summary>
    public void SetLastFm(ILastFmService lastFm) => _lastFm = lastFm;
    public void SetListenBrainz(IListenBrainzService listenBrainz) => _listenBrainz = listenBrainz;

    public void SetArtistImageService(ArtistImageService svc) => _artistImageService = svc;

    public void SetUpdateService(UpdateService updateService) => _updateService = updateService;

    /// <summary>Gets the navigation key for the default page.</summary>
    public string GetDefaultPageKey() => "home";

    /// <summary>Loads settings from disk and populates the view.</summary>
    public async Task LoadAsync()
    {
        if (_settingsLoaded)
            return;

        _suspendSettingPersistence = true;
        try
        {
            _settings = await _persistence.LoadSettingsAsync();

            // Theme — with one-shot migration from the v1 schema where "Dark" denoted today's Gray.
            // Also collapse any prior "MidnightBlack" choice into "Dark" since the two themes
            // are now visually identical.
            var storedTheme = _settings.Theme;
            if (storedTheme == "Dark" && !_settings.ThemeV2Migrated)
            {
                storedTheme = "Gray";
                _settings.Theme = "Gray";
            }
            else if (storedTheme == "MidnightBlack")
            {
                storedTheme = "Dark";
                _settings.Theme = "Dark";
            }
            _settings.ThemeV2Migrated = true;
            SetActiveThemeFlags(storedTheme);

            // Hydrate user-created themes.
            CustomThemes.Clear();
            foreach (var def in _settings.CustomThemes)
                CustomThemes.Add(MapDefToTile(def));

            // If active theme is Custom:<id>, mark the matching tile and clear built-in flags.
            if (storedTheme.StartsWith("Custom:", StringComparison.Ordinal))
            {
                var id = storedTheme.Substring("Custom:".Length);
                ActiveCustomThemeId = id;
                foreach (var t in CustomThemes) t.IsActive = t.Id == id;
                if (!CustomThemes.Any(t => t.Id == id))
                {
                    // Stale reference — fall back to Gray and persist.
                    ActiveCustomThemeId = null;
                    SetActiveThemeFlags("Gray");
                    _settings.Theme = "Gray";
                }
                else
                {
                    SetActiveThemeFlags("__Custom"); // clears all five built-in flags
                }
            }

            // Profile
            ProfileName = _settings.ProfileName ?? string.Empty;
            ProfileUsername = _settings.ProfileUsername ?? string.Empty;
            ProfileAvatarPath = _settings.ProfileAvatarPath ?? string.Empty;

            // Accent colour
            ActiveAccentHex = string.IsNullOrWhiteSpace(_settings.AccentColorHex) ? "#E74856" : _settings.AccentColorHex;
            ActiveAccentName = string.IsNullOrWhiteSpace(_settings.AccentPresetName) ? "Crimson" : _settings.AccentPresetName;
            CustomAccentHex = ActiveAccentHex;
            try
            {
                _suppressPickerSync = true;
                PickerColor = Avalonia.Media.Color.Parse(ActiveAccentHex);
            }
            catch { }
            finally { _suppressPickerSync = false; }
            RebuildAccentSwatches();

            ScanOnStartup = _settings.ScanOnStartup;
            WatchFoldersEnabled = _settings.WatchFoldersEnabled;
            OrganizePattern = _settings.OrganizePattern;
            OrganizeTargetRoot = _settings.OrganizeTargetRoot;
            AcoustIdApiKey = _settings.AcoustIdApiKey;
            FpcalcPath = _settings.FpcalcPath;
            IncludePrereleaseUpdates = _settings.IncludePrereleaseUpdates;

            // Playback
            MigrateTransitionSettings(_settings);
            CrossfadeEnabled = _settings.CrossfadeEnabled;
            CrossfadeDuration = Math.Clamp(_settings.CrossfadeDuration, 1, 12);
            SongTransitionsEnabled = _settings.SongTransitionsEnabled;
            TransitionStyle = string.IsNullOrWhiteSpace(_settings.TransitionStyle) ? "Crossfade" : _settings.TransitionStyle;
            SongTransitionStrength = string.IsNullOrWhiteSpace(_settings.SongTransitionStrength) ? "Balanced" : _settings.SongTransitionStrength;
            SongTransitionBeatMatch = _settings.SongTransitionBeatMatch;
            OnPropertyChanged(nameof(IsCrossfadeStyle));
            OnPropertyChanged(nameof(IsAutoMixStyle));
            SoundCheckEnabled = _settings.SoundCheckEnabled;
            TrackTitleMarqueeEnabled = _settings.TrackTitleMarqueeEnabled;
            ArtistMarqueeEnabled = _settings.ArtistMarqueeEnabled;
            CoverFlowMarqueeEnabled = _settings.CoverFlowMarqueeEnabled;
            CoverFlowArtistMarqueeEnabled = _settings.CoverFlowArtistMarqueeEnabled;
            CoverFlowAlbumMarqueeEnabled = _settings.CoverFlowAlbumMarqueeEnabled;
            LyricsTitleMarqueeEnabled = _settings.LyricsTitleMarqueeEnabled;
            LyricsArtistMarqueeEnabled = _settings.LyricsArtistMarqueeEnabled;
            MiniPlayerTitleMarqueeEnabled = _settings.MiniPlayerTitleMarqueeEnabled;
            MiniPlayerAlbumMarqueeEnabled = _settings.MiniPlayerAlbumMarqueeEnabled;
            EnableAnimatedCovers = _settings.EnableAnimatedCovers;
            WaveformSeekbarEnabled = _settings.WaveformSeekbarEnabled;
            MinimizeToTray = _settings.MinimizeToTray;
            CloseToTray = _settings.CloseToTray;
            WebRemoteEnabled = _settings.WebRemoteEnabled;
            ShowRatingColumn = _settings.ShowRatingColumn;
            ShowBpmColumn = _settings.ShowBpmColumn;
            ShowBitrateColumn = _settings.ShowBitrateColumn;
            ShowSampleRateColumn = _settings.ShowSampleRateColumn;
            PlaybackBarBackgroundOpacity = Math.Clamp(_settings.PlaybackBarBackgroundOpacity, 0, 1);
            SidebarHoverExpand = _settings.SidebarHoverExpand;
            CollapseAlbumEditions = _settings.CollapseAlbumEditions;

            // Lyrics providers
            LrcLibEnabled = _settings.LrcLibEnabled;
            FfmpegPath = _settings.FfmpegPath;
            RefreshFfmpegStatus();
            ReplayGainMode = string.IsNullOrEmpty(_settings.ReplayGainMode) ? "Off" : _settings.ReplayGainMode;
            ReplayGainPreampDb = _settings.ReplayGainPreampDb;
            ReplayGainEnabled = !string.Equals(ReplayGainMode, "Off", StringComparison.OrdinalIgnoreCase);
            GaplessPlaybackEnabled = _settings.GaplessPlaybackEnabled;
            BpmKeyAnalysisEnabled = _settings.BpmKeyAnalysisEnabled;
            WriteAnalysisToTags = _settings.WriteAnalysisToTags;
            ExclusiveAudioEnabled = _settings.ExclusiveAudioEnabled && IsExclusiveAudioSupported;
            NetEaseEnabled = _settings.NetEaseEnabled;

            // Equalizer
            _suppressEqNotify = true;
            EqualizerEnabled = _settings.EqualizerEnabled;
            int loadedIdx = Math.Clamp(_settings.EqualizerPresetIndex + 1, 0, EqPresetNames.Length - 1);
            SelectedEqPresetIndex = loadedIdx;
            SelectedEqPresetName = EqPresetNames[loadedIdx];
            // Parametric bands are the source of truth; settings files written
            // before the parametric EQ migrate from the legacy 10-band gains.
            var loadedBands = _settings.ParametricEqBands is { Count: > 0 } pb
                ? pb
                : ParametricEqMath.FromGraphicBands(_settings.EqualizerBands);
            SetEqBands(loadedBands);
            _suppressEqNotify = false;

            // Music folders
            MusicFolders.Clear();
            foreach (var folder in _settings.MusicFolders)
                MusicFolders.Add(folder);
            FolderRules.Clear();
            foreach (var rule in _settings.FolderRules)
                FolderRules.Add(new FolderRule
                {
                    Path = rule.Path,
                    Include = rule.Include,
                    Enabled = rule.Enabled
                });
            OnPropertyChanged(nameof(MediaFolderDisplay));

            // Stats/storage are refreshed on navigation to Settings; library is not
            // loaded yet at this point, so calling them here would just report zeros.

            // Integrations
            DiscordRichPresenceEnabled = _settings.DiscordRichPresenceEnabled;
            LastFmScrobblingEnabled = _settings.LastFmScrobblingEnabled;
            LastFmUsername = _settings.LastFmUsername;

            if (_lastFm != null && !string.IsNullOrEmpty(_settings.LastFmSessionKey))
            {
                _lastFm.Configure(_settings.LastFmSessionKey);
                IsLastFmConnected = true;
                LastFmStatusText = $"Connected as {_settings.LastFmUsername}";
            }

            // ListenBrainz
            ListenBrainzScrobblingEnabled = _settings.ListenBrainzScrobblingEnabled;
            ListenBrainzToken = _settings.ListenBrainzToken;
            ListenBrainzUsername = _settings.ListenBrainzUsername;
            if (_listenBrainz != null && !string.IsNullOrEmpty(_settings.ListenBrainzToken))
            {
                _listenBrainz.Configure(_settings.ListenBrainzToken);
                IsListenBrainzConnected = !string.IsNullOrEmpty(_settings.ListenBrainzUsername);
                LastFmStatusText = LastFmStatusText; // no-op, kept for symmetry
                if (IsListenBrainzConnected)
                    ListenBrainzStatusText = $"Connected as {_settings.ListenBrainzUsername}";
            }

            if (_discord != null && DiscordRichPresenceEnabled)
            {
                _ = _discord.ConnectAsync();
            }

            // Ensure player gets the persisted startup settings even if no toggle changed.
            ApplyAudioSettings();

            // Apply the persisted theme on startup
            ThemeChanged?.Invoke(this, ResolveActiveThemeKey());

            // Apply the persisted accent colour on startup
            AccentChanged?.Invoke(this, ActiveAccentHex);

            _settingsLoaded = true;
        }
        finally
        {
            _suspendSettingPersistence = false;
        }
    }

    /// <summary>Saves current settings to disk.</summary>
    public async Task SaveAsync()
    {
        if (!_settingsLoaded || _suspendSettingPersistence)
            return;

        await _saveLock.WaitAsync();
        try
        {
            SyncToSettings();
            await _persistence.SaveSettingsAsync(_settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsViewModel] Failed to save settings: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void SyncToSettings()
    {
        if (IsGrayTheme) _settings.Theme = "Gray";
        else if (IsDarkTheme) _settings.Theme = "Dark";
        else if (IsLightTheme) _settings.Theme = "Light";
        else if (IsMidnightTheme) _settings.Theme = "Midnight";
        else _settings.Theme = "System";

        if (!string.IsNullOrEmpty(ActiveCustomThemeId)) _settings.Theme = "Custom:" + ActiveCustomThemeId;

        _settings.CustomThemes = CustomThemes.Select(t => new CustomThemeDefinition
        {
            Id = t.Id,
            Name = t.Name,
            BaseMode = t.BaseMode,
            MainBackgroundHex = t.MainHex,
            SidebarBackgroundHex = t.SidebarHex,
            AccentHex = t.AccentHex,
        }).ToList();

        _settings.ThemeV2Migrated = true;
        _settings.ProfileName = ProfileName ?? string.Empty;
        _settings.ProfileUsername = ProfileUsername ?? string.Empty;
        _settings.ProfileAvatarPath = ProfileAvatarPath ?? string.Empty;
        _settings.AccentColorHex = ActiveAccentHex;
        _settings.AccentPresetName = ActiveAccentName;

        _settings.ScanOnStartup = ScanOnStartup;
        _settings.WatchFoldersEnabled = WatchFoldersEnabled;
        _settings.OrganizePattern = OrganizePattern;
        _settings.OrganizeTargetRoot = OrganizeTargetRoot;
        _settings.AcoustIdApiKey = AcoustIdApiKey;
        _settings.FpcalcPath = FpcalcPath;
        _settings.MusicFolders = MusicFolders.ToList();
        _settings.FolderRules = FolderRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new FolderRule
            {
                Path = r.Path.Trim(),
                Include = r.Include,
                Enabled = r.Enabled
            })
            .ToList();
        _settings.SongTransitionsEnabled = SongTransitionsEnabled;
        _settings.TransitionStyle = TransitionStyle ?? "Crossfade";
        _settings.SongTransitionStrength = SongTransitionStrength ?? "Balanced";
        _settings.SongTransitionBeatMatch = SongTransitionBeatMatch;
        // Back-compat mirror so older builds still crossfade when appropriate.
        _settings.CrossfadeEnabled = SongTransitionsEnabled && IsCrossfadeStyle;
        _settings.CrossfadeDuration = Math.Clamp(CrossfadeDuration, 1, 12);
        _settings.SoundCheckEnabled = SoundCheckEnabled;
        _settings.TrackTitleMarqueeEnabled = TrackTitleMarqueeEnabled;
        _settings.ArtistMarqueeEnabled = ArtistMarqueeEnabled;
        _settings.CoverFlowMarqueeEnabled = CoverFlowMarqueeEnabled;
        _settings.CoverFlowArtistMarqueeEnabled = CoverFlowArtistMarqueeEnabled;
        _settings.CoverFlowAlbumMarqueeEnabled = CoverFlowAlbumMarqueeEnabled;
        _settings.LyricsTitleMarqueeEnabled = LyricsTitleMarqueeEnabled;
        _settings.LyricsArtistMarqueeEnabled = LyricsArtistMarqueeEnabled;
        _settings.MiniPlayerTitleMarqueeEnabled = MiniPlayerTitleMarqueeEnabled;
        _settings.MiniPlayerAlbumMarqueeEnabled = MiniPlayerAlbumMarqueeEnabled;
        _settings.EnableAnimatedCovers = EnableAnimatedCovers;
        _settings.WaveformSeekbarEnabled = WaveformSeekbarEnabled;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.CloseToTray = CloseToTray;
        _settings.WebRemoteEnabled = WebRemoteEnabled;
        _settings.ShowRatingColumn = ShowRatingColumn;
        _settings.ShowBpmColumn = ShowBpmColumn;
        _settings.ShowBitrateColumn = ShowBitrateColumn;
        _settings.ShowSampleRateColumn = ShowSampleRateColumn;
        _settings.PlaybackBarBackgroundOpacity = Math.Clamp(PlaybackBarBackgroundOpacity, 0, 1);
        _settings.SidebarHoverExpand = SidebarHoverExpand;
        _settings.CollapseAlbumEditions = CollapseAlbumEditions;
        _settings.LrcLibEnabled = LrcLibEnabled;
        _settings.FfmpegPath = FfmpegPath ?? string.Empty;
        _settings.ReplayGainMode = ReplayGainMode ?? "Off";
        _settings.ReplayGainPreampDb = ReplayGainPreampDb;
        _settings.GaplessPlaybackEnabled = GaplessPlaybackEnabled;
        _settings.BpmKeyAnalysisEnabled = BpmKeyAnalysisEnabled;
        _settings.WriteAnalysisToTags = WriteAnalysisToTags;
        _settings.ExclusiveAudioEnabled = ExclusiveAudioEnabled;
        _settings.NetEaseEnabled = NetEaseEnabled;
        _settings.EqualizerEnabled = EqualizerEnabled;
        _settings.EqualizerPresetIndex = SelectedEqPresetIndex - 1;
        _settings.ParametricEqBands = EqBands
            .Select(b => new ParametricEqBand { FrequencyHz = b.FrequencyHz, GainDb = b.GainDb, Q = b.Q })
            .ToList();
        // Downgrade-safe mirror of the applied 10-band curve.
        _settings.EqualizerBands = GetGraphicEqBands().bands;
        _settings.DiscordRichPresenceEnabled = DiscordRichPresenceEnabled;
        _settings.LastFmScrobblingEnabled = LastFmScrobblingEnabled;
        _settings.LastFmUsername = LastFmUsername;
        if (_lastFm is LastFmService lfm)
            _settings.LastFmSessionKey = lfm.GetSessionKey() ?? "";

        _settings.ListenBrainzScrobblingEnabled = ListenBrainzScrobblingEnabled;
        _settings.ListenBrainzToken = ListenBrainzToken ?? string.Empty;
        _settings.ListenBrainzUsername = ListenBrainzUsername ?? string.Empty;
    }

    /// <summary>Returns the loaded settings object.</summary>
    public AppSettings GetSettings() => _settings;

    /// <summary>Updates the volume setting in the internal settings object.</summary>
    public void SetVolume(int volume) => _settings.Volume = volume;

    private void ApplyAudioSettings()
    {
        _audioPlayer?.SetNormalization(SoundCheckEnabled);
        _audioPlayer?.SetCrossfade(SongTransitionsEnabled && IsCrossfadeStyle, (int)Math.Round(CrossfadeDuration));
        ApplyAutoMixToPlayer();
        _audioPlayer?.SetGapless(GaplessPlaybackEnabled);
        _audioPlayer?.SetExclusiveMode(ExclusiveAudioEnabled);
        _audioPlayer?.ApplyReplayGain(ReplayGainMode ?? "Off", ReplayGainPreampDb);
        ApplyEqualizer();
        _player?.RefreshSignalPath();
    }

    /// <summary>Pushes the AutoMix transition mode/strength/beat-match settings onto the player.</summary>
    private void ApplyAutoMixToPlayer()
    {
        if (_player == null) return;
        _player.AutoMixTransitionMode = MapTransitionMode(SongTransitionsEnabled, TransitionStyle);
        _player.AutoMixStrength = SongTransitionStrength switch
        {
            "Subtle" => Models.AutoMixStrength.Subtle,
            "Extended" => Models.AutoMixStrength.Extended,
            _ => Models.AutoMixStrength.Balanced,
        };
        _player.AutoMixBeatMatch = SongTransitionBeatMatch;
        _player.AutoMixAvoidAlbums = true; // albums in order stay gapless
    }

    private void ApplyPlayerSettings()
    {
        if (_player == null) return;
        // The Song Transitions toggle + style drive the player's transition machinery;
        // gapless covers natural track changes when transitions are off.
        ApplyAutoMixToPlayer();
        _player.GaplessEnabled = GaplessPlaybackEnabled;
        _player.TrackTitleMarqueeEnabled = TrackTitleMarqueeEnabled;
        _player.ArtistMarqueeEnabled = ArtistMarqueeEnabled;
        _player.IslandBackgroundOpacity = Math.Clamp(PlaybackBarBackgroundOpacity, 0, 1);
        Controls.MarqueeTextBlock.GlobalCoverFlowScrollEnabled = CoverFlowMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalCoverFlowArtistScrollEnabled = CoverFlowArtistMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalCoverFlowAlbumScrollEnabled = CoverFlowAlbumMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalLyricsTitleScrollEnabled = LyricsTitleMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalLyricsArtistScrollEnabled = LyricsArtistMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = MiniPlayerTitleMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalMiniPlayerAlbumScrollEnabled = MiniPlayerAlbumMarqueeEnabled;
    }

    private void ApplyEqualizer()
    {
        var (bands, preamp) = GetGraphicEqBands();
        _audioPlayer?.SetAdvancedEqualizer(EqualizerEnabled, bands, preamp);
        _player?.RefreshSignalPath();
    }

    /// <summary>
    /// The 10-band graphic curve + preamp currently in effect. Named presets keep
    /// LibVLC's exact preset bands and preamp; Custom maps the parametric bands
    /// onto the graphic frequencies via <see cref="ParametricEqMath"/>.
    /// </summary>
    private (float[] bands, float preamp) GetGraphicEqBands()
    {
        if (SelectedEqPresetIndex > 0 &&
            TryGetVlcPresetCurve(SelectedEqPresetIndex - 1, out var presetBands, out var presetPreamp))
            return (presetBands, presetPreamp);

        return (ParametricEqMath.MapToGraphicBands(
            EqBands.Select(b => new ParametricEqBand { FrequencyHz = b.FrequencyHz, GainDb = b.GainDb, Q = b.Q })), 0f);
    }

    /// <summary>Maps the Song Transitions master toggle + style to the player's transition mode.</summary>
    public static Models.AutoMixTransitionMode MapTransitionMode(bool enabled, string style)
    {
        if (!enabled) return Models.AutoMixTransitionMode.Off;
        return string.Equals(style, "AutoMix", StringComparison.OrdinalIgnoreCase)
            ? Models.AutoMixTransitionMode.AutoMix
            : Models.AutoMixTransitionMode.Crossfade;
    }

    /// <summary>One-time migration: legacy CrossfadeEnabled becomes SongTransitions + Crossfade style.</summary>
    public static void MigrateTransitionSettings(Models.AppSettings s)
    {
        if (s.CrossfadeEnabled && !s.SongTransitionsEnabled)
        {
            s.SongTransitionsEnabled = true;
            s.TransitionStyle = "Crossfade";
        }
    }

    private static bool TryGetVlcPresetCurve(int vlcPresetIndex, out float[] bands, out float preamp)
    {
        bands = new float[10];
        preamp = 0f;
        try
        {
            using var tempEq = new LibVLCSharp.Shared.Equalizer((uint)vlcPresetIndex);
            for (uint i = 0; i < 10; i++)
                bands[i] = Math.Clamp(tempEq.Amp(i), -12f, 12f);
            preamp = Math.Clamp(tempEq.Preamp, -20f, 20f);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies an EQ preset by name for per-track overrides.
    /// Pass empty/null to restore the global EQ setting.
    /// </summary>
    public void ApplyEqPresetByName(string? presetName)
    {
        if (string.IsNullOrEmpty(presetName))
        {
            ApplyEqualizer();
            return;
        }

        var index = Array.IndexOf(EqPresetNames, presetName);
        // index 0 = "Custom", 1 = "Flat" = VLC preset 0
        if (index <= 0 || !TryGetVlcPresetCurve(index - 1, out var bands, out var preamp))
        {
            ApplyEqualizer();
            return;
        }

        _audioPlayer?.SetAdvancedEqualizer(true, bands, preamp);
    }

    private void QueueEqualizerSave()
    {
        _eqSaveDebounceCts?.Cancel();
        _eqSaveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _eqSaveDebounceCts = cts;
        _ = SaveEqualizerDebouncedAsync(cts.Token);
    }

    private async Task SaveEqualizerDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(EqSaveDebounceMs, token);
            if (token.IsCancellationRequested) return;
            await SaveAsync();
        }
        catch (OperationCanceledException)
        {
            // Newer EQ edits superseded this pending save.
        }
    }

    /// <summary>Replace the editable band list (count clamped to 5–10), without firing per-band edits.</summary>
    private void SetEqBands(IEnumerable<ParametricEqBand> bands)
    {
        var wasSuppressed = _suppressEqNotify;
        _suppressEqNotify = true;
        EqBands.Clear();
        foreach (var b in bands.Take(ParametricEqMath.MaxBands))
            EqBands.Add(new EqBandViewModel(b.FrequencyHz, b.GainDb, b.Q, OnEqBandEdited));
        while (EqBands.Count < ParametricEqMath.MinBands)
        {
            EqBands.Add(new EqBandViewModel(
                ParametricEqMath.GraphicBandFrequencies[EqBands.Count], 0, ParametricEqMath.DefaultQ, OnEqBandEdited));
        }
        _suppressEqNotify = wasSuppressed;
        OnPropertyChanged(nameof(CanAddEqBand));
        OnPropertyChanged(nameof(CanRemoveEqBand));
    }

    // ── Theme commands ──

    [RelayCommand] private void SetGrayTheme() => ApplyTheme("Gray");
    [RelayCommand] private void SetDarkTheme() => ApplyTheme("Dark");
    [RelayCommand] private void SetLightTheme() => ApplyTheme("Light");
    [RelayCommand] private void SetSystemTheme() => ApplyTheme("System");
    [RelayCommand] private void SetMidnightTheme() => ApplyTheme("Midnight");

    [RelayCommand]
    private void ApplyCustomTheme(string id)
    {
        var tile = CustomThemes.FirstOrDefault(t => t.Id == id);
        if (tile == null) return;

        foreach (var t in CustomThemes) t.IsActive = t.Id == id;
        ActiveCustomThemeId = id;
        SetActiveThemeFlags("__Custom");

        ApplyAccent(tile.AccentHex, "Custom");
        ThemeChanged?.Invoke(this, ResolveActiveThemeKey());

        if (_settingsLoaded) _ = SaveAsync();
    }

    [RelayCommand]
    private void DeleteCustomTheme(string id)
    {
        var tile = CustomThemes.FirstOrDefault(t => t.Id == id);
        if (tile == null) return;
        CustomThemes.Remove(tile);

        if (ActiveCustomThemeId == id)
        {
            ActiveCustomThemeId = null;
            SetActiveThemeFlags("Gray");
            ApplyAccent("#E74856", "Crimson");
            ThemeChanged?.Invoke(this, ResolveActiveThemeKey());
        }

        if (_settingsLoaded) _ = SaveAsync();
    }

    [RelayCommand]
    private async Task OpenThemeEditorAsync(string? existingId)
    {
        var existingTile = string.IsNullOrEmpty(existingId)
            ? null
            : CustomThemes.FirstOrDefault(t => t.Id == existingId);

        CustomThemeDefinition? existingDef = null;
        if (existingTile != null)
        {
            existingDef = new CustomThemeDefinition
            {
                Id = existingTile.Id,
                Name = existingTile.Name,
                BaseMode = existingTile.BaseMode,
                MainBackgroundHex = existingTile.MainHex,
                SidebarBackgroundHex = existingTile.SidebarHex,
                AccentHex = existingTile.AccentHex,
            };
        }

        var nameBlocklist = CustomThemes
            .Where(t => existingTile == null || t.Id != existingTile.Id)
            .Select(t => t.Name);

        var vm = new ThemeEditorViewModel(existingDef, nameBlocklist);
        var dialog = new Views.ThemeEditorDialog(vm);

        var owner = (Avalonia.Application.Current?.ApplicationLifetime
                      as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner != null) await dialog.ShowDialog(owner);
        else dialog.Show();

        if (dialog.Result == null) return;

        var result = dialog.Result;
        if (existingTile != null)
        {
            existingTile.Name = result.Name;
            existingTile.BaseMode = result.BaseMode;
            existingTile.MainHex = result.MainBackgroundHex;
            existingTile.SidebarHex = result.SidebarBackgroundHex;
            existingTile.AccentHex = result.AccentHex;
        }
        else
        {
            CustomThemes.Add(new CustomThemeTile
            {
                Id = result.Id,
                Name = result.Name,
                BaseMode = result.BaseMode,
                MainHex = result.MainBackgroundHex,
                SidebarHex = result.SidebarBackgroundHex,
                AccentHex = result.AccentHex,
            });
        }

        if (_settingsLoaded) _ = SaveAsync();
        ApplyCustomTheme(result.Id);
    }

    // ── Accent commands ──

    /// <summary>Re-build the swatches list from App.AccentPresets, marking the current pick as active.</summary>
    private void RebuildAccentSwatches()
    {
        AccentSwatches.Clear();
        foreach (var p in App.AccentPresets)
        {
            AccentSwatches.Add(new AccentSwatch
            {
                Name = p.Name,
                Hex = p.Hex,
                IsActive = string.Equals(p.Name, ActiveAccentName, StringComparison.OrdinalIgnoreCase),
            });
        }
        IsCustomAccentSelected = string.Equals(ActiveAccentName, "Custom", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ApplyAccentPreset(AccentSwatch? swatch)
    {
        if (swatch == null) return;
        ApplyAccent(swatch.Hex, swatch.Name);
    }

    [RelayCommand]
    private void ApplyCustomAccent()
    {
        var hex = NormalizeAccentHex(CustomAccentHex);
        if (hex == null) return;
        try { _ = Avalonia.Media.Color.Parse(hex); }
        catch { return; }
        ApplyAccent(hex, "Custom");
    }

    private bool _suppressPickerSync;
    private bool _suppressCustomHexHandler;

    private void ApplyAccent(string hex, string presetName)
    {
        ActiveAccentHex = hex;
        ActiveAccentName = presetName;
        _settings.AccentColorHex = hex;
        _settings.AccentPresetName = presetName;
        foreach (var s in AccentSwatches)
            s.IsActive = string.Equals(s.Name, presetName, StringComparison.OrdinalIgnoreCase);
        IsCustomAccentSelected = string.Equals(presetName, "Custom", StringComparison.OrdinalIgnoreCase);

        // Keep the custom picker in sync when the change comes from a preset click,
        // without re-entering OnPickerColorChanged and stomping the preset name.
        if (!_suppressPickerSync)
        {
            try
            {
                var parsed = Avalonia.Media.Color.Parse(hex);
                if (parsed != PickerColor)
                {
                    _suppressPickerSync = true;
                    try { PickerColor = parsed; }
                    finally { _suppressPickerSync = false; }
                }
            }
            catch { /* invalid hex shouldn't reach here */ }
        }

        // Keep the custom picker's hex-row swatch in lockstep with the active accent,
        // even when the change came from a preset click. Suppress the custom-hex handler
        // so it doesn't re-enter ApplyAccent and stomp the just-set preset name.
        if (!string.Equals(CustomAccentHex, hex, StringComparison.OrdinalIgnoreCase))
        {
            _suppressCustomHexHandler = true;
            try { CustomAccentHex = hex; }
            finally { _suppressCustomHexHandler = false; }
        }

        AccentChanged?.Invoke(this, hex);
        _ = SaveAsync();
    }

    private static string? NormalizeAccentHex(string? value)
    {
        var hex = (value ?? string.Empty).Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        return hex.Length is 7 or 9 ? hex : null;
    }

    private void ApplyTheme(string themeKey)
    {
        ActiveCustomThemeId = null;
        foreach (var t in CustomThemes)
            t.IsActive = false;

        SetActiveThemeFlags(themeKey);
        ThemeChanged?.Invoke(this, ResolveActiveThemeKey());
        _ = SaveAsync();
    }

    private void SetActiveThemeFlags(string themeKey)
    {
        IsGrayTheme = themeKey == "Gray";
        IsDarkTheme = themeKey == "Dark";
        IsLightTheme = themeKey == "Light";
        IsSystemTheme = themeKey == "System";
        IsMidnightTheme = themeKey == "Midnight";

        if (themeKey == "__Custom") return; // custom-theme active: all built-in flags stay false

        // Default-safety: if no flag matched, fall back to Gray.
        if (!IsGrayTheme && !IsDarkTheme && !IsLightTheme && !IsSystemTheme && !IsMidnightTheme)
            IsGrayTheme = true;
    }

    private static CustomThemeTile MapDefToTile(CustomThemeDefinition def) => new()
    {
        Id = def.Id,
        Name = def.Name,
        AccentHex = def.AccentHex,
        SidebarHex = def.SidebarBackgroundHex,
        MainHex = def.MainBackgroundHex,
        BaseMode = def.BaseMode,
    };

    /// <summary>
    /// Returns the actual theme key to apply now. For "System" this resolves to either
    /// Gray or Light depending on the OS appearance setting.
    /// </summary>
    private string ResolveActiveThemeKey()
    {
        if (!string.IsNullOrEmpty(ActiveCustomThemeId)) return "Custom:" + ActiveCustomThemeId;
        if (IsLightTheme) return "Light";
        if (IsDarkTheme) return "Dark";
        if (IsMidnightTheme) return "Midnight";
        if (IsSystemTheme) return IsSystemDarkMode() ? "Gray" : "Light";
        return "Gray";
    }

    private static bool IsSystemDarkMode()
    {
        return Helpers.PlatformHelper.IsSystemDarkMode();
    }

    // ── Property change handlers ──

    partial void OnScanOnStartupChanged(bool value)
    {
        _settings.ScanOnStartup = value;
        _ = SaveAsync();
    }

    partial void OnWatchFoldersEnabledChanged(bool value)
    {
        _settings.WatchFoldersEnabled = value;
        _ = SaveAsync();
        // Start/stop the filesystem watchers to match the new preference.
        App.Services?.GetService<ILibraryWatcherService>()?.Refresh();
    }

    partial void OnIncludePrereleaseUpdatesChanged(bool value)
    {
        _settings.IncludePrereleaseUpdates = value;
        _ = SaveAsync();
    }

    partial void OnCrossfadeEnabledChanged(bool value)
    {
        ApplyAudioSettings();
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnSongTransitionsEnabledChanged(bool value)
    {
        ApplyAudioSettings();
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnTransitionStyleChanged(string value)
    {
        OnPropertyChanged(nameof(IsCrossfadeStyle));
        OnPropertyChanged(nameof(IsAutoMixStyle));
        ApplyAudioSettings();
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnSongTransitionStrengthChanged(string value)
    {
        ApplyAudioSettings();
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnSongTransitionBeatMatchChanged(bool value)
    {
        ApplyAudioSettings();
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnCrossfadeDurationChanged(double value)
    {
        var clamped = Math.Clamp(value, 1, 12);
        if (clamped != value)
        {
            CrossfadeDuration = clamped;
            return;
        }

        ApplyAudioSettings();
        _ = SaveAsync();
    }

    partial void OnSoundCheckEnabledChanged(bool value)
    {
        ApplyAudioSettings();
        _ = SaveAsync();
    }

    partial void OnExclusiveAudioEnabledChanged(bool value)
    {
        _audioPlayer?.SetExclusiveMode(value);
        if (!value) ExclusiveAudioStatus = "";
        _ = SaveAsync();
    }

    partial void OnSidebarHoverExpandChanged(bool value)
    {
        _ = SaveAsync();
    }

    partial void OnPlaybackBarBackgroundOpacityChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        if (clamped != value)
        {
            PlaybackBarBackgroundOpacity = clamped;
            return;
        }

        ApplyPlayerSettings();
        if (_settingsLoaded && !_suspendSettingPersistence) _ = SaveAsync();
    }

    partial void OnCollapseAlbumEditionsChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnTrackTitleMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnArtistMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnCoverFlowMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnCoverFlowArtistMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnCoverFlowAlbumMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnLyricsTitleMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnLyricsArtistMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnMiniPlayerTitleMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnMiniPlayerAlbumMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnLrcLibEnabledChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnNetEaseEnabledChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnAcoustIdApiKeyChanged(string value)
    {
        _settings.AcoustIdApiKey = value ?? string.Empty;
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnFpcalcPathChanged(string value)
    {
        _settings.FpcalcPath = value ?? string.Empty;
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnFfmpegPathChanged(string value)
    {
        // Reflect the new path into the live settings object *before* probing so the
        // status label and the converter see it immediately. RefreshFfmpegStatus()
        // resolves through GetFfmpegPath(), which reads _settings.FfmpegPath; without
        // this the label lags one edit behind (stays "Not found" after a valid paste).
        // SaveAsync() re-syncs + persists below.
        _settings.FfmpegPath = value ?? string.Empty;
        RefreshFfmpegStatus();
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnReplayGainModeChanged(string value)
    {
        // Keep the on/off toggle mirrored to the mode and remember the last
        // active mode so re-enabling restores it.
        var isOff = string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase);
        if (!isOff && !string.IsNullOrEmpty(value))
            _lastActiveReplayGainMode = value;
        _suppressRgNotify = true;
        ReplayGainEnabled = !isOff;
        _suppressRgNotify = false;

        if (_suspendSettingPersistence) return;
        _audioPlayer?.ApplyReplayGain(value, ReplayGainPreampDb);
        _ = SaveAsync();
    }

    partial void OnReplayGainEnabledChanged(bool value)
    {
        if (_suppressRgNotify) return;
        ReplayGainMode = value ? _lastActiveReplayGainMode : "Off";
    }

    partial void OnGaplessPlaybackEnabledChanged(bool value)
    {
        _audioPlayer?.SetGapless(value);
        if (_player != null) _player.GaplessEnabled = value;
        _ = SaveAsync();
    }

    partial void OnBpmKeyAnalysisEnabledChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnWriteAnalysisToTagsChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnReplayGainPreampDbChanged(double value)
    {
        if (_suspendSettingPersistence) return;
        _audioPlayer?.ApplyReplayGain(ReplayGainMode, value);
        _ = SaveAsync();
    }

    private int _ffmpegProbeGeneration;

    /// <summary>Probes the configured or auto-detected ffmpeg path and updates
    /// <see cref="FfmpegStatus"/> so the Settings view can show whether the
    /// converter will work without the user having to open the dialog.
    /// Existence is not enough — the resolved binary is run with <c>-version</c>
    /// to confirm it is genuinely ffmpeg (so e.g. a README file is rejected).</summary>
    public void RefreshFfmpegStatus()
    {
        var svc = App.Services?.GetService<IAudioConverterService>();
        if (svc == null) { FfmpegStatus = string.Empty; return; }

        var path = svc.GetFfmpegPath();
        // Bump the generation so a slow probe from an earlier edit can't overwrite
        // the status of a newer one.
        var generation = ++_ffmpegProbeGeneration;

        if (path == null)
        {
            FfmpegStatus = "Not found — set a path below, or install ffmpeg on your PATH.";
            return;
        }

        FfmpegStatus = $"Checking {path}…";

        _ = Task.Run(async () =>
        {
            var version = await svc.ValidateFfmpegAsync(path);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _ffmpegProbeGeneration) return; // superseded by a newer edit
                FfmpegStatus = version != null
                    ? $"ffmpeg found ✓ — {version}"
                    : $"Not a valid ffmpeg executable — {path}";
            });
        });
    }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not Avalonia.Controls.Window owner) return;

        var top = Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (top == null) return;

        var picks = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Locate ffmpeg",
            AllowMultiple = false,
        });
        if (picks.Count > 0)
            FfmpegPath = picks[0].Path.LocalPath;
    }

    // ── Integration handlers ──

    partial void OnDiscordRichPresenceEnabledChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        if (_discord != null)
        {
            _ = HandleDiscordToggleAsync(value);
        }
        else
        {
            _ = SaveAsync();
        }
    }

    private async Task HandleDiscordToggleAsync(bool enabled)
    {
        if (enabled)
        {
            var ok = await _discord!.ConnectAsync();
            if (!ok)
            {
                // Revert toggle — connection failed (Discord not running, etc.)
                _suspendSettingPersistence = true;
                DiscordRichPresenceEnabled = false;
                _suspendSettingPersistence = false;
                Debug.WriteLine("[Settings] Discord connect failed — reverted toggle.");
            }
            else
            {
                // Republish current playback state so the track appears immediately.
                await RepublishDiscordPresenceAsync();
            }
        }
        else
        {
            await _discord!.ClearAsync();
            await _discord.DisconnectAsync();
        }

        await SaveAsync();
    }

    /// <summary>
    /// Pushes the current playback state to Discord after a reconnect,
    /// so the user doesn't have to wait for the next track/state event.
    /// </summary>
    private async Task RepublishDiscordPresenceAsync()
    {
        if (_discord == null || !_discord.IsConnected || _player == null)
            return;

        var track = _player.CurrentTrack;
        if (track == null || _player.State == PlaybackState.Stopped)
        {
            await _discord.ClearAsync();
            return;
        }

        var artworkUrl = _loon?.GetArtworkUrl(track.AlbumArtworkPath);
        var dto = new DiscordPresenceTrack(
            Title: track.Title ?? "Unknown",
            Artist: track.Artist ?? "Unknown Artist",
            Album: track.Album,
            Duration: track.Duration,
            ArtworkUrl: artworkUrl);

        var isPlaying = _player.State == PlaybackState.Playing;
        await _discord.UpdateAsync(dto, _player.Position, _player.Duration, isPlaying);
    }

    partial void OnLastFmScrobblingEnabledChanged(bool value)
    {
        _ = SaveAsync();
    }

    [RelayCommand]
    private async Task LoginLastFm()
    {
        if (_lastFm == null) return;

        // Root cause fix: a prior poll held the "in progress" guard for the full 2-minute
        // window, so if the user declined in the browser and clicked Connect again the
        // command silently no-op'd (dead button). Cancel any in-flight poll and start a
        // fresh attempt instead of blocking re-initiation.
        _lastFmAuthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lastFmAuthCts = cts;

        LastFmStatusText = "Opening browser...";
        var authUrl = await _lastFm.GetAuthUrlAsync();
        if (string.IsNullOrEmpty(authUrl))
        {
            LastFmStatusText = "Failed to get auth URL. Check API key.";
            return;
        }

        try
        {
            Helpers.PlatformHelper.OpenUrl(authUrl);
        }
        catch
        {
            LastFmStatusText = "Failed to open browser.";
            return;
        }

        LastFmStatusText = "Waiting for authorization in browser...";
        _ = PollLastFmAuthAsync(cts);
    }

    private async Task PollLastFmAuthAsync(CancellationTokenSource cts)
    {
        if (_lastFm == null) return;
        var token = cts.Token;
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            var failedAttempts = 0;
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                var success = await _lastFm.CompleteAuthAsync();
                if (success)
                {
                    IsLastFmConnected = true;
                    LastFmScrobblingEnabled = true;
                    LastFmUsername = _lastFm.Username ?? "";
                    LastFmStatusText = $"Connected as {LastFmUsername}";
                    _settings.LastFmSessionKey = _lastFm.GetSessionKey() ?? "";
                    await SaveAsync();
                    return;
                }

                failedAttempts++;

                // Root cause fix: status was left in "Waiting..." with no early reset path.
                // Reset to baseline quickly if authorization isn't completed.
                if (!IsLastFmConnected && failedAttempts >= 2)
                    LastFmStatusText = "Not connected";

                try
                {
                    await Task.Delay(2000, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (!IsLastFmConnected && !token.IsCancellationRequested)
                LastFmStatusText = "Not connected";
        }
        finally
        {
            // Only clear the shared handle if a newer attempt hasn't already replaced it.
            if (ReferenceEquals(_lastFmAuthCts, cts))
                _lastFmAuthCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void LogoutLastFm()
    {
        _lastFm?.Logout();
        IsLastFmConnected = false;
        LastFmUsername = "";
        LastFmStatusText = "Not connected";
        _ = SaveAsync();
    }

    // ── ListenBrainz handlers ──

    partial void OnListenBrainzScrobblingEnabledChanged(bool value)
    {
        _ = SaveAsync();
    }

    partial void OnListenBrainzTokenChanged(string value)
    {
        // Clear any stale validation error as soon as the user edits the token.
        ListenBrainzError = "";
        // Just keep the in-memory service in sync; the user must hit "Connect"
        // to validate and persist. Don't autosave keystroke-by-keystroke.
        _listenBrainz?.Configure(value);
    }

    [RelayCommand]
    private async Task TestListenBrainz()
    {
        if (_listenBrainz == null) return;
        if (string.IsNullOrWhiteSpace(ListenBrainzToken))
        {
            ListenBrainzError = "Token required";
            ListenBrainzStatusText = "Not connected";
            return;
        }

        ListenBrainzError = "";
        ListenBrainzStatusText = "Validating...";
        _listenBrainz.Configure(ListenBrainzToken);
        var username = await _listenBrainz.ValidateTokenAsync();
        if (!string.IsNullOrEmpty(username))
        {
            ListenBrainzUsername = username!;
            IsListenBrainzConnected = true;
            ListenBrainzStatusText = $"Connected as {username}";
            await SaveAsync();
        }
        else
        {
            IsListenBrainzConnected = false;
            ListenBrainzUsername = "";
            ListenBrainzError = "Token invalid or network error.";
            ListenBrainzStatusText = "Not connected";
        }
    }

    [RelayCommand]
    private void LogoutListenBrainz()
    {
        _listenBrainz?.Logout();
        IsListenBrainzConnected = false;
        ListenBrainzToken = "";
        ListenBrainzUsername = "";
        ListenBrainzStatusText = "Not connected";
        ListenBrainzError = "";
        _ = SaveAsync();
    }

    // ── Equalizer handlers ──

    partial void OnEqualizerEnabledChanged(bool value)
    {
        if (_suppressEqNotify) return;
        ApplyEqualizer();
        QueueEqualizerSave();
    }

    partial void OnSelectedEqPresetIndexChanged(int value)
    {
        if (_suppressEqNotify) return;

        ApplyEqualizer();

        if (value > 0)
            LoadPresetBands(value - 1);

        QueueEqualizerSave();
    }

    partial void OnSelectedEqPresetNameChanged(string value)
    {
        if (_suppressEqNotify) return;
        if (string.IsNullOrEmpty(value)) return;

        int idx = System.Array.IndexOf(EqPresetNames, value);
        if (idx < 0) return;

        _suppressEqNotify = true;
        SelectedEqPresetIndex = idx;
        _suppressEqNotify = false;

        ApplyEqualizer();

        if (idx > 0)
        {
            LoadPresetBands(idx - 1);
            SyncCustomInVisiblePresets(false);
        }

        QueueEqualizerSave();
    }

    private void SyncCustomInVisiblePresets(bool shouldShowCustom)
    {
        // Keep the ComboBox ItemsSource stable while its popup is open.
        // Mutating this collection on selection makes the popup re-layout and visibly shift.
    }

    /// <summary>Populate the band editor from a VLC preset curve (one parametric band per graphic frequency).</summary>
    private void LoadPresetBands(int vlcPresetIndex)
    {
        TryGetVlcPresetCurve(vlcPresetIndex, out var bands, out _);
        SetEqBands(ParametricEqMath.FromGraphicBands(bands));
    }

    /// <summary>A band's frequency / gain / Q was edited: switch to Custom, apply, save.</summary>
    private void OnEqBandEdited()
    {
        if (_suppressEqNotify) return;

        if (SelectedEqPresetIndex != 0)
        {
            _suppressEqNotify = true;
            SyncCustomInVisiblePresets(true);
            SelectedEqPresetIndex = 0;
            SelectedEqPresetName = "Custom";
            _suppressEqNotify = false;
        }

        ApplyEqualizer();
        QueueEqualizerSave();
    }

    [RelayCommand]
    private void AddEqBand()
    {
        if (!CanAddEqBand) return;
        // New band starts neutral at 1 kHz so adding never changes the sound.
        EqBands.Add(new EqBandViewModel(1000, 0, ParametricEqMath.DefaultQ, OnEqBandEdited));
        OnPropertyChanged(nameof(CanAddEqBand));
        OnPropertyChanged(nameof(CanRemoveEqBand));
        OnEqBandEdited();
    }

    [RelayCommand]
    private void RemoveEqBand(EqBandViewModel? band)
    {
        if (band == null || !CanRemoveEqBand || !EqBands.Remove(band)) return;
        OnPropertyChanged(nameof(CanAddEqBand));
        OnPropertyChanged(nameof(CanRemoveEqBand));
        OnEqBandEdited();
    }

    [RelayCommand]
    private void ResetEqualizer()
    {
        _suppressEqNotify = true;
        SelectedEqPresetIndex = 1; // "Flat"
        SelectedEqPresetName = "Flat";
        SyncCustomInVisiblePresets(false);
        SetEqBands(ParametricEqMath.FromGraphicBands(null));
        _suppressEqNotify = false;

        ApplyEqualizer();
        QueueEqualizerSave();
    }

    // ── Snoozed tracks (hidden from shuffle + radio for a period) ──

    /// <summary>Tracks currently snoozed, shown in a reversible Settings list.</summary>
    public ObservableCollection<Track> SnoozedTracks { get; } = new();

    /// <summary>True when at least one track is snoozed (drives the empty-state placeholder).</summary>
    [ObservableProperty] private bool _hasSnoozedTracks;

    public void RefreshSnoozedTracks()
    {
        SnoozedTracks.Clear();
        foreach (var t in _library.Tracks.Where(t => t.IsSnoozed).OrderBy(t => t.SnoozedUntil))
            SnoozedTracks.Add(t);
        HasSnoozedTracks = SnoozedTracks.Count > 0;
    }

    [RelayCommand]
    private async Task Unsnooze(Track? track)
    {
        if (track == null) return;
        await _library.SetTracksSnoozedAsync(new[] { track }, null);
        RefreshSnoozedTracks();
    }

    // ── Library overview + Storage ──

    public void RefreshLibraryStats()
    {
        var tracks = _library.Tracks;
        TotalSongs = tracks.Count;
        TotalArtists = _library.Artists.Count;
        TotalAlbums = _library.Albums.Count;

        // Single pass over Tracks: Sum(FileSize) + Sum(Duration) + Count(IsLossless)
        // + Count(IsHiResLossless) all in one iteration. Previously 4 LINQ passes
        // over the same collection plus a redundant tracks.Count subtraction.
        long totalBytes = 0;
        long totalDurationTicks = 0;
        int losslessCount = 0;
        int hiResCount = 0;
        foreach (var t in tracks)
        {
            totalBytes += t.FileSize;
            totalDurationTicks += t.Duration.Ticks;
            if (t.IsLossless) losslessCount++;
            if (t.IsHiResLossless) hiResCount++;
        }

        TotalFileSize = FormatLibrarySize(totalBytes);
        TotalListeningTime = FormatDuration(TimeSpan.FromTicks(totalDurationTicks));
        LosslessCount = losslessCount;
        LossyCount = tracks.Count - losslessCount;
        HiResCount = hiResCount;
        RefreshSnoozedTracks();
        if (tracks.Count > 0)
        {
            var pct = (double)losslessCount / tracks.Count;
            LosslessPercentage = pct;
            LosslessPercentageText = $"{pct * 100:F0}%";
            LossyPercentageText = $"{(1 - pct) * 100:F0}%";
            HiResPercentageText = $"{(double)hiResCount / tracks.Count * 100:F0}%";
        }
        else
        {
            LosslessPercentage = 0;
            LosslessPercentageText = "0%";
            LossyPercentageText = "0%";
            HiResPercentageText = "0%";
        }
    }

    private static string FormatLibrarySize(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F1} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F0} MB";
        return $"{bytes / (double)(1L << 10):F0} KB";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalMinutes} min";
    }

    public async Task RefreshPlaylistCountAsync()
    {
        try
        {
            var playlists = await _persistence.LoadPlaylistsAsync();
            TotalPlaylists = playlists.Count;
        }
        catch
        {
            TotalPlaylists = 0;
        }
    }

    public void RefreshStorageInfo(bool forceRefresh = false)
    {
        var dataDir = _persistence.DataDirectory;
        if (!Directory.Exists(dataDir)) return;

        long librarySize = GetFileSize(Path.Combine(dataDir, "library.json"));
        long queueSize = GetFileSize(Path.Combine(dataDir, "queue.json"));
        long playlistsSize = GetFileSize(Path.Combine(dataDir, "playlists.json"));
        long settingsSize = GetFileSize(Path.Combine(dataDir, "settings.json"));
        long artworkSize = GetDirectorySize(Path.Combine(dataDir, "artwork"), forceRefresh);

        StorageLibraryData = FormatBytes(librarySize + queueSize);
        StorageArtwork = FormatBytes(artworkSize);
        StoragePlaylists = FormatBytes(playlistsSize);
        StorageSettings = FormatBytes(settingsSize);
        StorageTotal = FormatBytes(librarySize + queueSize + playlistsSize + settingsSize + artworkSize);
    }

    /// <summary>
    /// Async variant of <see cref="RefreshStorageInfo"/> for click paths (e.g. opening Settings).
    /// Computes sizes on a background thread, then marshals formatted strings back to the UI.
    /// </summary>
    public async Task RefreshStorageInfoAsync()
    {
        var dataDir = _persistence.DataDirectory;
        if (!Directory.Exists(dataDir)) return;

        var result = await Task.Run(() =>
        {
            long librarySize = GetFileSize(Path.Combine(dataDir, "library.json"));
            long queueSize = GetFileSize(Path.Combine(dataDir, "queue.json"));
            long playlistsSize = GetFileSize(Path.Combine(dataDir, "playlists.json"));
            long settingsSize = GetFileSize(Path.Combine(dataDir, "settings.json"));
            long artworkSize = GetDirectorySize(Path.Combine(dataDir, "artwork"));

            return new
            {
                LibraryData = FormatBytes(librarySize + queueSize),
                Artwork = FormatBytes(artworkSize),
                Playlists = FormatBytes(playlistsSize),
                Settings = FormatBytes(settingsSize),
                Total = FormatBytes(librarySize + queueSize + playlistsSize + settingsSize + artworkSize),
            };
        }).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StorageLibraryData = result.LibraryData;
            StorageArtwork = result.Artwork;
            StoragePlaylists = result.Playlists;
            StorageSettings = result.Settings;
            StorageTotal = result.Total;
        });
    }

    private static long GetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception ex) { Debug.WriteLine($"[Settings] GetFileSize failed for '{path}': {ex.Message}"); return 0; }
    }

    // Cache the recursive artwork-folder walk for a few seconds so repeated
    // RefreshStorageInfo calls (e.g. open Settings, close, open again) don't
    // re-walk the whole tree. Cache is invalidated on cache-clear / scan /
    // settings-reset paths because they call RefreshStorageInfo with the
    // expectation of a fresh number — those paths bypass the cache via the
    // forceRefresh flag below.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, long Bytes)>
        _dirSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan _dirSizeCacheTtl = TimeSpan.FromSeconds(5);

    private static long GetDirectorySize(string path) => GetDirectorySize(path, forceRefresh: false);

    private static long GetDirectorySize(string path, bool forceRefresh)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;

            if (!forceRefresh
                && _dirSizeCache.TryGetValue(path, out var cached)
                && DateTime.UtcNow - cached.Stamp < _dirSizeCacheTtl)
            {
                return cached.Bytes;
            }

            long size = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            _dirSizeCache[path] = (DateTime.UtcNow, size);
            return size;
        }
        catch (Exception ex) { Debug.WriteLine($"[Settings] GetDirectorySize failed for '{path}': {ex.Message}"); return 0; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    // ── Files / folder commands ──

    /// <summary>Called from the View after the folder picker dialog returns a path.</summary>
    public async Task AddFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || MusicFolders.Contains(path))
            return;

        MusicFolders.Add(path);
        _settings.MusicFolders = MusicFolders.ToList();
        OnPropertyChanged(nameof(MediaFolderDisplay));
        await SaveAsync();
        MusicFoldersChanged?.Invoke(this, EventArgs.Empty);

        // Scan the library automatically so the user doesn't have to press "Scan".
        // A full scan over all folders reuses the unchanged-file fast path, so only
        // the newly added folder's files actually get read.
        var folders = MusicFolders.ToList();
        _ = Task.Run(async () =>
        {
            try { await _library.ScanAsync(folders); }
            catch (Exception ex) { Debug.WriteLine($"[Settings] Auto-scan after folder add failed: {ex.Message}"); }
        });
    }

    [RelayCommand]
    private async Task RemoveFolder(string folder)
    {
        MusicFolders.Remove(folder);
        _settings.MusicFolders = MusicFolders.ToList();
        OnPropertyChanged(nameof(MediaFolderDisplay));
        await SaveAsync();
        MusicFoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetScanStatus(string text, bool autoClear = false)
    {
        ScanStatusText = text;
        _scanStatusClearCts?.Cancel();
        _scanStatusClearCts?.Dispose();
        _scanStatusClearCts = null;

        if (autoClear && !string.IsNullOrEmpty(text))
        {
            var cts = new CancellationTokenSource();
            _scanStatusClearCts = cts;
            _ = ClearScanStatusAfterDelay(cts.Token);
        }
    }

    private async Task ClearScanStatusAfterDelay(CancellationToken ct)
    {
        try
        {
            await Task.Delay(3000, ct);
            ScanStatusText = "";
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task OpenOrganizeFiles()
        => await MetadataHelper.OpenOrganizeFilesDialog(this);

    [RelayCommand]
    private async Task OpenDuplicateFinder()
        => await MetadataHelper.OpenDuplicateFinderDialog();

    [RelayCommand]
    private async Task OpenMetadataFinder()
        => await MetadataHelper.OpenMetadataFinderDialog();

    [RelayCommand]
    private async Task OpenPlaylistImport()
        => await MetadataHelper.OpenPlaylistImportDialog();

    [RelayCommand]
    private async Task Rescan()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatusText = "Starting scan...";
        ScanProgress = 0;

        try
        {
            await _library.ScanAsync(MusicFolders);
            SetScanStatus(_library.Tracks.Count == 0
                ? "No tracks found."
                : $"{_library.Tracks.Count} tracks found.", autoClear: true);
            RefreshLibraryStats();
            RefreshStorageInfo(forceRefresh: true);
        }
        catch (Exception ex)
        {
            SetScanStatus($"Scan error: {ex.Message}", autoClear: true);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task RebuildIndex()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatusText = "Rebuilding library index...";
        ScanProgress = 0;

        try
        {
            await _library.RebuildIndexAsync();
            SetScanStatus(_library.Tracks.Count == 0
                ? "No tracks found."
                : "Indexed Library.", autoClear: true);
            RefreshLibraryStats();
            RefreshStorageInfo(forceRefresh: true);
        }
        catch (Exception ex)
        {
            SetScanStatus($"Index rebuild error: {ex.Message}", autoClear: true);
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ── Reset / Clear commands ──

    [RelayCommand]
    private void ShowResetConfirm() => IsResetConfirmVisible = true;

    [RelayCommand]
    private void CancelReset() => IsResetConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmResetLibrary()
    {
        IsResetConfirmVisible = false;

        // Clear library, playlists, queue
        try
        {
            await _library.ClearAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear library: {ex.Message}");
        }

        try
        {
            await _persistence.SavePlaylistsAsync(new List<Playlist>());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear playlists: {ex.Message}");
        }

        try
        {
            await _persistence.SaveQueueStateAsync(new QueueState());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear queue state: {ex.Message}");
        }

        // Clear artwork cache (albums + artists)
        try
        {
            var artworkDir = Path.Combine(_persistence.DataDirectory, "artwork");
            if (Directory.Exists(artworkDir))
            {
                Directory.Delete(artworkDir, true);
                Directory.CreateDirectory(artworkDir);
                Directory.CreateDirectory(Path.Combine(artworkDir, "artists"));
            }
            _dirSizeCache.TryRemove(artworkDir, out _);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear artwork cache: {ex.Message}");
        }

        // Clear lyrics cache
        try
        {
            var lyricsDir = Path.Combine(Helpers.AppPaths.DataRoot, "lyrics_cache");
            if (Directory.Exists(lyricsDir))
            {
                Directory.Delete(lyricsDir, true);
                Directory.CreateDirectory(lyricsDir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear lyrics cache: {ex.Message}");
        }

        // Clear playlist covers
        try
        {
            var coversDir = Path.Combine(_persistence.DataDirectory, "playlist_covers");
            if (Directory.Exists(coversDir))
            {
                Directory.Delete(coversDir, true);
                Directory.CreateDirectory(coversDir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear playlist covers: {ex.Message}");
        }

        // Clear offline / streaming cache
        try
        {
            var cacheDir = Path.Combine(_persistence.DataDirectory, "cache");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
                Directory.CreateDirectory(cacheDir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear offline cache: {ex.Message}");
        }

        // Clear audit trail
        try
        {
            var auditDir = Path.Combine(_persistence.DataDirectory, "audit");
            if (Directory.Exists(auditDir))
            {
                Directory.Delete(auditDir, true);
                Directory.CreateDirectory(auditDir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear audit trail: {ex.Message}");
        }

        // Clear crash log
        try
        {
            var crashPath = Path.Combine(_persistence.DataDirectory, "crash.log");
            if (File.Exists(crashPath))
                File.Delete(crashPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear crash log: {ex.Message}");
        }

        // Clear index cache
        try
        {
            var indexPath = Path.Combine(_persistence.DataDirectory, "indexes.json");
            if (File.Exists(indexPath))
                File.Delete(indexPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear index cache: {ex.Message}");
        }

        // Reset settings to defaults and save
        var defaultSettings = new AppSettings();
        try
        {
            await _persistence.SaveSettingsAsync(defaultSettings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to save default settings: {ex.Message}");
        }

        // Update ViewModel with defaults (suspend persistence during update)
        _suspendSettingPersistence = true;
        try
        {
            _settings = defaultSettings;

            // Theme — reset to default (Gray)
            SetActiveThemeFlags("Gray");

            // Accent colour — reset to default (Crimson)
            ActiveAccentHex = defaultSettings.AccentColorHex;
            ActiveAccentName = defaultSettings.AccentPresetName;
            CustomAccentHex = ActiveAccentHex;
            try
            {
                _suppressPickerSync = true;
                PickerColor = Avalonia.Media.Color.Parse(ActiveAccentHex);
            }
            catch { }
            finally { _suppressPickerSync = false; }
            RebuildAccentSwatches();

            // Preferences
            ScanOnStartup = true;
            WatchFoldersEnabled = true;
            OrganizePattern = "{AlbumArtist}/{Album}/{TrackNo} {Title}";
            OrganizeTargetRoot = string.Empty;
            AcoustIdApiKey = string.Empty;
            FpcalcPath = string.Empty;
            IncludePrereleaseUpdates = false;

            // Playback
            CrossfadeEnabled = false;
            CrossfadeDuration = 6;
            SongTransitionsEnabled = false;
            TransitionStyle = "Crossfade";
            SongTransitionStrength = "Balanced";
            SongTransitionBeatMatch = true;
            SoundCheckEnabled = false;
            ExclusiveAudioEnabled = false;
            GaplessPlaybackEnabled = true;
            BpmKeyAnalysisEnabled = true;
            WriteAnalysisToTags = false;
            ReplayGainMode = "Auto";
            TrackTitleMarqueeEnabled = true;
            ArtistMarqueeEnabled = true;
            CoverFlowMarqueeEnabled = true;
            CoverFlowArtistMarqueeEnabled = true;
            CoverFlowAlbumMarqueeEnabled = true;
            LyricsTitleMarqueeEnabled = true;
            LyricsArtistMarqueeEnabled = true;
            MiniPlayerTitleMarqueeEnabled = true;
            MiniPlayerAlbumMarqueeEnabled = true;
            SidebarHoverExpand = true;

            // Lyrics providers
            LrcLibEnabled = true;
            NetEaseEnabled = true;

            // Equalizer
            _suppressEqNotify = true;
            EqualizerEnabled = true;
            SelectedEqPresetIndex = 1; // Flat
            SelectedEqPresetName = "Flat";
            SyncCustomInVisiblePresets(false);
            SetEqBands(ParametricEqMath.FromGraphicBands(null));
            _suppressEqNotify = false;

            // Music folders
            MusicFolders.Clear();
            FolderRules.Clear();
            OnPropertyChanged(nameof(MediaFolderDisplay));

            // Integrations
            DiscordRichPresenceEnabled = false;
            LastFmScrobblingEnabled = true;
            LastFmUsername = "";
            IsLastFmConnected = false;
            LastFmStatusText = "Not connected";

            ListenBrainzScrobblingEnabled = true;
            ListenBrainzToken = "";
            ListenBrainzUsername = "";
            IsListenBrainzConnected = false;
            ListenBrainzStatusText = "Not connected";
            _listenBrainz?.Logout();

            // Disconnect Discord if connected
            if (_discord != null)
            {
                _ = _discord.DisconnectAsync();
            }

            // Apply audio settings
            ApplyAudioSettings();

            // Apply theme
            ThemeChanged?.Invoke(this, ResolveActiveThemeKey());

            // Apply accent
            AccentChanged?.Invoke(this, ActiveAccentHex);
        }
        finally
        {
            _suspendSettingPersistence = false;
        }

        SetScanStatus("All settings and data have been reset.", autoClear: true);
        RefreshLibraryStats();
        TotalPlaylists = 0;
        RefreshStorageInfo();

        SettingsReset?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task FetchArtistImages()
    {
        if (_artistImageService is null) return;

        var artists = _library.Artists;
        if (artists.Count == 0)
        {
            SetScanStatus("No artists in library.", autoClear: true);
            return;
        }

        ScanStatusText = $"Fetching artist images for {artists.Count} artists...";

        try
        {
            var fetched = 0;
            await _artistImageService.FetchAndCacheAsync(artists, (artist, _) =>
            {
                fetched++;
                Dispatcher.UIThread.Post(() =>
                    ScanStatusText = $"Fetched {fetched} artist image(s)...");
            });

            SetScanStatus(fetched > 0
                ? $"Fetched {fetched} new artist image(s)."
                : "All artist images are already cached.", autoClear: true);
        }
        catch (Exception ex)
        {
            SetScanStatus($"Failed to fetch artist images: {ex.Message}", autoClear: true);
        }
    }

    [RelayCommand]
    private void ClearArtworkCache()
    {
        try
        {
            var artworkDir = Path.Combine(_persistence.DataDirectory, "artwork");
            if (Directory.Exists(artworkDir))
            {
                Directory.Delete(artworkDir, true);
                Directory.CreateDirectory(artworkDir);
            }
            _dirSizeCache.TryRemove(artworkDir, out _);
            RefreshStorageInfo();
            SetScanStatus("Artwork cache cleared.", autoClear: true);
        }
        catch (Exception ex)
        {
            SetScanStatus($"Failed to clear cache: {ex.Message}", autoClear: true);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            var dataDir = _persistence.DataDirectory;
            if (Directory.Exists(dataDir))
            {
                Helpers.PlatformHelper.OpenFolder(dataDir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to open data folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Silently checks for an update on startup. On success, sets IsUpdateAvailable
    /// + LatestVersionTag so the UI can surface a passive "Update available" badge
    /// without any user action. Errors are swallowed so startup is never noisy.
    /// </summary>
    public async Task CheckForUpdateSilentAsync()
    {
        if (_updateService is null) return;
        if (IsCheckingForUpdate || IsUpdateAvailable || IsDownloadingUpdate || IsReadyToInstall) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var update = await _updateService.CheckForUpdateAsync(IncludePrereleaseUpdates, cts.Token);
            if (update is null) return;
            if (update.InstallerApiUrl is null) return;

            // This runs inside Task.Run at startup, so continuations are on a
            // thread-pool thread. PropertyChanged must be raised on the UI thread
            // or the About page update UI won't refresh.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LatestVersionTag = update.TagName;
                IsLatestPrerelease = update.IsPrerelease;
                IsUpdateAvailable = true;
            });
        }
        catch
        {
            // Silent: no toast, no status text, no error banner on startup.
        }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (_updateService is null || IsCheckingForUpdate) return;

        // Reset state
        IsUpdateAvailable = false;
        IsDownloadingUpdate = false;
        IsReadyToInstall = false;
        DownloadProgress = 0;
        UpdateStatusText = "Checking for updates...";
        IsCheckingForUpdate = true;
        _downloadedInstallerPath = null;

        try
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var update = await _updateService.CheckForUpdateAsync(IncludePrereleaseUpdates, _updateCts.Token);

            if (update is null)
            {
                UpdateStatusText = "You're on the latest version.";
                _ = ClearUpdateStatusAfterDelay();
            }
            else if (update.InstallerApiUrl is null)
            {
                LatestVersionTag = update.TagName;
                IsLatestPrerelease = update.IsPrerelease;
                UpdateStatusText = $"{update.TagName} available — installer not found. Visit GitHub.";
            }
            else
            {
                LatestVersionTag = update.TagName;
                IsLatestPrerelease = update.IsPrerelease;
                UpdateStatusText = $"{update.TagName} is available.";
                IsUpdateAvailable = true;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update check timed out. Try again later.";
            _ = ClearUpdateStatusAfterDelay();
        }
        catch
        {
            UpdateStatusText = "Couldn't check for updates. Try again later.";
            _ = ClearUpdateStatusAfterDelay();
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_updateService is null || IsDownloadingUpdate) return;

        IsUpdateAvailable = false;
        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        UpdateStatusText = "Downloading update...";

        try
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            // Re-check to get fresh URL
            var update = await _updateService.CheckForUpdateAsync(IncludePrereleaseUpdates, _updateCts.Token);
            if (update is null || update.InstallerApiUrl is null)
            {
                UpdateStatusText = "Update no longer available.";
                IsDownloadingUpdate = false;
                _ = ClearUpdateStatusAfterDelay();
                return;
            }

            var progress = new Progress<double>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    DownloadProgress = p;
                    UpdateStatusText = $"Downloading update... {p:F0}%";
                }));

            _downloadedInstallerPath = await _updateService.DownloadInstallerAsync(
                update, progress, _updateCts.Token);

            UpdateStatusText = "Update ready to install.";
            IsReadyToInstall = true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("corrupted"))
        {
            UpdateStatusText = "Download corrupted. Try again.";
            _ = ClearUpdateStatusAfterDelay();
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Download cancelled.";
            _ = ClearUpdateStatusAfterDelay();
        }
        catch
        {
            UpdateStatusText = "Download failed. Try again.";
            _ = ClearUpdateStatusAfterDelay();
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void CancelUpdate()
    {
        _updateCts?.Cancel();
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (_updateService is null || string.IsNullOrEmpty(_downloadedInstallerPath)) return;

        if (_updateService.LaunchInstaller(_downloadedInstallerPath))
        {
            // Shut down the app so Inno Setup can replace files
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(0);
            }
        }
        else
        {
            UpdateStatusText = "Couldn't start installer. Download manually from GitHub.";
            IsReadyToInstall = false;
        }
    }

    private async Task ClearUpdateStatusAfterDelay()
    {
        await Task.Delay(5000);
        if (!IsUpdateAvailable && !IsDownloadingUpdate && !IsReadyToInstall)
            UpdateStatusText = "";
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        Helpers.PlatformHelper.OpenUrl("https://github.com/heartached/Noctis");
    }

    [RelayCommand]
    private void OpenDiscord()
    {
        Helpers.PlatformHelper.OpenUrl("https://discord.gg/BNCDZQUVx7");
    }
}

