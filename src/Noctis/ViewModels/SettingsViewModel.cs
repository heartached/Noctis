using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services.Plugins;
using Noctis.Services.Server;
using Noctis.Services;
using Noctis.Services.Loon;
using Noctis.Services.MediaServer;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the unified Settings page (tabbed modal).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IPersistenceService _persistence;
    private readonly ILibraryService _library;
    private readonly IPlayHistoryService _playHistory;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private IAudioPlayer? _audioPlayer;
    private PlayerViewModel? _player;
    private IDiscordPresenceService? _discord;
    private LoonClient? _loon;
    private ILastFmService? _lastFm;
    private IListenBrainzService? _listenBrainz;
    private IMediaServerService? _mediaServer;
    private UpdateService? _updateService;
    private CancellationTokenSource? _updateCts;
    private string? _downloadedInstallerPath;
    private CancellationTokenSource? _lastFmAuthCts;
    private bool _settingsLoaded;

    /// <summary>Action → gesture map behind Settings › Shortcuts and MainWindow's key dispatch.</summary>
    public ShortcutService ShortcutService { get; }

    /// <summary>Rows behind the Shortcuts tab.</summary>
    public ShortcutsSettingsViewModel Shortcuts { get; }
    private bool _suspendSettingPersistence;
    private CancellationTokenSource? _eqSaveDebounceCts;
    private CancellationTokenSource? _scanStatusClearCts;
    // Drives library-scan supersession: a folder add/remove cancels any in-flight
    // scan and re-runs against the latest folder set instead of being dropped.
    private CancellationTokenSource? _scanCts;
    private Task _scanInFlight = Task.CompletedTask;

    [ObservableProperty] private int _mediaFoldersScrollRequest;

    // ── Tabs ──
    // The Settings modal is split into named tabs; exactly one tab panel is visible at a time.
    public const string TabGeneral = "General";
    public const string TabAppearance = "Appearance";
    public const string TabAudio = "Audio";
    public const string TabLibrary = "Library";
    public const string TabShortcuts = "Shortcuts";
    public const string TabStatistics = "Statistics";
    public const string TabIntegrations = "Integrations";
    public const string TabPlugins = "Plugins";
    public const string TabAbout = "About";

    [ObservableProperty] private string _selectedSettingsTab = TabGeneral;

    /// <summary>Rail entries in display order; IsSelected mirrors <see cref="SelectedSettingsTab"/>.</summary>
    public IReadOnlyList<SettingsSection> Sections { get; } = new[]
    {
        new SettingsSection(TabGeneral, "SettingsIcon") { IsSelected = true },
        new SettingsSection(TabAppearance, "PaletteIcon"),
        new SettingsSection(TabAudio, "SpeakerHighIcon"),
        new SettingsSection(TabLibrary, "FolderIcon"),
        new SettingsSection(TabShortcuts, "KeyboardIcon"),
        new SettingsSection(TabIntegrations, "PlugIcon"),
        new SettingsSection(TabPlugins, "PuzzleIcon"),
        new SettingsSection(TabStatistics, "StatisticsIcon"),
        new SettingsSection(TabAbout, "InfoIcon"),
    };

    /// <summary>Text in the rail's search box. The view applies it to the card index.</summary>
    [ObservableProperty] private string _searchQuery = string.Empty;

    public bool IsGeneralTabSelected => SelectedSettingsTab == TabGeneral;
    public bool IsAppearanceTabSelected => SelectedSettingsTab == TabAppearance;
    public bool IsAudioTabSelected => SelectedSettingsTab == TabAudio;
    public bool IsLibraryTabSelected => SelectedSettingsTab == TabLibrary;
    public bool IsShortcutsTabSelected => SelectedSettingsTab == TabShortcuts;
    public bool IsStatisticsTabSelected => SelectedSettingsTab == TabStatistics;
    public bool IsIntegrationsTabSelected => SelectedSettingsTab == TabIntegrations;
    public bool IsPluginsTabSelected => SelectedSettingsTab == TabPlugins;
    public bool IsPluginsTabVisible => IsPluginsTabSelected;
    public bool IsAboutTabSelected => SelectedSettingsTab == TabAbout;

    // ── Plugins tab ──
    /// <summary>The plugin host, attached by MainWindowViewModel once the player exists.</summary>
    [ObservableProperty] private PluginHost? _plugins;

    /// <summary>True once settings are read from disk; the plugin host waits for this so the disabled list is honoured.</summary>
    public bool IsSettingsLoaded => _settingsLoaded;
    public event EventHandler? SettingsLoaded;

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        if (Plugins is null) return;
        try { Directory.CreateDirectory(Plugins.PluginsDirectory); } catch { /* shown by the OS if it fails */ }
        PlatformHelper.OpenUrl(Plugins.PluginsDirectory);
    }

    [RelayCommand]
    private void ReloadPlugins() => Plugins?.LoadAll();

    public bool IsGeneralTabVisible => IsGeneralTabSelected;
    public bool IsAppearanceTabVisible => IsAppearanceTabSelected;
    public bool IsAudioTabVisible => IsAudioTabSelected;
    public bool IsLibraryTabVisible => IsLibraryTabSelected;
    public bool IsShortcutsTabVisible => IsShortcutsTabSelected;
    public bool IsStatisticsTabVisible => IsStatisticsTabSelected;
    public bool IsIntegrationsTabVisible => IsIntegrationsTabSelected;
    public bool IsAboutTabVisible => IsAboutTabSelected;

    partial void OnSelectedSettingsTabChanged(string value)
    {
        foreach (var section in Sections)
            section.IsSelected = section.Key == value;

        OnPropertyChanged(nameof(IsGeneralTabSelected));
        OnPropertyChanged(nameof(IsAppearanceTabSelected));
        OnPropertyChanged(nameof(IsAudioTabSelected));
        OnPropertyChanged(nameof(IsLibraryTabSelected));
        OnPropertyChanged(nameof(IsShortcutsTabSelected));
        OnPropertyChanged(nameof(IsStatisticsTabSelected));
        OnPropertyChanged(nameof(IsIntegrationsTabSelected));
        OnPropertyChanged(nameof(IsPluginsTabSelected));
        OnPropertyChanged(nameof(IsPluginsTabVisible));
        OnPropertyChanged(nameof(IsAboutTabSelected));
        OnPropertyChanged(nameof(IsGeneralTabVisible));
        OnPropertyChanged(nameof(IsAppearanceTabVisible));
        OnPropertyChanged(nameof(IsAudioTabVisible));
        OnPropertyChanged(nameof(IsLibraryTabVisible));
        OnPropertyChanged(nameof(IsShortcutsTabVisible));
        OnPropertyChanged(nameof(IsStatisticsTabVisible));
        OnPropertyChanged(nameof(IsIntegrationsTabVisible));
        OnPropertyChanged(nameof(IsAboutTabVisible));

        // Transient validation hints (e.g. ListenBrainz "Token required") are tied to
        // a Connect click, not to persisted state — drop them when navigating tabs so
        // they don't reappear when returning to Integrations.
        ListenBrainzError = "";
        ClearTransientServerError();

        // Play counts change during the session without a LibraryUpdated event,
        // so recompute stats whenever the Statistics tab is opened.
        if (value == TabStatistics)
        {
            RefreshLibraryStats();
            // TotalPlaylists has no other writer, so without this the Statistics tab
            // reported "0 playlists" to every user who had any.
            _ = RefreshPlaylistCountAsync();
        }

        // The GitHub release list is otherwise fetched only when Developer Mode
        // flips on (once at startup for users who keep it enabled), so a release
        // published while the app runs would never appear or claim the "Latest"
        // pill. Re-fetch whenever the About tab opens with the version manager
        // visible; the old rows stay on screen until the new list arrives.
        if (value == TabAbout && DeveloperMode)
            _ = RefreshReleasesAsync();
    }

    [RelayCommand]
    private void SelectSettingsTab(string tab) => SelectedSettingsTab = tab;

    // ── Profile ──
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _profileAvatarPath = string.Empty;

    partial void OnProfileNameChanged(string value) { if (_settingsLoaded) QueueSettingsSave(); }
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

    /// <summary>Curated swatches from App.AccentPresets; the active one is highlighted in the UI.</summary>
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCrossfade3s))]
    [NotifyPropertyChangedFor(nameof(IsCrossfade6s))]
    [NotifyPropertyChangedFor(nameof(IsCrossfade10s))]
    private double _crossfadeDuration = 6;

    // Preset chips beside the slider (3s / 6s / 10s); the slider keeps the fine control.
    public bool IsCrossfade3s => Math.Abs(CrossfadeDuration - 3) < 0.5;
    public bool IsCrossfade6s => Math.Abs(CrossfadeDuration - 6) < 0.5;
    public bool IsCrossfade10s => Math.Abs(CrossfadeDuration - 10) < 0.5;

    [RelayCommand]
    private void SetCrossfadePreset(string? seconds)
    {
        if (double.TryParse(seconds, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s))
            CrossfadeDuration = Math.Clamp(s, 1, 12);
    }

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
    /// <summary>Album pages tinted by the cover's edge colour (Appearance tab). Album
    /// detail view-models watch this and rebuild their background live.</summary>
    [ObservableProperty] private bool _albumPageTintEnabled = true;

    /// <summary>Persisted name of the now-playing artwork costume ("Cover", "CompactDisc",
    /// "Vinyl", "Cassette"). The Appearance picker binds the Is* flags below, the same
    /// shape as the Song Transitions style cards.</summary>
    [ObservableProperty] private string _nowPlayingArtworkStyle = ArtworkMediums.DefaultSetting;

    /// <summary>Typed view of <see cref="NowPlayingArtworkStyle"/> for the lyrics page's MediaArtwork.</summary>
    public ArtworkMedium NowPlayingArtworkMedium => ArtworkMediums.Parse(NowPlayingArtworkStyle);

    public bool IsArtworkStyleCover { get => NowPlayingArtworkMedium == ArtworkMedium.Cover; set { if (value) NowPlayingArtworkStyle = nameof(ArtworkMedium.Cover); } }
    public bool IsArtworkStyleCompactDisc { get => NowPlayingArtworkMedium == ArtworkMedium.CompactDisc; set { if (value) NowPlayingArtworkStyle = nameof(ArtworkMedium.CompactDisc); } }
    public bool IsArtworkStyleVinyl { get => NowPlayingArtworkMedium == ArtworkMedium.Vinyl; set { if (value) NowPlayingArtworkStyle = nameof(ArtworkMedium.Vinyl); } }
    public bool IsArtworkStyleCassette { get => NowPlayingArtworkMedium == ArtworkMedium.Cassette; set { if (value) NowPlayingArtworkStyle = nameof(ArtworkMedium.Cassette); } }

    /// <summary>Mini player design picker (Appearance): the classic resizable card or one
    /// of the fixed community designs. The mini player VM follows this live.</summary>
    [ObservableProperty] private string _miniPlayerStyle = MiniPlayerStyles.DefaultSetting;

    public MiniPlayerStyle MiniPlayerStyleMode => MiniPlayerStyles.Parse(MiniPlayerStyle);

    public bool IsMiniStyleClassic { get => MiniPlayerStyleMode == Models.MiniPlayerStyle.Classic; set { if (value) MiniPlayerStyle = nameof(Models.MiniPlayerStyle.Classic); } }
    public bool IsMiniStylePill { get => MiniPlayerStyleMode == Models.MiniPlayerStyle.Pill; set { if (value) MiniPlayerStyle = nameof(Models.MiniPlayerStyle.Pill); } }
    public bool IsMiniStyleSleeve { get => MiniPlayerStyleMode == Models.MiniPlayerStyle.Sleeve; set { if (value) MiniPlayerStyle = nameof(Models.MiniPlayerStyle.Sleeve); } }

    /// <summary>Player Island Buttons (Appearance): the podcast/audiobook extras on the bar.
    /// Mirrored onto PlayerViewModel by ApplyPlayerSettings, like the marquee flags.</summary>
    [ObservableProperty] private bool _playbackBarShowSkipButtons;
    [ObservableProperty] private int _playbackBarSkipSeconds = 15;
    [ObservableProperty] private bool _playbackBarShowPlaybackSpeed;
    [ObservableProperty] private bool _playbackBarShowSleepTimer;
    [ObservableProperty] private bool _playbackBarShowShuffle;

    public bool IsSkipSeconds10 { get => PlaybackBarSkipSeconds == 10; set { if (value) PlaybackBarSkipSeconds = 10; } }
    public bool IsSkipSeconds15 { get => PlaybackBarSkipSeconds == 15; set { if (value) PlaybackBarSkipSeconds = 15; } }
    public bool IsSkipSeconds30 { get => PlaybackBarSkipSeconds == 30; set { if (value) PlaybackBarSkipSeconds = 30; } }

    /// <summary>Persisted name of the Cover Flow layout ("Carousel", "Cascade", "Collage").
    /// Two-way with CoverFlowViewModel.Layout via MainWindowViewModel, so the top-bar pill
    /// segment and the Appearance picker stay in step.</summary>
    [ObservableProperty] private string _coverFlowLayout = CoverFlowLayouts.DefaultSetting;

    public CoverFlowLayout CoverFlowLayoutMode
    {
        get => CoverFlowLayouts.Parse(CoverFlowLayout);
        set => CoverFlowLayout = value.ToString();
    }

    public bool IsCoverFlowCarousel { get => CoverFlowLayoutMode == Models.CoverFlowLayout.Carousel; set { if (value) CoverFlowLayout = nameof(Models.CoverFlowLayout.Carousel); } }
    public bool IsCoverFlowCascade { get => CoverFlowLayoutMode == Models.CoverFlowLayout.Cascade; set { if (value) CoverFlowLayout = nameof(Models.CoverFlowLayout.Cascade); } }
    public bool IsCoverFlowCollage { get => CoverFlowLayoutMode == Models.CoverFlowLayout.Collage; set { if (value) CoverFlowLayout = nameof(Models.CoverFlowLayout.Collage); } }

    /// <summary>Explicit Content toggle (Audio tab). Off = explicit tracks never play
    /// automatically; see <see cref="AppSettings.AllowExplicitContent"/>.</summary>
    [ObservableProperty] private bool _allowExplicitContent = true;
    [ObservableProperty] private bool _lyricsFlowingLightEnabled;
    /// <summary>Live spectrum visualizer behind the lyrics page (Appearance tab).</summary>
    [ObservableProperty] private bool _lyricsVisualizerEnabled;
    /// <summary>Visualizer look, stored as a <see cref="VisualizerStyle"/> name; the picker
    /// binds the Is* flags below like the Now Playing Artwork cards.</summary>
    [ObservableProperty] private string _lyricsVisualizerStyle = VisualizerStyles.DefaultSetting;
    /// <summary>Visualizer bars take the artwork's colour (Appearance tab).</summary>
    [ObservableProperty] private bool _lyricsVisualizerArtworkColor = true;

    /// <summary>One entry of the Language picker: "" = follow the OS, else a shipped culture.</summary>
    public sealed record LanguageOption(string Code, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>System language first, then every shipped translation by its native name.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = BuildLanguageOptions();

    private static IReadOnlyList<LanguageOption> BuildLanguageOptions()
    {
        var list = new List<LanguageOption> { new(Loc.SystemLanguage, Loc.T("Settings.Language.System")) };
        foreach (var code in Loc.Supported)
        {
            var native = System.Globalization.CultureInfo.GetCultureInfo(code).NativeName;
            list.Add(new(code, char.ToUpperInvariant(native[0]) + native[1..]));
        }
        return list;
    }

    [ObservableProperty] private LanguageOption? _languageChoice;

    partial void OnLanguageChoiceChanged(LanguageOption? value)
    {
        if (value is null) return;
        _settings.Language = value.Code;
        Loc.Instance.SetCulture(value.Code);
        if (_settingsLoaded) _ = SaveAsync();
    }

    [RelayCommand]
    private void OpenTranslationHelp() => PlatformHelper.OpenUrl("https://crowdin.com/project/noctis");

    public VisualizerStyle LyricsVisualizerStyleMode => VisualizerStyles.Parse(LyricsVisualizerStyle);
    public bool IsVisualizerStyleBars { get => LyricsVisualizerStyleMode == VisualizerStyle.Bars; set { if (value) LyricsVisualizerStyle = nameof(VisualizerStyle.Bars); } }
    public bool IsVisualizerStyleMirror { get => LyricsVisualizerStyleMode == VisualizerStyle.Mirror; set { if (value) LyricsVisualizerStyle = nameof(VisualizerStyle.Mirror); } }
    public bool IsVisualizerStyleWave { get => LyricsVisualizerStyleMode == VisualizerStyle.Wave; set { if (value) LyricsVisualizerStyle = nameof(VisualizerStyle.Wave); } }
    /// <summary>Looping video/GIF behind the lyrics page (Appearance tab); empty = none.
    /// The file lives under the data root — SettingsView copies the pick there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLyricsBackgroundMedia))]
    [NotifyPropertyChangedFor(nameof(LyricsBackgroundMediaName))]
    private string _lyricsBackgroundMediaPath = string.Empty;
    public bool HasLyricsBackgroundMedia => !string.IsNullOrEmpty(LyricsBackgroundMediaPath);
    public string LyricsBackgroundMediaName =>
        string.IsNullOrEmpty(LyricsBackgroundMediaPath) ? "None" : Path.GetFileName(LyricsBackgroundMediaPath);
    [ObservableProperty] private bool _lyricsFullScreenFocusEnabled;
    [ObservableProperty] private bool _lyricsJoinSplitWords;

    /// <summary>Minimize hides the main window to the system tray.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    /// <summary>Close hides the main window to the system tray instead of exiting.</summary>
    [ObservableProperty] private bool _closeToTray;

    /// <summary>Launch Noctis automatically when the user logs into the computer.
    /// The OS entry (registry / LaunchAgent / autostart .desktop) is the source of
    /// truth — there's no AppSettings copy to drift out of sync.</summary>
    [ObservableProperty] private bool _launchAtStartup;

    /// <summary>When launched at login, start hidden in the tray (only meaningful when
    /// LaunchAtStartup is on; honored at startup only if the tray is available).</summary>
    [ObservableProperty] private bool _startMinimizedToTray;

    /// <summary>Restore the last-played track (paused) into the playbar on reopen.</summary>
    [ObservableProperty] private bool _restoreLastTrackOnStartup = true;

    partial void OnMinimizeToTrayChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnCloseToTrayChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnRestoreLastTrackOnStartupChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    /// <summary>Shown under the launch-at-login toggle when the OS refused the change.</summary>
    [ObservableProperty] private string _launchAtStartupError = string.Empty;

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (!_settingsLoaded || _suppressLaunchAtStartupHandler) return;
        ApplyLaunchAtStartup(value, StartMinimizedToTray);
    }

    private bool _suppressLaunchAtStartupHandler;

    /// <summary>
    /// Writes the OS autostart entry and re-asserts the toggle from what the OS actually
    /// reports. The result used to be discarded entirely, so under a locked-down HKCU, a
    /// read-only ~/.config/autostart or an unbundled macOS run the switch stayed ON while
    /// nothing had been registered.
    /// </summary>
    private void ApplyLaunchAtStartup(bool value, bool startMinimized)
    {
        var ok = Helpers.StartupHelper.SetEnabled(value, startMinimized);
        var actual = Helpers.StartupHelper.IsEnabled();

        if (ok && actual == value)
        {
            LaunchAtStartupError = string.Empty;
            return;
        }

        LaunchAtStartupError = value
            ? "Couldn't register Noctis to launch at login. Check the app has permission to write its startup entry."
            : "Couldn't remove the launch-at-login entry.";

        if (actual != value)
        {
            _suppressLaunchAtStartupHandler = true;
            try { LaunchAtStartup = actual; }
            finally { _suppressLaunchAtStartupHandler = false; }
        }
    }

    partial void OnStartMinimizedToTrayChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _ = SaveAsync();
        // Re-register so the autostart command's --minimized flag matches the new value
        // (only when autostart is actually on; the toggle is disabled in the UI otherwise).
        if (LaunchAtStartup) ApplyLaunchAtStartup(true, value);
    }

    // ── Songs page optional columns ──

    [ObservableProperty] private bool _showArtworkColumn = true;
    [ObservableProperty] private bool _showGenreColumn = true;
    [ObservableProperty] private bool _showRatingColumn = true;
    [ObservableProperty] private bool _showBpmColumn;
    [ObservableProperty] private bool _showBitrateColumn;
    [ObservableProperty] private bool _showSampleRateColumn;

    // Formerly always-on columns; hideable since the chooser moved into View Options.
    [ObservableProperty] private bool _showTimeColumn = true;
    [ObservableProperty] private bool _showArtistColumn = true;
    [ObservableProperty] private bool _showAlbumColumn = true;
    [ObservableProperty] private bool _showFavoritesColumn = true;
    [ObservableProperty] private bool _showPlaysColumn = true;

    partial void OnShowArtworkColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowGenreColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowRatingColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowBpmColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowBitrateColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowSampleRateColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowTimeColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowArtistColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowAlbumColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowFavoritesColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnShowPlaysColumnChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }

    // ── Songs page sort / filter, Albums grid sort ──
    //
    // Persisted view state rather than user-facing Settings toggles: nothing in
    // SettingsView binds these. They live here because SettingsViewModel owns the
    // AppSettings round-trip, and the Songs/Albums view models read and write them
    // through it — the same route the column flags already take.

    [ObservableProperty] private string _songsSortColumn = "Date Added";
    [ObservableProperty] private bool _songsSortAscending;
    [ObservableProperty] private bool _songsShowOnlyFavorites;
    [ObservableProperty] private string _albumSortMode = "default";
    [ObservableProperty] private bool _albumSortAscending = true;
    [ObservableProperty] private string _artistSortMode = "name";
    [ObservableProperty] private bool _artistSortAscending = true;

    partial void OnSongsSortColumnChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnSongsSortAscendingChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnSongsShowOnlyFavoritesChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnAlbumSortModeChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnAlbumSortAscendingChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnArtistSortModeChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnArtistSortAscendingChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }

    // ── Home section collapse state ──
    //
    // Same arrangement as the Songs/Albums view state above: persisted UI state, not a
    // Settings toggle. HomeViewModel reads and writes these through this view model.

    [ObservableProperty] private bool _homeTopSongsExpanded = true;
    [ObservableProperty] private bool _homeTopArtistsExpanded = true;
    [ObservableProperty] private bool _homeRecentlyPlayedExpanded = true;
    [ObservableProperty] private bool _homeTimeRotationExpanded = true;
    [ObservableProperty] private bool _homeHeavyRotationExpanded = true;
    [ObservableProperty] private bool _homeRediscoveredExpanded = true;

    partial void OnHomeTopSongsExpandedChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnHomeTopArtistsExpandedChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnHomeRecentlyPlayedExpandedChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnHomeTimeRotationExpandedChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnHomeHeavyRotationExpandedChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }
    partial void OnHomeRediscoveredExpandedChanged(bool value) { if (_settingsLoaded) _ = SaveAsync(); }

    // ── Noctis server (OpenSubsonic API for phones / other clients) ──

    private NoctisServer? _noctisServer;
    private ServerUserStore? _serverUsers;
    private string ServerDataDirectory => Path.Combine(_persistence.DataDirectory, "server");
    private ServerUserStore ServerUsers => _serverUsers ??= new ServerUserStore(Path.Combine(ServerDataDirectory, "users.db"));

    [ObservableProperty] private bool _noctisServerEnabled;
    [ObservableProperty] private int _noctisServerPort = 4747;
    /// <summary>https://ip:port while running; empty when off.</summary>
    [ObservableProperty] private string _noctisServerUrl = string.Empty;
    /// <summary>SHA-256 fingerprint of the server certificate — what a phone pins when it accepts the self-signed cert.</summary>
    [ObservableProperty] private string _noctisServerFingerprint = string.Empty;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _noctisServerQr;
    [ObservableProperty] private bool _noctisServerStartFailed;
    [ObservableProperty] private string _noctisServerError = string.Empty;
    [ObservableProperty] private bool _noctisServerClientSeen;
    [ObservableProperty] private string _noctisServerLastClient = string.Empty;
    private int _noctisServerClientGeneration;
    [ObservableProperty] private bool _noctisServerUrlCopied;

    public ObservableCollection<ServerUser> ServerUsersList { get; } = new();
    [ObservableProperty] private string _newServerUserName = string.Empty;
    [ObservableProperty] private string _newServerUserPassword = string.Empty;
    /// <summary>The API key just issued — shown once, cleared when the user leaves the tab.</summary>
    [ObservableProperty] private string _issuedApiKey = string.Empty;
    [ObservableProperty] private string _issuedApiKeyUser = string.Empty;
    [ObservableProperty] private string _serverUserError = string.Empty;
    public bool HasIssuedApiKey => IssuedApiKey.Length > 0;
    public bool HasServerUsers => ServerUsersList.Count > 0;
    partial void OnIssuedApiKeyChanged(string value) => OnPropertyChanged(nameof(HasIssuedApiKey));

    partial void OnNoctisServerEnabledChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveAsync();
        _ = UpdateNoctisServerStateAsync();
    }

    partial void OnNoctisServerPortChanged(int value)
    {
        if (!_settingsLoaded) return;
        _settings.NoctisServerPort = value;
        _ = SaveAsync();
        if (NoctisServerEnabled) _ = UpdateNoctisServerStateAsync(restart: true);
    }

    private async Task UpdateNoctisServerStateAsync(bool restart = false)
    {
        try
        {
            if (restart && _noctisServer is not null) await _noctisServer.StopAsync();

            if (!NoctisServerEnabled)
            {
                if (_noctisServer is not null) await _noctisServer.StopAsync();
                NoctisServerUrl = string.Empty;
                NoctisServerStartFailed = false;
                NoctisServerError = string.Empty;
                _noctisServerClientGeneration++;
                NoctisServerClientSeen = false;
                SetNoctisServerQr(null);
                return;
            }

            RefreshServerUsers();
            if (_noctisServer is null)
            {
                var adapter = new LibraryServerAdapter(_library, _persistence, _playHistory,
                    () => App.Services?.GetService<MainWindowViewModel>()?.Sidebar.LoadPlaylistsAsync() ?? Task.CompletedTask);
                _noctisServer = new NoctisServer(adapter, ServerUsers, UpdateService.CurrentVersionDisplay);
                _noctisServer.ClientAuthenticated += (_, user) => Dispatcher.UIThread.Post(() => OnNoctisServerClient(user));
            }
            if (!_noctisServer.IsRunning)
            {
                var cert = await Task.Run(() => ServerCertificate.LoadOrCreate(ServerDataDirectory));
                NoctisServerFingerprint = ServerCertificate.Fingerprint(cert);
                var port = _settings.NoctisServerPort is >= 1024 and <= 65535 ? _settings.NoctisServerPort : 4747;
                await _noctisServer.StartAsync(port, cert);
            }
            var ip = WebRemoteServer.GetLocalAddress() ?? "<this-pc-ip>";
            NoctisServerUrl = $"https://{ip}:{_noctisServer.Port}";
            NoctisServerStartFailed = false;
            NoctisServerError = string.Empty;
            SetNoctisServerQr(QrCodeBitmap.TryRender(NoctisServerUrl));
        }
        catch (Exception ex)
        {
            NoctisServerStartFailed = true;
            NoctisServerError = ex.Message;
            NoctisServerUrl = string.Empty;
            SetNoctisServerQr(null);
            DebugLogger.Error(DebugLogger.Category.Error, "Server", $"start failed: {ex.Message}");
        }
    }

    /// <summary>App exit: stop listening so the port is released and in-flight streams end cleanly.</summary>
    public async Task StopNoctisServerAsync()
    {
        if (_noctisServer is null) return;
        try { await _noctisServer.StopAsync(); }
        catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.Error, "Server", $"stop: {ex.Message}"); }
    }

    private void OnNoctisServerClient(string user)
    {
        NoctisServerClientSeen = true;
        NoctisServerLastClient = user;
        var generation = ++_noctisServerClientGeneration;
        _ = Task.Delay(WebRemotePhoneQuietMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _noctisServerClientGeneration) NoctisServerClientSeen = false;
        }));
    }

    private void SetNoctisServerQr(Avalonia.Media.Imaging.Bitmap? qr)
    {
        var old = NoctisServerQr;
        NoctisServerQr = qr;
        old?.Dispose();
    }

    private void RefreshServerUsers()
    {
        try
        {
            ServerUsersList.Clear();
            foreach (var u in ServerUsers.List()) ServerUsersList.Add(u);
            OnPropertyChanged(nameof(HasServerUsers));
        }
        catch (Exception ex) { ServerUserError = ex.Message; }
    }

    [RelayCommand]
    private void AddServerUser()
    {
        ServerUserError = string.Empty;
        try
        {
            ServerUsers.Create(NewServerUserName, NewServerUserPassword, isAdmin: ServerUsersList.Count == 0);
            var key = ServerUsers.RegenerateApiKey(NewServerUserName.Trim());
            IssuedApiKeyUser = NewServerUserName.Trim();
            IssuedApiKey = key;
            NewServerUserName = string.Empty;
            NewServerUserPassword = string.Empty;
            RefreshServerUsers();
        }
        catch (Exception ex) { ServerUserError = ex.Message; }
    }

    [RelayCommand]
    private void DeleteServerUser(ServerUser user)
    {
        ServerUserError = string.Empty;
        try
        {
            ServerUsers.Delete(user.Name);
            if (IssuedApiKeyUser == user.Name) { IssuedApiKey = string.Empty; IssuedApiKeyUser = string.Empty; }
            RefreshServerUsers();
        }
        catch (Exception ex) { ServerUserError = ex.Message; }
    }

    [RelayCommand]
    private void RegenerateServerApiKey(ServerUser user)
    {
        ServerUserError = string.Empty;
        try
        {
            IssuedApiKey = ServerUsers.RegenerateApiKey(user.Name);
            IssuedApiKeyUser = user.Name;
            RefreshServerUsers();
        }
        catch (Exception ex) { ServerUserError = ex.Message; }
    }

    [RelayCommand]
    private void HideIssuedApiKey() { IssuedApiKey = string.Empty; IssuedApiKeyUser = string.Empty; }

    [RelayCommand]
    private async Task CopyNoctisServerUrlAsync()
    {
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(NoctisServerUrl); } catch { return; }
        NoctisServerUrlCopied = true;
        await Task.Delay(1500);
        NoctisServerUrlCopied = false;
    }

    [RelayCommand]
    private async Task CopyIssuedApiKeyAsync()
    {
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null || IssuedApiKey.Length == 0) return;
        try { await clipboard.SetTextAsync(IssuedApiKey); } catch { }
    }

    // ── Web remote ──

    private WebRemoteServer? _webRemote;

    /// <summary>Local-network web remote (phone control page). Off by default.</summary>
    [ObservableProperty] private bool _webRemoteEnabled;

    /// <summary>Display URL for the running remote, or empty when off.</summary>
    [ObservableProperty] private string _webRemoteUrl = string.Empty;

    /// <summary>QR code for <see cref="WebRemoteUrl"/>, or null when the remote is off
    /// or failed to start. Saves typing the address on the phone (Discord request).</summary>
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _webRemoteQr;

    /// <summary>Token-free URL for on-screen display. The full auth-bearing URL stays
    /// off the card — a settings screenshot leaked a live token on Discord — and is
    /// carried by the QR code, the copy button and the enlarge flyout instead
    /// (same reasoning as DebugLog's no-auth-URLs rule).</summary>
    [ObservableProperty] private string _webRemoteDisplayUrl = string.Empty;

    /// <summary>True when the remote is enabled but the server threw on start —
    /// drives the error row (WebRemoteUrl then holds the failure text).</summary>
    [ObservableProperty] private bool _webRemoteStartFailed;

    /// <summary>True briefly after the remote URL is copied — inline "Copied!".</summary>
    [ObservableProperty] private bool _webRemoteUrlCopied;

    /// <summary>True while a phone is actively using the remote — flips the card's
    /// status line from "waiting" to "connected", so a scan that silently goes
    /// nowhere (wrong network, firewall) is visible as a problem. The remote page
    /// polls /api/status every 2 s while open, so requests going quiet means the
    /// phone left: reverts after ~3 missed polls instead of latching forever.</summary>
    [ObservableProperty] private bool _webRemotePhoneSeen;

    /// <summary>Guard for the phone-seen quiet-window reset (same idiom as the search
    /// generation counters): bumped on every authorized request, so only the reset
    /// scheduled by the newest request may flip the flag back off.</summary>
    private int _webRemotePhoneSeenGeneration;

    private const int WebRemotePhoneQuietMs = 6500;

    private void OnWebRemoteClientConnected()
    {
        WebRemotePhoneSeen = true;
        var generation = ++_webRemotePhoneSeenGeneration;
        _ = ResetWebRemotePhoneSeenAfterQuietAsync(generation);
    }

    private async Task ResetWebRemotePhoneSeenAfterQuietAsync(int generation)
    {
        await Task.Delay(WebRemotePhoneQuietMs);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation == _webRemotePhoneSeenGeneration)
                WebRemotePhoneSeen = false;
        });
    }

    /// <summary>Copies the full remote URL (including the access key).</summary>
    [RelayCommand]
    private async Task CopyWebRemoteUrlAsync()
    {
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null) return;

        try { await clipboard.SetTextAsync(WebRemoteUrl); } catch { return; }

        WebRemoteUrlCopied = true;
        await Task.Delay(1500);
        WebRemoteUrlCopied = false;
    }

    private void SetWebRemoteQr(Avalonia.Media.Imaging.Bitmap? qr)
    {
        var old = WebRemoteQr;
        WebRemoteQr = qr;
        // Dispose after the swap so a bound Image never paints a disposed bitmap.
        old?.Dispose();
    }

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
                if (_webRemote == null)
                {
                    _webRemote = new WebRemoteServer(_player);
                    _webRemote.ClientConnected += (_, _) =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(OnWebRemoteClientConnected);
                }
                if (!_webRemote.IsRunning)
                {
                    try
                    {
                        _webRemote.Start(_settings.WebRemotePort);
                    }
                    catch (SocketException)
                    {
                        // The port was in use. This used to leave WebRemoteUrl reading
                        // "Failed to start: …" for the rest of the session with nothing
                        // the user could do about it — the port is settings.json-only.
                        // Bind an ephemeral port instead and show the one actually bound.
                        _webRemote.Start(0);
                    }
                }
                var ip = WebRemoteServer.GetLocalAddress() ?? "<this-pc-ip>";
                WebRemoteUrl = $"http://{ip}:{_webRemote.Port}/?k={_webRemote.Token}";
                WebRemoteDisplayUrl = $"http://{ip}:{_webRemote.Port}";
                WebRemoteStartFailed = false;
                WebRemotePhoneSeen = false;
                SetWebRemoteQr(Helpers.QrCodeBitmap.TryRender(WebRemoteUrl));
            }
            catch (Exception ex)
            {
                WebRemoteUrl = $"Failed to start: {ex.Message}";
                WebRemoteDisplayUrl = string.Empty;
                WebRemoteStartFailed = true;
                SetWebRemoteQr(null);
                DebugLogger.Error(DebugLogger.Category.Error, "WebRemote.StartFailed", ex.Message);
            }
        }
        else
        {
            _webRemote?.Stop();
            WebRemoteUrl = string.Empty;
            WebRemoteDisplayUrl = string.Empty;
            WebRemoteStartFailed = false;
            _webRemotePhoneSeenGeneration++; // cancel any pending quiet-window reset
            WebRemotePhoneSeen = false;
            SetWebRemoteQr(null);
        }
    }
    [ObservableProperty] private double _playbackBarBackgroundOpacity = 0.4;
    /// <summary>Player island width in DIPs (GitHub #50): the Appearance slider and the
    /// bar's edge-grip drag drive the same value. Mirrors <see cref="AppSettings.PlaybackBarWidth"/>.</summary>
    [ObservableProperty] private double _playbackBarIslandWidth = PlaybackBarDefaultWidth;
    public const double PlaybackBarDefaultWidth = 626;
    public const double PlaybackBarMinWidth = 340;
    public const double PlaybackBarMaxWidth = 1400;
    // True while a grip drag pushes its width into the slider property, so the
    // property handler doesn't turn around and re-persist the same value.
    private bool _syncingPlaybackBarWidth;
    /// <summary>Bound straight to the mini player card's fill brush (no player plumbing —
    /// the mini player's view model exposes this view model directly).</summary>
    [ObservableProperty] private double _miniPlayerBackgroundOpacity = 0.35;
    /// <summary>Album cover sizing for the Albums/Favorites grids. Auto = classic five per
    /// row; otherwise the grids derive their column count from the target size. The album
    /// and favorites view models react through this view model's PropertyChanged.</summary>
    [ObservableProperty] private bool _albumTileSizeAuto = true;
    [ObservableProperty] private double _albumTileTargetSize = 220;
    [ObservableProperty] private bool _sidebarHoverExpand = true;
    [ObservableProperty] private bool _sidebarAlwaysExpanded = false;
    /// <summary>Liquid Glass needs OS blur-behind (Acrylic/Mica/vibrancy). On Linux/X11
    /// none of those exist and the hint would degrade to a plain see-through window
    /// (issue #26), so the Settings card is hidden there (same pattern as
    /// <see cref="IsExclusiveAudioSupported"/>) and MainWindow ignores the value.</summary>
    public bool IsLiquidGlassSupported => !OperatingSystem.IsLinux();
    /// <summary>Taskbar progress rides ITaskbarList3, which only exists on Windows.</summary>
    public bool IsTaskbarProgressSupported => OperatingSystem.IsWindows();

    // ── File types (Windows "Open with" / Default apps registration) ──
    public bool IsFileAssociationSupported => OperatingSystem.IsWindows();
    [ObservableProperty] private bool _isRegisteredForAudioFiles;
    [ObservableProperty] private string _fileTypesStatus = string.Empty;

    private static string? CurrentExePath => Environment.ProcessPath;

    public void RefreshFileAssociationState()
    {
        if (!OperatingSystem.IsWindows() || CurrentExePath is not { } exe) { IsRegisteredForAudioFiles = false; return; }
        IsRegisteredForAudioFiles = WindowsFileAssociations.IsRegistered(exe);
        if (!IsRegisteredForAudioFiles && !_fileAssociationRepointAttempted)
        {
            // A registration the user made earlier may point at an exe that no longer
            // exists (app moved/renamed/updated in place). Quietly move it to this copy —
            // HKCU only, no prompt — so "Open with Noctis" keeps working. Off the UI
            // thread: the registry walk is a dozen key writes and SHChangeNotify.
            _fileAssociationRepointAttempted = true;
            _ = Task.Run(() =>
            {
                if (!WindowsFileAssociations.TryRepointToCurrentExe(exe)) return;
                DebugLogger.Info(DebugLogger.Category.UI, "FileTypes.Repoint", $"exe={exe}");
                Dispatcher.UIThread.Post(() => IsRegisteredForAudioFiles = WindowsFileAssociations.IsRegistered(exe));
            });
        }
    }

    /// <summary>Stores a user-picked lyrics background video/GIF: copies it into the data
    /// root (so the setting survives the source moving) and points the setting at the copy.
    /// Runs the copy off the UI thread — the picker admits large files on slow shares.</summary>
    public async Task SetLyricsBackgroundMediaAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return;
        var dir = Path.Combine(AppPaths.DataRoot, "lyrics_background");
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(dir, "background" + ext);
        await Task.Run(() =>
        {
            Directory.CreateDirectory(dir);
            // One background at a time: drop a previous pick with a different extension.
            foreach (var existing in Directory.EnumerateFiles(dir, "background.*"))
            {
                if (!string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(existing); } catch { }
                }
            }
            File.Copy(sourcePath, target, overwrite: true);
        });
        // Same path twice must still re-play the new file: bounce through empty so the
        // player-side property change fires and the lyrics backdrop restarts.
        if (string.Equals(LyricsBackgroundMediaPath, target, StringComparison.OrdinalIgnoreCase))
            LyricsBackgroundMediaPath = string.Empty;
        LyricsBackgroundMediaPath = target;
    }

    /// <summary>Removes the lyrics background video/GIF and its copy under the data root.</summary>
    [RelayCommand]
    private void ClearLyricsBackgroundMedia()
    {
        var path = LyricsBackgroundMediaPath;
        LyricsBackgroundMediaPath = string.Empty;
        if (string.IsNullOrEmpty(path)) return;
        _ = Task.Run(() => { try { File.Delete(path); } catch { } });
    }

    private bool _fileAssociationRepointAttempted;

    /// <summary>Registers (or unregisters) Noctis as an Open-with / Default-apps choice for
    /// its audio formats, then opens Windows' Default apps page so the user can pick it —
    /// modern Windows will not let an app assign itself the default.</summary>
    [RelayCommand]
    private void RegisterFileTypes()
    {
        if (!OperatingSystem.IsWindows() || CurrentExePath is not { } exe)
        {
            FileTypesStatus = "Only available on Windows.";
            return;
        }
        try
        {
            if (IsRegisteredForAudioFiles)
            {
                WindowsFileAssociations.Unregister();
                FileTypesStatus = "Noctis removed from the Open-with list.";
            }
            else
            {
                WindowsFileAssociations.Register(exe);
                FileTypesStatus = "Registered. Pick Noctis under Settings → Apps → Default apps, or right-click a song → Open with.";
                try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
            }
        }
        catch (Exception ex)
        {
            FileTypesStatus = $"Couldn't update file types: {ex.Message}";
        }
        RefreshFileAssociationState();
    }
    [ObservableProperty] private bool _taskbarProgressEnabled;
    [ObservableProperty] private bool _liquidGlassEnabled;
    [ObservableProperty] private bool _collapseAlbumEditions;
    [ObservableProperty] private bool _mergeFeaturedFromTitles = true;

    // ── Artist grouping (GitHub #51) ──

    /// <summary>Persisted name of the Artists-section grouping ("Artist" or "AlbumArtist").</summary>
    [ObservableProperty] private string _artistGroupMode = ArtistGroupModes.DefaultSetting;

    public bool IsArtistGroupByArtist
    {
        get => ArtistGroupModes.Parse(ArtistGroupMode) == Models.ArtistGroupMode.Artist;
        set { if (value) ArtistGroupMode = nameof(Models.ArtistGroupMode.Artist); }
    }

    public bool IsArtistGroupByAlbumArtist
    {
        get => ArtistGroupModes.Parse(ArtistGroupMode) == Models.ArtistGroupMode.AlbumArtist;
        set { if (value) ArtistGroupMode = nameof(Models.ArtistGroupMode.AlbumArtist); }
    }

    /// <summary>Separators that split a multi-artist tag into credited names. Edited as
    /// chips; every change re-tokenizes app-wide and regroups the artist index.</summary>
    public ObservableCollection<string> ArtistTagSeparators { get; } = new();

    /// <summary>Text of the "add separator" box on the Library tab.</summary>
    [ObservableProperty] private string _newArtistSeparator = string.Empty;

    // ── Lyrics Providers ──

    [ObservableProperty] private bool _lrcLibEnabled = true;
    [ObservableProperty] private bool _netEaseEnabled = true;

    // ── Metadata Providers ──
    [ObservableProperty] private bool _deezerEnabled = true;
    [ObservableProperty] private bool _musicBrainzEnabled = true;

    [ObservableProperty] private string _ffmpegPath = string.Empty;
    [ObservableProperty] private string _ffmpegStatus = string.Empty;

    [ObservableProperty] private string _externalOpenAppPath = string.Empty;

    public string[] ReplayGainModeOptions { get; } = { "Off", "Track", "Album", "Auto" };
    [ObservableProperty] private string _replayGainMode = "Off";
    [ObservableProperty] private double _replayGainPreampDb;
    /// <summary>On/off mirror of <see cref="ReplayGainMode"/> for the Settings toggle.</summary>
    [ObservableProperty] private bool _replayGainEnabled;
    private string _lastActiveReplayGainMode = "Auto";
    private bool _suppressRgNotify;

    [ObservableProperty] private bool _gaplessPlaybackEnabled = true;

    /// <summary>Autoplay: when the queue ends naturally, keep playing similar songs
    /// from the library. Off by default (new behavior-changing extras ship opt-in).</summary>
    [ObservableProperty] private bool _autoplayEnabled;

    // ── Audio analysis (background BPM/key detection) ──

    [ObservableProperty] private bool _bpmKeyAnalysisEnabled = true;
    [ObservableProperty] private bool _writeAnalysisToTags;

    // ── Exclusive mode (Windows WASAPI) ──

    public bool IsExclusiveAudioSupported => OperatingSystem.IsWindows();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseSongTransitions))]
    [NotifyPropertyChangedFor(nameof(SongTransitionsOpacity))]
    private bool _exclusiveAudioEnabled;

    /// <summary>Song transitions need two overlapping audio streams; Exclusive Mode is
    /// single-stream, so every transition style (AutoMix and Crossfade) is unavailable
    /// while it's active. Drives the Song Transitions card's enabled state.</summary>
    public bool CanUseSongTransitions => !ExclusiveAudioEnabled;

    /// <summary>Grays the Song Transitions card while it's unavailable (exclusive on).</summary>
    public double SongTransitionsOpacity => ExclusiveAudioEnabled ? 0.5 : 1.0;
    /// <summary>Live output-path status from the player ("Exclusive output active — 44.1 kHz / 24-bit",
    /// "Exclusive mode unavailable (device in use) — using shared output", ...).</summary>
    [ObservableProperty] private string _exclusiveAudioStatus = "";

    // ── Equalizer ──

    [ObservableProperty] private bool _equalizerEnabled = true;
    [ObservableProperty] private int _selectedEqPresetIndex = 1; // 0 = Custom, 1 = Flat, 2+ = VLC preset
    [ObservableProperty] private string _selectedEqPresetName = "Flat";
    /// <summary>EQ pre-amp in dB relative to native (0 = unchanged); rides presets and custom curves alike.</summary>
    [ObservableProperty] private double _eqPreampDb;

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
    /// <summary>Album line on the Discord card; see <see cref="AppSettings.DiscordShowAlbum"/>.</summary>
    [ObservableProperty] private bool _discordShowAlbum = true;
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

    // ── Media server ──
    // The editable fields below are typing state; the authoritative connected
    // state is _mediaServerConnection (token/user id inside), which is what
    // SyncToSettings persists. Credentials follow the ListenBrainz idiom: no
    // autosave, persisted only by the Connect/Disconnect commands.
    // Named presets rather than a protocol pair: Navidrome/Airsonic/Gonic users
    // shouldn't have to know they are "a Subsonic". Everything except Jellyfin
    // speaks the Subsonic protocol underneath (SourceType.Navidrome).
    public string[] MediaServerTypeOptions { get; } =
        { "Jellyfin", "Navidrome", "Airsonic", "Gonic", "Subsonic (other)" };
    [ObservableProperty] private string _mediaServerType = "Jellyfin";
    [ObservableProperty] private string _mediaServerUrl = "";
    [ObservableProperty] private string _mediaServerUsername = "";
    [ObservableProperty] private string _mediaServerPassword = "";
    [ObservableProperty] private bool _isMediaServerConnected;
    [ObservableProperty] private bool _isMediaServerBusy;
    [ObservableProperty] private string _mediaServerStatusText = "Not connected";
    /// <summary>True while the status line shows a failure — drives the red status styling.</summary>
    [ObservableProperty] private bool _hasMediaServerError;

    /// <summary>Switching preset starts a fresh form: stale credentials for another
    /// server would only produce a confusing failed connect.</summary>
    partial void OnMediaServerTypeChanged(string value)
    {
        if (!_settingsLoaded || IsMediaServerConnected) return;
        MediaServerUrl = string.Empty;
        MediaServerUsername = string.Empty;
        MediaServerPassword = string.Empty;
        MediaServerStatusText = "Not connected";
        HasMediaServerError = false;
    }

    /// <summary>The persisted server connection while connected; null otherwise.</summary>
    private SourceConnection? _mediaServerConnection;

    /// <summary>Raised after Connect/Disconnect so the shell can show/hide the Server section.</summary>
    public event EventHandler? MediaServerConnectionChanged;

    // ── Preferences ──

    [ObservableProperty] private bool _scanOnStartup = true;

    [ObservableProperty] private bool _watchFoldersEnabled = true;

    [ObservableProperty] private bool _useEmbeddedArtwork = true;

    [ObservableProperty] private string _organizePattern = "{AlbumArtist}/{Album}/{TrackNo} {Title}";
    [ObservableProperty] private string _organizeTargetRoot = string.Empty;

    // OrganizeFilesViewModel writes both of these back here when its dialog closes, but
    // nothing persisted them — unlike every other setting they had no change handler, so
    // they only reached disk if some unrelated save happened to run afterwards. Killing
    // the process before that lost the user's organize template.
    partial void OnOrganizePatternChanged(string value) { if (_settingsLoaded) QueueSettingsSave(); }
    partial void OnOrganizeTargetRootChanged(string value) { if (_settingsLoaded) QueueSettingsSave(); }

    // ── Library overview stats ──

    [ObservableProperty] private int _totalSongs;
    [ObservableProperty] private int _totalArtists;
    [ObservableProperty] private int _totalAlbums;
    [ObservableProperty] private int _totalPlaylists;

    [ObservableProperty] private string _totalFileSize = "0 MB";
    [ObservableProperty] private string _totalListeningTime = "0 min";

    // ── Listening statistics ──

    [ObservableProperty] private string _totalPlays = "0";
    [ObservableProperty] private string _timeListened = "0 min";
    [ObservableProperty] private string _avgTrackLength = "0:00";
    [ObservableProperty] private int _likedTracks;

    public BulkObservableCollection<StatItem> TopArtists { get; } = new();
    public BulkObservableCollection<StatItem> TopAlbums { get; } = new();

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckForUpdatesButtonText))]
    private bool _isCheckingForUpdate;

    /// <summary>True briefly after a manual check finds no newer release; drives the
    /// inline "You're up to date" label on the Check-for-Updates button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckForUpdatesButtonText))]
    private bool _isUpToDate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCheckForUpdatesButton))]
    [NotifyPropertyChangedFor(nameof(ShowInAppUpdateButton))]
    [NotifyPropertyChangedFor(nameof(ShowManualUpdateButton))]
    private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCheckForUpdatesButton))]
    private bool _isReadyToInstall;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private string _latestVersionTag = "";
    [ObservableProperty] private bool _isLatestPrerelease;
    [ObservableProperty] private bool _includePrereleaseUpdates;

    public bool ShowCheckForUpdatesButton => !IsUpdateAvailable && !IsReadyToInstall;

    /// <summary>Label for the Check-for-Updates pill, reflecting progress/result inline:
    /// "Checking..." while polling, "You're up to date" briefly when no update is found,
    /// otherwise the default call to action.</summary>
    public string CheckForUpdatesButtonText =>
        IsCheckingForUpdate ? "Checking..."
        : IsUpToDate ? "✓ Up to date"
        : "Update";

    /// <summary>Label for the update-available buttons, naming the target version
    /// when known (e.g. "Update to 1.2.8").</summary>
    public string UpdateButtonText => string.IsNullOrEmpty(LatestVersionTag)
        ? "Update available"
        : $"Update to {LatestVersionTag.TrimStart('v', 'V')}";

    /// <summary>True when this copy can update itself via the bundled installer
    /// (Inno install on Windows, or any non-Windows build). False for Scoop /
    /// portable copies, which update through their own channel.</summary>
    public bool CanInstallInApp => UpdateService.SupportsInAppUpdate;

    /// <summary>Shows the in-app "Update available" (download &amp; install) button.</summary>
    public bool ShowInAppUpdateButton => IsUpdateAvailable && CanInstallInApp;

    /// <summary>Shows the "Update available" button that opens GitHub for Scoop /
    /// portable copies, which can't safely use the in-app installer.</summary>
    public bool ShowManualUpdateButton => IsUpdateAvailable && !CanInstallInApp;

    /// <summary>Manager-specific update guidance for Scoop / portable copies
    /// (e.g. "Update with: scoop update noctis"); null for in-app-updatable builds.</summary>
    public string? ExternalUpdateHint => UpdateService.ExternalUpdateHint;

    // ── Events ──

    /// <summary>Fires when the theme changes so the App can update. Payload is the theme key.</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>Fires when the Liquid Glass appearance toggle changes so the main window
    /// can switch its transparency hint, acrylic backdrop and surface brushes.</summary>
    public event EventHandler<bool>? LiquidGlassChanged;

    /// <summary>Fires when "Keep sidebar expanded" changes so the main window can pin the
    /// sidebar open (or collapse it back to the icon rail) immediately.</summary>
    public event EventHandler<bool>? SidebarAlwaysExpandedChanged;

    /// <summary>Fires after a full settings reset so the shell can reload playlists, etc.</summary>
    public event EventHandler? SettingsReset;

    /// <summary>Fires once the persisted list view state (Songs sort/filter, Albums sort)
    /// has been read from disk, so the library view models can adopt it. They are built
    /// during startup, before the settings load completes.</summary>
    public event EventHandler? ViewStateLoaded;

    /// <summary>Fires when a media folder is added or removed so the Folders view can rebuild its tree.</summary>
    public event EventHandler? MusicFoldersChanged;

    /// <summary>Raised when the user asks to open the full standalone Statistics page
    /// from the Settings → Statistics tab. The shell closes the modal and navigates.</summary>
    public event EventHandler? OpenStatisticsRequested;

    public SettingsViewModel(IPersistenceService persistence, ILibraryService library, IPlayHistoryService playHistory,
        IMediaServerService? mediaServer = null, ShortcutService? shortcuts = null)
    {
        _persistence = persistence;
        _library = library;
        _playHistory = playHistory;
        _mediaServer = mediaServer;
        _settings = new AppSettings();

        // Rebindable keys live in the shared service (MainWindow matches against it); the
        // view-model only owns persistence. Saved through the same debounce as every toggle.
        ShortcutService = shortcuts ?? new ShortcutService();
        ShortcutService.Changed += (_, _) => { if (_settingsLoaded) QueueSettingsSave(); };
        Shortcuts = new ShortcutsSettingsViewModel(ShortcutService);

        _library.ScanProgress += (_, count) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ScanProgress = count;
                ScanStatusText = $"Scanning Library {count:N0}";
            });
        };

        // The library drops a configured root when it's gone from disk and contributes no
        // tracks. It now says so directly: this used to hang off LibraryUpdated and do a
        // full LoadSettingsAsync (file read + JSON parse + DPAPI unprotect) just to compare
        // the folder set — on every scan, drop-import, removal and metadata write.
        _library.MusicFoldersChanged += (_, folders) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var updated = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
                var displayed = new HashSet<string>(MusicFolders, StringComparer.OrdinalIgnoreCase);
                if (updated.SetEquals(displayed)) return;

                MusicFolders.Clear();
                foreach (var folder in folders)
                    MusicFolders.Add(folder);
                _settings.MusicFolders = folders;
                OnPropertyChanged(nameof(MediaFolderDisplay));
            });
        };

        // Keep the Developer Mode log view live while it's visible.
        DebugLog.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (DeveloperMode)
                DevLogText = ComposeDevLogText();
        });

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
        // Only surface the output-mode status while Exclusive Mode is enabled (it
        // describes the exclusive/fell-back-to-shared state). When exclusive is off
        // the line stays hidden. This also prevents a flicker: toggling exclusive
        // OFF clears the status synchronously (card shrinks), and without this gate
        // the async "Shared output…" notice would immediately re-populate it (card
        // grows again) — a visible shrink-then-grow that jolted the toggle/text.
        audioPlayer.OutputModeChanged += (_, status) =>
        {
            // Output-path transitions (exclusive engaged, fell back to shared,
            // sink rebuilt after a device error) are exactly what an audio bug
            // report needs, and they are rare — log them all.
            DebugLog.Write("Audio", $"Output path: {status}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ExclusiveAudioStatus = ExclusiveAudioEnabled ? status : "");
        };
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

    // ── TIDAL (playlist import sign-in) ───────────────────────
    private ITidalAuthService? _tidal;
    [ObservableProperty] private bool _isTidalConnected;
    [ObservableProperty] private bool _isTidalBusy;
    /// <summary>The card only shows in builds that carry a TIDAL client id.</summary>
    public bool IsTidalAvailable => TidalOAuth.IsConfigured;

    public void SetTidal(ITidalAuthService tidal)
    {
        _tidal = tidal;
        IsTidalConnected = tidal.IsConnected;
    }

    [RelayCommand]
    private async Task ConnectTidal()
    {
        if (_tidal is null || IsTidalBusy) return;
        IsTidalBusy = true;
        try { IsTidalConnected = await _tidal.LoginAsync(); }
        finally { IsTidalBusy = false; }
    }

    [RelayCommand]
    private void DisconnectTidal()
    {
        _tidal?.Disconnect();
        IsTidalConnected = false;
    }

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
            ShortcutService.Load(_settings);

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

            // The "System" tile was removed on 2026-08-31 (Gray is the default and the
            // app no longer follows the OS). Anyone still on it lands on Gray.
            if (storedTheme == "System")
            {
                storedTheme = "Gray";
                _settings.Theme = "Gray";
            }
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
            UseEmbeddedArtwork = _settings.UseEmbeddedArtwork;
            OrganizePattern = _settings.OrganizePattern;
            OrganizeTargetRoot = _settings.OrganizeTargetRoot;
            IncludePrereleaseUpdates = _settings.IncludePrereleaseUpdates;
            DeveloperMode = _settings.DeveloperMode;

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
            AlbumPageTintEnabled = _settings.AlbumPageTintEnabled;
            // Round-trip through Parse so a stale/unknown file value normalizes to "Cover".
            NowPlayingArtworkStyle = ArtworkMediums.Parse(_settings.NowPlayingArtworkStyle).ToString();
            CoverFlowLayout = CoverFlowLayouts.Parse(_settings.CoverFlowLayout).ToString();
            MiniPlayerStyle = MiniPlayerStyles.Parse(_settings.MiniPlayerStyle).ToString();
            PlaybackBarShowSkipButtons = _settings.PlaybackBarShowSkipButtons;
            PlaybackBarSkipSeconds = _settings.PlaybackBarSkipSeconds;
            PlaybackBarShowPlaybackSpeed = _settings.PlaybackBarShowPlaybackSpeed;
            PlaybackBarShowSleepTimer = _settings.PlaybackBarShowSleepTimer;
            PlaybackBarShowShuffle = _settings.PlaybackBarShowShuffle;
            PlaybackBarIslandWidth = _settings.PlaybackBarWidth;
            LyricsFlowingLightEnabled = _settings.LyricsFlowingLightEnabled;
            LyricsFlowingStyle = FlowingStyles.Normalize(_settings.LyricsFlowingStyle);
            LyricsVisualizerEnabled = _settings.LyricsVisualizerEnabled;
            LyricsVisualizerStyle = _settings.LyricsVisualizerStyle;
            LyricsVisualizerArtworkColor = _settings.LyricsVisualizerArtworkColor;
            LanguageChoice = LanguageOptions.FirstOrDefault(o => o.Code == (_settings.Language ?? string.Empty)) ?? LanguageOptions[0];
            LyricsBackgroundMediaPath = File.Exists(_settings.LyricsBackgroundMediaPath)
                ? _settings.LyricsBackgroundMediaPath
                : string.Empty;
            LyricsFullScreenFocusEnabled = _settings.LyricsFullScreenFocusEnabled;
            LyricsJoinSplitWords = _settings.LyricsJoinSplitWords;
            MinimizeToTray = _settings.MinimizeToTray;
            CloseToTray = _settings.CloseToTray;
            // Reflect the real OS autostart state (not an AppSettings copy) so the
            // toggle matches reality even if changed via Task Manager / Login Items.
            LaunchAtStartup = Helpers.StartupHelper.IsEnabled();
            StartMinimizedToTray = _settings.StartMinimizedToTray;
            RestoreLastTrackOnStartup = _settings.RestoreLastTrackOnStartup;
            WebRemoteEnabled = _settings.WebRemoteEnabled;
            NoctisServerPort = _settings.NoctisServerPort;
            NoctisServerEnabled = _settings.NoctisServerEnabled;
            ShowArtworkColumn = _settings.ShowArtworkColumn;
            ShowGenreColumn = _settings.ShowGenreColumn;
            ShowRatingColumn = _settings.ShowRatingColumn;
            ShowBpmColumn = _settings.ShowBpmColumn;
            ShowBitrateColumn = _settings.ShowBitrateColumn;
            ShowSampleRateColumn = _settings.ShowSampleRateColumn;
            ShowTimeColumn = _settings.ShowTimeColumn;
            ShowArtistColumn = _settings.ShowArtistColumn;
            ShowAlbumColumn = _settings.ShowAlbumColumn;
            ShowFavoritesColumn = _settings.ShowFavoritesColumn;
            ShowPlaysColumn = _settings.ShowPlaysColumn;
            SongsSortColumn = _settings.SongsSortColumn;
            SongsSortAscending = _settings.SongsSortAscending;
            SongsShowOnlyFavorites = _settings.SongsShowOnlyFavorites;
            AlbumSortMode = _settings.AlbumSortMode;
            AlbumSortAscending = _settings.AlbumSortAscending;
            ArtistSortMode = _settings.ArtistSortMode;
            ArtistSortAscending = _settings.ArtistSortAscending;
            HomeTopSongsExpanded = _settings.HomeTopSongsExpanded;
            HomeTopArtistsExpanded = _settings.HomeTopArtistsExpanded;
            HomeRecentlyPlayedExpanded = _settings.HomeRecentlyPlayedExpanded;
            HomeTimeRotationExpanded = _settings.HomeTimeRotationExpanded;
            HomeHeavyRotationExpanded = _settings.HomeHeavyRotationExpanded;
            HomeRediscoveredExpanded = _settings.HomeRediscoveredExpanded;
            PlaybackBarBackgroundOpacity = Math.Clamp(_settings.PlaybackBarBackgroundOpacity, 0, 1);
            MiniPlayerBackgroundOpacity = Math.Clamp(_settings.MiniPlayerBackgroundOpacity, 0, 1);
            AlbumTileSizeAuto = _settings.AlbumTileSizeAuto;
            AlbumTileTargetSize = Math.Clamp(_settings.AlbumTileTargetSize,
                Helpers.AlbumGridMetrics.MinTargetSize, Helpers.AlbumGridMetrics.MaxTargetSize);
            SidebarHoverExpand = _settings.SidebarHoverExpand;
            SidebarAlwaysExpanded = _settings.SidebarAlwaysExpanded;
            LiquidGlassEnabled = _settings.LiquidGlassEnabled;
            TaskbarProgressEnabled = _settings.TaskbarProgressEnabled;
            RefreshFileAssociationState();
            CollapseAlbumEditions = _settings.CollapseAlbumEditions;
            MergeFeaturedFromTitles = _settings.MergeFeaturedFromTitles;
            ArtistGroupMode = ArtistGroupModes.Parse(_settings.ArtistGroupMode).ToString();
            ReplaceArtistTagSeparators(_settings.ArtistTagSeparators);

            // Lyrics providers
            LrcLibEnabled = _settings.LrcLibEnabled;

            // Metadata providers
            DeezerEnabled = _settings.DeezerEnabled;
            MusicBrainzEnabled = _settings.MusicBrainzEnabled;
            FfmpegPath = _settings.FfmpegPath;
            RefreshFfmpegStatus();
            ExternalOpenAppPath = _settings.ExternalOpenAppPath;
            ReplayGainMode = string.IsNullOrEmpty(_settings.ReplayGainMode) ? "Off" : _settings.ReplayGainMode;
            // The ±12 dB bounds were UI-only (SettingsView.axaml PreampSlider), so a
            // hand-edited or corrupt settings.json rendered a nonsense value on the slider.
            ReplayGainPreampDb = Math.Clamp(_settings.ReplayGainPreampDb, -12, 12);
            ReplayGainEnabled = !string.Equals(ReplayGainMode, "Off", StringComparison.OrdinalIgnoreCase);
            GaplessPlaybackEnabled = _settings.GaplessPlaybackEnabled;
            AutoplayEnabled = _settings.AutoplayEnabled;
            AllowExplicitContent = _settings.AllowExplicitContent;
            BpmKeyAnalysisEnabled = _settings.BpmKeyAnalysisEnabled;
            WriteAnalysisToTags = _settings.WriteAnalysisToTags;
            ExclusiveAudioEnabled = _settings.ExclusiveAudioEnabled && IsExclusiveAudioSupported;
            NetEaseEnabled = _settings.NetEaseEnabled;

            // Equalizer
            _suppressEqNotify = true;
            EqualizerEnabled = _settings.EqualizerEnabled;
            EqPreampDb = Math.Clamp(_settings.EqPreampDb, ParametricEqMath.EqPreampMinDb, ParametricEqMath.EqPreampMaxDb);
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
            DiscordShowAlbum = _settings.DiscordShowAlbum;
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

            // Media server (v1: a single Subsonic/Jellyfin connection)
            var serverConnection = _settings.SourceConnections
                .FirstOrDefault(c => c.Type is SourceType.Navidrome or SourceType.Jellyfin);
            if (serverConnection != null)
            {
                _mediaServerConnection = serverConnection;
                // Prefer the stored flavor ("Gonic", "Airsonic", …); connections saved
                // before flavors existed carry the client's generic protocol name, so
                // fall back to the protocol's default preset.
                MediaServerType = MediaServerTypeOptions.Contains(serverConnection.Name)
                    ? serverConnection.Name
                    : serverConnection.Type == SourceType.Jellyfin
                        ? MediaServerTypeOptions[0]
                        : MediaServerTypeOptions[^1];
                MediaServerUrl = serverConnection.BaseUriOrPath;
                MediaServerUsername = serverConnection.Username;
                // The credential (Subsonic password / Jellyfin token) stays in the
                // connection object only — never surfaced back into the password box.
                IsMediaServerConnected = true;
                MediaServerStatusText = $"Connected to {serverConnection.BaseUriOrPath}";
                _mediaServer?.SetActiveConnection(serverConnection);
                MediaServerConnectionChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_discord != null)
            {
                // Lets the service's background reconnect stop as soon as the user turns
                // the setting off, instead of retrying against a disabled feature. Wired
                // unconditionally: setting it only when the feature was already on left it
                // null for anyone who enabled Discord later in the session, and the retry
                // loop then had no way to notice a subsequent toggle-off.
                _discord.IsEnabled = () => DiscordRichPresenceEnabled;

                if (DiscordRichPresenceEnabled)
                {
                    _ = _discord.ConnectAsync();
                    // Loon exists solely to serve Discord cover art — it follows the
                    // presence lifecycle instead of connecting unconditionally at
                    // startup (an always-on remote channel nobody may be using).
                    _ = ConnectLoonAsync();
                }
            }

            // Ensure player gets the persisted startup settings even if no toggle changed.
            // SetPlayer runs in the MainWindowViewModel constructor — before this load —
            // so fields without an OnChanged partial (e.g. the playback-bar width) would
            // otherwise only ever see their defaults. Both applies are idempotent.
            ApplyAudioSettings();
            ApplyPlayerSettings();

            // Apply the persisted theme on startup
            ThemeChanged?.Invoke(this, ResolveActiveThemeKey());

            // Apply the persisted accent colour on startup
            AccentChanged?.Invoke(this, ActiveAccentHex);

            // Apply the persisted Liquid Glass state on startup. The change handler
            // already fired during the load when the stored value was true; this
            // explicit (idempotent) invoke keeps the window consistent either way.
            LiquidGlassChanged?.Invoke(this, LiquidGlassEnabled);

            // Same deal for the pinned sidebar: idempotent re-invoke so the window
            // reflects the persisted state even if it subscribed after the load.
            SidebarAlwaysExpandedChanged?.Invoke(this, SidebarAlwaysExpanded);

            // The Songs/Albums view models are constructed before this async load
            // finishes, so they start on hardcoded defaults. Tell them the persisted
            // sort/filter is now readable rather than having them watch three
            // properties each and guess when the last one landed.
            ViewStateLoaded?.Invoke(this, EventArgs.Empty);

            _settingsLoaded = true;
            SettingsLoaded?.Invoke(this, EventArgs.Empty);
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
            await MergeExternalSettingChangesAsync();
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

    /// <summary>
    /// Re-bases <see cref="_settings"/> on what is currently on disk, then lets
    /// <see cref="SyncToSettings"/> re-apply the fields this view-model owns.
    ///
    /// This view-model loads one AppSettings at startup and keeps it for the whole
    /// session, but it is not the only writer: LibraryService writes ExcludedFilePaths
    /// and MetadataSchemaVersion, LyricsViewModel writes the lyrics background fields —
    /// each on its own instance, loaded and saved independently. SyncToSettings never
    /// touches those, so every save here wrote the app-start values back over them.
    /// Concretely: remove a track, close the window (which always saves), and the
    /// exclusion was erased — the next startup scan re-imported the file. The same
    /// mechanism reverted the lyrics background on every restart and made the
    /// MetadataSchemaVersion backfill (a full-library tag re-read) re-run every launch.
    ///
    /// Copying every readable/writable property means a field added by another component
    /// in future is preserved automatically, rather than silently reverting until someone
    /// remembers to extend a hand-written list.
    /// </summary>
    /// <summary>
    /// Window geometry is the one group of fields the merge must NOT pull back from disk.
    /// It is written straight onto <see cref="_settings"/> by the windows themselves
    /// (MainWindow.CaptureWindowPlacement, MiniPlayerWindow's move/resize capture) rather
    /// than by a view-model property, so <see cref="SyncToSettings"/> has nothing to
    /// re-apply afterwards — the merge simply restored the app-start values and the save
    /// wrote those back. That silently defeated geometry persistence entirely: the
    /// window's size and position never survived a restart. Nothing outside this process
    /// writes these keys, so the in-memory value is always the newer one.
    /// </summary>
    private static readonly HashSet<string> ProcessOwnedPlacementKeys = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.WindowWidth),
        nameof(AppSettings.WindowHeight),
        nameof(AppSettings.WindowX),
        nameof(AppSettings.WindowY),
        nameof(AppSettings.MainWindowState),
        nameof(AppSettings.MiniPlayerWidth),
        nameof(AppSettings.MiniPlayerHeight),
        nameof(AppSettings.MiniPlayerX),
        nameof(AppSettings.MiniPlayerY),
    };

    private async Task MergeExternalSettingChangesAsync()
    {
        try
        {
            var onDisk = await _persistence.LoadSettingsAsync();
            if (onDisk == null) return;

            foreach (var prop in typeof(AppSettings).GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (ProcessOwnedPlacementKeys.Contains(prop.Name)) continue;
                prop.SetValue(_settings, prop.GetValue(onDisk));
            }
        }
        catch (Exception ex)
        {
            // A failed merge must not block the save — worst case we write the
            // in-memory view, which is the old behaviour.
            Debug.WriteLine($"[SettingsViewModel] Settings merge failed: {ex.Message}");
        }
    }

    private void SyncToSettings()
    {
        ShortcutService.SaveTo(_settings);
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
        _settings.ProfileAvatarPath = ProfileAvatarPath ?? string.Empty;
        _settings.AccentColorHex = ActiveAccentHex;
        _settings.AccentPresetName = ActiveAccentName;

        _settings.ScanOnStartup = ScanOnStartup;
        _settings.WatchFoldersEnabled = WatchFoldersEnabled;
        _settings.UseEmbeddedArtwork = UseEmbeddedArtwork;
        _settings.OrganizePattern = OrganizePattern;
        _settings.OrganizeTargetRoot = OrganizeTargetRoot;
        // The change handlers write these straight into _settings, but SaveAsync
        // re-bases _settings on the on-disk file first (MergeExternalSettingChangesAsync),
        // so any VM-owned field not re-applied here is silently reverted on every save —
        // both About-tab toggles turned back off on the next launch.
        _settings.IncludePrereleaseUpdates = IncludePrereleaseUpdates;
        _settings.DeveloperMode = DeveloperMode;
        _settings.MusicFolders = _collectionSnapshot?.MusicFolders ?? MusicFolders.ToList();
        _settings.FolderRules = _collectionSnapshot?.FolderRules ?? FolderRules
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
        _settings.AlbumPageTintEnabled = AlbumPageTintEnabled;
        _settings.NowPlayingArtworkStyle = NowPlayingArtworkStyle ?? ArtworkMediums.DefaultSetting;
        _settings.CoverFlowLayout = CoverFlowLayout ?? CoverFlowLayouts.DefaultSetting;
        _settings.MiniPlayerStyle = MiniPlayerStyle ?? MiniPlayerStyles.DefaultSetting;
        _settings.PlaybackBarShowSkipButtons = PlaybackBarShowSkipButtons;
        _settings.PlaybackBarSkipSeconds = PlaybackBarSkipSeconds;
        _settings.PlaybackBarShowPlaybackSpeed = PlaybackBarShowPlaybackSpeed;
        _settings.PlaybackBarShowSleepTimer = PlaybackBarShowSleepTimer;
        _settings.PlaybackBarShowShuffle = PlaybackBarShowShuffle;
        _settings.LyricsFlowingLightEnabled = LyricsFlowingLightEnabled;
        _settings.LyricsFlowingStyle = LyricsFlowingStyle;
        _settings.LyricsVisualizerEnabled = LyricsVisualizerEnabled;
        _settings.LyricsVisualizerStyle = LyricsVisualizerStyle;
        _settings.LyricsVisualizerArtworkColor = LyricsVisualizerArtworkColor;
        _settings.LyricsBackgroundMediaPath = LyricsBackgroundMediaPath ?? string.Empty;
        _settings.LyricsFullScreenFocusEnabled = LyricsFullScreenFocusEnabled;
        _settings.LyricsJoinSplitWords = LyricsJoinSplitWords;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.CloseToTray = CloseToTray;
        _settings.StartMinimizedToTray = StartMinimizedToTray;
        _settings.RestoreLastTrackOnStartup = RestoreLastTrackOnStartup;
        _settings.WebRemoteEnabled = WebRemoteEnabled;
        _settings.NoctisServerEnabled = NoctisServerEnabled;
        _settings.NoctisServerPort = NoctisServerPort;
        _settings.ShowArtworkColumn = ShowArtworkColumn;
        _settings.ShowGenreColumn = ShowGenreColumn;
        _settings.ShowRatingColumn = ShowRatingColumn;
        _settings.ShowBpmColumn = ShowBpmColumn;
        _settings.ShowBitrateColumn = ShowBitrateColumn;
        _settings.ShowSampleRateColumn = ShowSampleRateColumn;
        _settings.ShowTimeColumn = ShowTimeColumn;
        _settings.ShowArtistColumn = ShowArtistColumn;
        _settings.ShowAlbumColumn = ShowAlbumColumn;
        _settings.ShowFavoritesColumn = ShowFavoritesColumn;
        _settings.ShowPlaysColumn = ShowPlaysColumn;
        _settings.SongsSortColumn = SongsSortColumn;
        _settings.SongsSortAscending = SongsSortAscending;
        _settings.SongsShowOnlyFavorites = SongsShowOnlyFavorites;
        _settings.AlbumSortMode = AlbumSortMode;
        _settings.AlbumSortAscending = AlbumSortAscending;
        _settings.ArtistSortMode = ArtistSortMode;
        _settings.ArtistSortAscending = ArtistSortAscending;
        _settings.HomeTopSongsExpanded = HomeTopSongsExpanded;
        _settings.HomeTopArtistsExpanded = HomeTopArtistsExpanded;
        _settings.HomeRecentlyPlayedExpanded = HomeRecentlyPlayedExpanded;
        _settings.HomeTimeRotationExpanded = HomeTimeRotationExpanded;
        _settings.HomeHeavyRotationExpanded = HomeHeavyRotationExpanded;
        _settings.HomeRediscoveredExpanded = HomeRediscoveredExpanded;
        _settings.PlaybackBarBackgroundOpacity = Math.Clamp(PlaybackBarBackgroundOpacity, 0, 1);
        _settings.MiniPlayerBackgroundOpacity = Math.Clamp(MiniPlayerBackgroundOpacity, 0, 1);
        _settings.AlbumTileSizeAuto = AlbumTileSizeAuto;
        _settings.AlbumTileTargetSize = Math.Clamp(AlbumTileTargetSize,
            Helpers.AlbumGridMetrics.MinTargetSize, Helpers.AlbumGridMetrics.MaxTargetSize);
        _settings.SidebarHoverExpand = SidebarHoverExpand;
        _settings.SidebarAlwaysExpanded = SidebarAlwaysExpanded;
        _settings.LiquidGlassEnabled = LiquidGlassEnabled;
        _settings.TaskbarProgressEnabled = TaskbarProgressEnabled;
        _settings.CollapseAlbumEditions = CollapseAlbumEditions;
        _settings.MergeFeaturedFromTitles = MergeFeaturedFromTitles;
        _settings.ArtistGroupMode = ArtistGroupModes.Parse(ArtistGroupMode).ToString();
        _settings.ArtistTagSeparators = ArtistTagSeparators.ToList();
        _settings.LrcLibEnabled = LrcLibEnabled;
        _settings.DeezerEnabled = DeezerEnabled;
        _settings.MusicBrainzEnabled = MusicBrainzEnabled;
        _settings.FfmpegPath = FfmpegPath ?? string.Empty;
        _settings.ExternalOpenAppPath = ExternalOpenAppPath ?? string.Empty;
        _settings.ReplayGainMode = ReplayGainMode ?? "Off";
        _settings.ReplayGainPreampDb = ReplayGainPreampDb;
        _settings.GaplessPlaybackEnabled = GaplessPlaybackEnabled;
        _settings.AutoplayEnabled = AutoplayEnabled;
        _settings.AllowExplicitContent = AllowExplicitContent;
        _settings.BpmKeyAnalysisEnabled = BpmKeyAnalysisEnabled;
        _settings.WriteAnalysisToTags = WriteAnalysisToTags;
        _settings.ExclusiveAudioEnabled = ExclusiveAudioEnabled;
        _settings.NetEaseEnabled = NetEaseEnabled;
        _settings.EqualizerEnabled = EqualizerEnabled;
        _settings.EqPreampDb = EqPreampDb;
        _settings.EqualizerPresetIndex = SelectedEqPresetIndex - 1;
        _settings.ParametricEqBands = EqBands
            .Select(b => new ParametricEqBand { FrequencyHz = b.FrequencyHz, GainDb = b.GainDb, Q = b.Q })
            .ToList();
        // Downgrade-safe mirror of the applied 10-band curve.
        _settings.EqualizerBands = GetGraphicEqBands().bands;
        _settings.DiscordRichPresenceEnabled = DiscordRichPresenceEnabled;
        _settings.DiscordShowAlbum = DiscordShowAlbum;
        _settings.LastFmScrobblingEnabled = LastFmScrobblingEnabled;
        _settings.LastFmUsername = LastFmUsername;
        if (_lastFm is LastFmService lfm)
            _settings.LastFmSessionKey = lfm.GetSessionKey() ?? "";

        _settings.ListenBrainzScrobblingEnabled = ListenBrainzScrobblingEnabled;
        // Persist-on-Connect contract (see OnListenBrainzTokenChanged): a token typed
        // but never validated must not ride along with unrelated saves — only a
        // connected (validated) token is stored.
        _settings.ListenBrainzToken = IsListenBrainzConnected ? (ListenBrainzToken ?? string.Empty) : string.Empty;
        _settings.ListenBrainzUsername = ListenBrainzUsername ?? string.Empty;

        // Media server: this VM owns the single Subsonic/Jellyfin connection, so the
        // stored list is rebuilt from the connected state on every save (the on-disk
        // merge above would otherwise revert a connect/disconnect). Connector types
        // this UI doesn't manage (Local/Smb/WebDav) are passed through untouched.
        var preservedConnections = _settings.SourceConnections
            .Where(c => c.Type is not (SourceType.Navidrome or SourceType.Jellyfin))
            .ToList();
        if (_mediaServerConnection != null)
            preservedConnections.Add(_mediaServerConnection);
        _settings.SourceConnections = preservedConnections;

        // Volume rides the same save: the shutdown path calls SetVolume right before
        // SaveAsync, and without this re-apply the on-disk merge above reverted it to
        // the stale stored value — the session's volume never survived a restart.
        if (_volume is int volume) _settings.Volume = volume;

        // Playback-bar width follows the same rule: the bar pushes it straight into
        // _settings via SetPlaybackBarWidth, so without this re-apply the on-disk
        // merge above would silently revert every resize on the next save.
        if (_playbackBarWidth is double barWidth) _settings.PlaybackBarWidth = barWidth;
    }

    /// <summary>Returns the loaded settings object.</summary>
    public AppSettings GetSettings() => _settings;

    /// <summary>Last volume pushed via <see cref="SetVolume"/>; null until the shell
    /// pushes one, so saves before that leave the stored value alone.</summary>
    private int? _volume;

    /// <summary>Updates the volume setting in the internal settings object.</summary>
    public void SetVolume(int volume) => _volume = _settings.Volume = volume;

    /// <summary>Last playback-bar width pushed via <see cref="SetPlaybackBarWidth"/>;
    /// null until the bar pushes one, so saves before that leave the stored value alone.</summary>
    private double? _playbackBarWidth;

    /// <summary>Persists a user resize of the playback bar (drag release / grip
    /// double-click reset). Debounced like every other settings write.</summary>
    public void SetPlaybackBarWidth(double width)
    {
        _playbackBarWidth = _settings.PlaybackBarWidth = width;
        // Keep the Appearance slider in step with a grip drag.
        _syncingPlaybackBarWidth = true;
        try { PlaybackBarIslandWidth = width; }
        finally { _syncingPlaybackBarWidth = false; }
        QueueSettingsSave();
    }

    /// <summary>Slider path of the island width: clamp, persist through the same
    /// debounced write the grip drag uses, and push it to the bar live.</summary>
    partial void OnPlaybackBarIslandWidthChanged(double value)
    {
        var clamped = double.IsFinite(value)
            ? Math.Clamp(value, PlaybackBarMinWidth, PlaybackBarMaxWidth)
            : PlaybackBarDefaultWidth;
        if (clamped != value)
        {
            PlaybackBarIslandWidth = clamped;
            return;
        }
        if (_suspendSettingPersistence || _syncingPlaybackBarWidth) return;
        _playbackBarWidth = _settings.PlaybackBarWidth = clamped;
        if (_player != null) _player.PlaybackBarIslandWidth = clamped;
        QueueSettingsSave();
    }

    /// <summary>
    /// Stores the mini player's current size (DIPs) and screen position (pixels) so the
    /// next open restores it. Called from the window's move/resize handlers rather than
    /// only on close: the mini player can be torn down by the main window shutting down,
    /// which saves before it gets there, and the debounce collapses a whole drag into one
    /// trailing write. See <see cref="ProcessOwnedPlacementKeys"/> for why these survive
    /// the on-disk merge.
    /// </summary>
    /// <summary>Position only: the mini player's stored SIZE is the classic card's and
    /// must survive a fixed design (whose canonical size is not the user's).</summary>
    public void SetMiniPlayerPosition(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        _settings.MiniPlayerX = x;
        _settings.MiniPlayerY = y;
        QueueSettingsSave();
    }

    /// <summary>The persisted classic-card size, if any.</summary>
    public (double Width, double Height)? StoredMiniPlayerSize
        => _settings.MiniPlayerWidth is { } w && _settings.MiniPlayerHeight is { } h
           && double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0
            ? (w, h)
            : null;

    public void SetMiniPlayerPlacement(double width, double height, double x, double y)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            width <= 0 || height <= 0)
            return;

        _settings.MiniPlayerWidth = width;
        _settings.MiniPlayerHeight = height;
        _settings.MiniPlayerX = x;
        _settings.MiniPlayerY = y;
        QueueSettingsSave();
    }

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
        _player.AutoplayEnabled = AutoplayEnabled;
        _player.AllowExplicitContent = AllowExplicitContent;
        _player.TrackTitleMarqueeEnabled = TrackTitleMarqueeEnabled;
        _player.ArtistMarqueeEnabled = ArtistMarqueeEnabled;
        _player.IslandShowSkipButtons = PlaybackBarShowSkipButtons;
        _player.IslandSkipSeconds = PlaybackBarSkipSeconds;
        _player.IslandShowPlaybackSpeed = PlaybackBarShowPlaybackSpeed;
        _player.IslandShowSleepTimer = PlaybackBarShowSleepTimer;
        _player.IslandShowShuffle = PlaybackBarShowShuffle;
        _player.IslandBackgroundOpacity = Math.Clamp(PlaybackBarBackgroundOpacity, 0, 1);
        // Already clamped by AppSettings.ClampToValidRanges on load; a live
        // SetPlaybackBarWidth writes the same value into _settings first.
        _player.PlaybackBarIslandWidth = _settings.PlaybackBarWidth;
        _player.LyricsFlowingLightEnabled = LyricsFlowingLightEnabled;
        _player.LyricsFlowingStyle = LyricsFlowingStyle;
        _player.LyricsVisualizerEnabled = LyricsVisualizerEnabled;
        _player.LyricsVisualizerStyle = LyricsVisualizerStyle;
        _player.LyricsVisualizerArtworkColor = LyricsVisualizerArtworkColor;
        _player.LyricsBackgroundMediaPath = LyricsBackgroundMediaPath ?? string.Empty;
        _player.LyricsFullScreenFocusEnabled = LyricsFullScreenFocusEnabled;
        _player.LyricsJoinSplitWords = LyricsJoinSplitWords;
        Controls.MarqueeTextBlock.GlobalCoverFlowScrollEnabled = CoverFlowMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalCoverFlowArtistScrollEnabled = CoverFlowArtistMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalCoverFlowAlbumScrollEnabled = CoverFlowAlbumMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalLyricsTitleScrollEnabled = LyricsTitleMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalLyricsArtistScrollEnabled = LyricsArtistMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = MiniPlayerTitleMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalMiniPlayerAlbumScrollEnabled = MiniPlayerAlbumMarqueeEnabled;
        Controls.MarqueeTextBlock.NotifyGlobalSettingsChanged();
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
            return (presetBands, ParametricEqMath.ApplyUserPreamp(presetPreamp, EqPreampDb));

        // Custom curves ride VLC's EQ filter, which attenuates its input by
        // EQZ_IN_FACTOR (−12 dB); the unity preamp cancels that so the overall
        // level stays at native and only the shaped bands move. A side effect:
        // an all-zero Custom curve no longer trips the flat-bypass (preamp is
        // non-zero) — it plays through the EQ at exact unity instead, which
        // also avoids a live filter-chain rebuild (audio dropout) every time a
        // drag crosses flat.
        return (ParametricEqMath.MapToGraphicBands(
            EqBands.Select(b => new ParametricEqBand { FrequencyHz = b.FrequencyHz, GainDb = b.GainDb, Q = b.Q })),
            ParametricEqMath.ApplyUserPreamp(ParametricEqMath.VlcEqUnityPreampDb, EqPreampDb));
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
            // VLC's presets carry ~+12 dB preamp because the EQ filter itself
            // attenuates by EQZ_IN_FACTOR (−12 dB) — preset preamps are unity
            // make-up, not a boost. Zeroing Flat's preamp makes the whole curve
            // register as flat so the player takes the true-bypass branch (no
            // filter in the chain) instead of running a unity-gain filter.
            if (vlcPresetIndex == 0) preamp = 0f;
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

        _audioPlayer?.SetAdvancedEqualizer(true, bands, ParametricEqMath.ApplyUserPreamp(preamp, EqPreampDb));
    }

    private void QueueEqualizerSave()
    {
        _eqSaveDebounceCts?.Cancel();
        _eqSaveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _eqSaveDebounceCts = cts;
        _ = SaveEqualizerDebouncedAsync(cts.Token);
    }

    // ── Debounced settings save ────────────────────────────────────────────
    // Everything driven by a continuous control (sliders, the accent colour picker,
    // text boxes) must persist on a trailing edge, not per input sample.
    //
    // Each SaveAsync is a full SyncToSettings + JsonSerializer.SerializeToNode + DPAPI
    // encrypt on the *calling* (UI) thread before SaveJsonAsync's Task.Run, plus a
    // temp-file write and rename. Sliders are driven straight off PointerMoved, so a
    // single drag produced dozens of those; the accent picker additionally rebuilt and
    // re-merged the whole accent ResourceDictionary per sample.
    private const int SettingsSaveDebounceMs = 250;
    private CancellationTokenSource? _settingsSaveDebounceCts;

    /// <summary>Persists settings on a trailing edge. Safe to call per input sample.</summary>
    private void QueueSettingsSave()
    {
        _settingsSaveDebounceCts?.Cancel();
        _settingsSaveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _settingsSaveDebounceCts = cts;
        _ = SaveSettingsDebouncedAsync(cts.Token);
    }

    private async Task SaveSettingsDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SettingsSaveDebounceMs, token);
            if (token.IsCancellationRequested) return;
            await SaveAsync();
        }
        catch (OperationCanceledException) { /* superseded by a newer change */ }
    }

    /// <summary>
    /// Flushes any pending debounced save immediately. Called on window close so the
    /// last drag/keystroke can't be lost to a cancelled timer.
    /// </summary>
    public async Task FlushPendingSaveAsync()
    {
        _settingsSaveDebounceCts?.Cancel();
        _eqSaveDebounceCts?.Cancel();
        try { await SaveAsync(); }
        finally { _collectionSnapshot = null; }
    }

    // UI-thread snapshot of the ObservableCollections SyncToSettings reads, taken before
    // a save is handed to a worker thread. Null on every normal (UI-thread) save.
    private sealed record CollectionSnapshot(List<string> MusicFolders, List<FolderRule> FolderRules);
    private CollectionSnapshot? _collectionSnapshot;

    /// <summary>
    /// Captures the UI-bound collections so a background save can serialize them without
    /// enumerating a collection the UI thread may be mutating. Call on the UI thread
    /// immediately before an off-thread <see cref="FlushPendingSaveAsync"/>.
    /// </summary>
    public void SnapshotCollectionsForSave()
    {
        _collectionSnapshot = new CollectionSnapshot(
            MusicFolders.ToList(),
            FolderRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => new FolderRule
                {
                    Path = r.Path.Trim(),
                    Include = r.Include,
                    Enabled = r.Enabled
                })
                .ToList());
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
        if (owner != null)
        {
            Helpers.DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else dialog.Show();

        if (dialog.Result == null) return;

        var result = dialog.Result;
        var wasActive = existingTile?.IsActive == true;
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

        // Activate new themes right away; re-apply an edited theme only if it was
        // already the active one — editing must not steal activation.
        if (existingTile == null || wasActive)
            ApplyCustomTheme(result.Id);
        else if (_settingsLoaded)
            _ = SaveAsync();
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
        // Debounced: the colour picker pushes a new hex on every pointer-move, and each
        // save is a full serialize + DPAPI encrypt on the UI thread. The live accent
        // (AccentChanged above) still applies immediately.
        QueueSettingsSave();
    }

    private static string? NormalizeAccentHex(string? value)
    {
        var hex = (value ?? string.Empty).Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;

        // 6 hex digits only. The old `Length is 7 or 9` accepted #AARRGGBB, so a pasted
        // "#40E74856" produced a 25%-opaque accent across the entire app; and with no
        // digit validation "#ZZZZZZ" passed here and failed later inside a swallowing try.
        if (hex.Length != 7) return null;
        for (var i = 1; i < hex.Length; i++)
            if (!Uri.IsHexDigit(hex[i])) return null;

        return hex;
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

    /// <summary>
    /// Re-applies the theme after an OS light/dark switch. No-op unless the System
    /// tile is active, so explicit picks and custom themes are never disturbed.
    /// Does not save: the persisted value stays "System".
    /// </summary>
    public void NotifySystemColorsChanged()
    {
        if (!IsSystemTheme || !string.IsNullOrEmpty(ActiveCustomThemeId)) return;
        ThemeChanged?.Invoke(this, ResolveActiveThemeKey());
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

    partial void OnUseEmbeddedArtworkChanged(bool value)
    {
        // Keep the extractor's static mirror current even during settings load, so
        // the first scan after startup honors a persisted "off" without a toggle flip.
        Services.MetadataService.UseEmbeddedArtwork = value;
        if (_suspendSettingPersistence) return;
        _settings.UseEmbeddedArtwork = value;
        _ = SaveAsync();
        // Turning it on can immediately heal albums that only carry tag art; turning
        // it off keeps covers already cached, matching the cache's fill-once design.
        if (value) _ = _library.BackfillMissingArtworkAsync();
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
        QueueSettingsSave();
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

    partial void OnSidebarAlwaysExpandedChanged(bool value)
    {
        // Raised even while settings are loading so a persisted "on" pins the sidebar
        // as soon as the value lands; the save itself stays gated on a finished load.
        SidebarAlwaysExpandedChanged?.Invoke(this, value);
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnLiquidGlassEnabledChanged(bool value)
    {
        // Raised even while settings are loading so a persisted "on" is applied as
        // soon as the value lands; the save itself stays gated on a finished load.
        LiquidGlassChanged?.Invoke(this, value);
        if (_settingsLoaded) _ = SaveAsync();
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
        if (_settingsLoaded && !_suspendSettingPersistence) QueueSettingsSave();
    }

    partial void OnMiniPlayerBackgroundOpacityChanged(double value)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.35;
        if (clamped != value)
        {
            MiniPlayerBackgroundOpacity = clamped;
            return;
        }

        // No Apply* step: the mini player's card binds this property directly, so the
        // change is live. Slider-driven, so the write is debounced like the bar's.
        if (_settingsLoaded && !_suspendSettingPersistence) QueueSettingsSave();
    }

    partial void OnTaskbarProgressEnabledChanged(bool value)
    {
        // MainWindow listens to this property to paint/clear the taskbar overlay.
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnCollapseAlbumEditionsChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnAlbumTileSizeAutoChanged(bool value)
    {
        // No Apply* step: the album/favorites view models watch this property.
        if (_settingsLoaded && !_suspendSettingPersistence) _ = SaveAsync();
    }

    partial void OnAlbumTileTargetSizeChanged(double value)
    {
        var clamped = double.IsFinite(value)
            ? Math.Clamp(value, Helpers.AlbumGridMetrics.MinTargetSize, Helpers.AlbumGridMetrics.MaxTargetSize)
            : 220;
        if (clamped != value)
        {
            AlbumTileTargetSize = clamped;
            return;
        }

        // Slider-driven, so the write is debounced like the opacity sliders'.
        if (_settingsLoaded && !_suspendSettingPersistence) QueueSettingsSave();
    }

    partial void OnMergeFeaturedFromTitlesChanged(bool value)
    {
        // Keep the scanner's static mirror current even during settings load, so the
        // first scan after startup honors a persisted "off" without a toggle flip.
        Services.MetadataService.MergeFeaturedFromTitles = value;
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
        _ = ApplyMergeFeaturedToLibraryAsync(value);
    }

    partial void OnArtistGroupModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsArtistGroupByArtist));
        OnPropertyChanged(nameof(IsArtistGroupByAlbumArtist));
        ApplyArtistGrouping();
    }

    /// <summary>
    /// Swaps the separator chips without firing a regroup per item; one apply at the end.
    /// </summary>
    private void ReplaceArtistTagSeparators(IEnumerable<string>? separators)
    {
        var normalized = ArtistCredit.NormalizeSeparators(separators);
        if (normalized.SequenceEqual(ArtistTagSeparators, StringComparer.Ordinal))
            return;
        ArtistTagSeparators.Clear();
        foreach (var s in normalized)
            ArtistTagSeparators.Add(s);
        ApplyArtistGrouping();
    }

    [RelayCommand]
    private void AddArtistSeparator()
    {
        var value = NewArtistSeparator?.Trim() ?? string.Empty;
        NewArtistSeparator = string.Empty;
        if (value.Length == 0) return;
        if (ArtistTagSeparators.Any(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase)))
            return;
        ArtistTagSeparators.Add(value);
        ApplyArtistGrouping();
    }

    [RelayCommand]
    private void RemoveArtistSeparator(string separator)
    {
        if (!ArtistTagSeparators.Remove(separator)) return;
        // An empty list would tokenize nothing; ArtistCredit falls back to the defaults,
        // so mirror that in the chips rather than showing an empty card that lies.
        if (ArtistTagSeparators.Count == 0)
            foreach (var s in ArtistCredit.DefaultSeparators)
                ArtistTagSeparators.Add(s);
        ApplyArtistGrouping();
    }

    [RelayCommand]
    private void ResetArtistSeparators() => ReplaceArtistTagSeparators(ArtistCredit.DefaultSeparators);

    /// <summary>
    /// Pushes the grouping mode and separators into the process-wide tokenizer. Runs during
    /// settings load too (before LibraryService restores its index cache) so startup sees
    /// the persisted configuration; only a real change after load saves and regroups.
    /// </summary>
    private void ApplyArtistGrouping()
    {
        var before = ArtistCredit.Version;
        ArtistCredit.Configure(ArtistGroupModes.Parse(ArtistGroupMode), ArtistTagSeparators);
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
        if (ArtistCredit.Version == before) return;
        // NotifyMetadataChanged rebuilds off-thread and raises LibraryUpdated itself,
        // which every grid already listens to; the status line just acknowledges.
        _library.NotifyMetadataChanged();
        SetScanStatus("Regrouping artists…", autoClear: true);
    }

    // Guards the status line against a superseded flip finishing after a newer one.
    private int _mergeFeatApplyGeneration;

    /// <summary>
    /// Applies a merge-featured toggle flip to the indexed library right away — a rescan
    /// reuses unchanged files, so it would never propagate this. Turning it off re-reads
    /// tags in the background (the pre-merge artist only exists in the files), so that
    /// direction announces itself in the scan status first.
    /// </summary>
    private async Task ApplyMergeFeaturedToLibraryAsync(bool value)
    {
        var generation = ++_mergeFeatApplyGeneration;
        if (!value)
            SetScanStatus("Restoring original artist credits…");

        int changed;
        try
        {
            changed = await _library.ApplyMergeFeaturedFromTitlesAsync(value);
        }
        catch (Exception)
        {
            if (generation == _mergeFeatApplyGeneration)
                SetScanStatus("Couldn't update artist credits.", autoClear: true);
            return;
        }

        if (generation != _mergeFeatApplyGeneration) return;
        SetScanStatus(changed switch
        {
            0 => "Artist credits already up to date.",
            1 => "Artist credits updated on 1 track.",
            _ => $"Artist credits updated on {changed:N0} tracks."
        }, autoClear: true);
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

    partial void OnEnableAnimatedCoversChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnAlbumPageTintEnabledChanged(bool value)
    {
        // No Apply* step: AlbumDetailViewModel subscribes to this VM's PropertyChanged.
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnLyricsBackgroundMediaPathChanged(string value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnNowPlayingArtworkStyleChanged(string value)
    {
        // No Apply* step: the lyrics page binds NowPlayingArtworkMedium directly.
        OnPropertyChanged(nameof(NowPlayingArtworkMedium));
        OnPropertyChanged(nameof(IsArtworkStyleCover));
        OnPropertyChanged(nameof(IsArtworkStyleCompactDisc));
        OnPropertyChanged(nameof(IsArtworkStyleVinyl));
        OnPropertyChanged(nameof(IsArtworkStyleCassette));
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnAllowExplicitContentChanged(bool value)
    {
        // Push straight to the player: turning it off prunes explicit tracks from the
        // live queue immediately, not at the next track change.
        if (_player != null) _player.AllowExplicitContent = value;
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnCoverFlowLayoutChanged(string value)
    {
        OnPropertyChanged(nameof(CoverFlowLayoutMode));
        OnPropertyChanged(nameof(IsCoverFlowCarousel));
        OnPropertyChanged(nameof(IsCoverFlowCascade));
        OnPropertyChanged(nameof(IsCoverFlowCollage));
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnPlaybackBarShowSkipButtonsChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnPlaybackBarSkipSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(IsSkipSeconds10));
        OnPropertyChanged(nameof(IsSkipSeconds15));
        OnPropertyChanged(nameof(IsSkipSeconds30));
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnPlaybackBarShowPlaybackSpeedChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnPlaybackBarShowSleepTimerChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnPlaybackBarShowShuffleChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnMiniPlayerStyleChanged(string value)
    {
        OnPropertyChanged(nameof(MiniPlayerStyleMode));
        OnPropertyChanged(nameof(IsMiniStyleClassic));
        OnPropertyChanged(nameof(IsMiniStylePill));
        OnPropertyChanged(nameof(IsMiniStyleSleeve));
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnLyricsFlowingLightEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    // ── Flowing background style (Drift + plugin visual layers) ──
    /// <summary>Selected style: "Drift" or a plugin layer name. Bound to the picker's SelectedItem.</summary>
    [ObservableProperty] private string _lyricsFlowingStyle = FlowingStyles.Drift;

    /// <summary>"Drift" first, then every visual layer the loaded, enabled plugins offer.</summary>
    public ObservableCollection<string> FlowingStyleOptions { get; } = new() { FlowingStyles.Drift };

    /// <summary>The picker only appears once a plugin actually adds a style — no plugins, no extra row.</summary>
    public bool HasFlowingStyleOptions => FlowingStyleOptions.Count > 1;

    partial void OnLyricsFlowingStyleChanged(string value)
    {
        if (value is null) { LyricsFlowingStyle = FlowingStyles.Drift; return; }
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnPluginsChanged(PluginHost? oldValue, PluginHost? newValue)
    {
        if (oldValue is not null) oldValue.VisualLayersChanged -= OnPluginVisualLayersChanged;
        if (newValue is not null) newValue.VisualLayersChanged += OnPluginVisualLayersChanged;
        RefreshFlowingStyleOptions();
    }

    private void OnPluginVisualLayersChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshFlowingStyleOptions);

    private void RefreshFlowingStyleOptions()
    {
        // Keep the current selection even when its plugin is off right now: the lyrics page
        // falls back to Drift on its own, and the choice comes back with the plugin.
        var keep = LyricsFlowingStyle;
        var names = new List<string> { FlowingStyles.Drift };
        if (Plugins is not null)
            foreach (var layer in Plugins.VisualLayers)
                if (!string.IsNullOrWhiteSpace(layer.Name) && !names.Contains(layer.Name)) names.Add(layer.Name);
        if (keep != FlowingStyles.Drift && !names.Contains(keep)) names.Add(keep);

        if (!names.SequenceEqual(FlowingStyleOptions))
        {
            FlowingStyleOptions.Clear();
            foreach (var n in names) FlowingStyleOptions.Add(n);
            // Rebuilding the items momentarily nulls a ComboBox's SelectedItem; put it back.
            LyricsFlowingStyle = keep;
        }
        OnPropertyChanged(nameof(HasFlowingStyleOptions));
    }

    partial void OnLyricsVisualizerEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnLyricsVisualizerArtworkColorChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnLyricsVisualizerStyleChanged(string value)
    {
        OnPropertyChanged(nameof(LyricsVisualizerStyleMode));
        OnPropertyChanged(nameof(IsVisualizerStyleBars));
        OnPropertyChanged(nameof(IsVisualizerStyleMirror));
        OnPropertyChanged(nameof(IsVisualizerStyleWave));
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnLyricsFullScreenFocusEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnLyricsJoinSplitWordsChanged(bool value)
    {
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
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

    partial void OnMusicBrainzEnabledChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnDeezerEnabledChanged(bool value)
    {
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
        // The probe spawns `ffmpeg -version`, and TextBox.Text updates per keystroke —
        // typing a path was one process launch per character. Trailing edge only; the
        // Enter handler in the view still probes immediately.
        QueueFfmpegProbe();
        if (_suspendSettingPersistence) return;
        QueueSettingsSave();
    }

    private CancellationTokenSource? _ffmpegProbeDebounceCts;

    private void QueueFfmpegProbe()
    {
        _ffmpegProbeDebounceCts?.Cancel();
        _ffmpegProbeDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _ffmpegProbeDebounceCts = cts;
        _ = ProbeFfmpegDebouncedAsync(cts.Token);
    }

    private async Task ProbeFfmpegDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SettingsSaveDebounceMs, token);
            if (token.IsCancellationRequested) return;
            RefreshFfmpegStatus();
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
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

    partial void OnAutoplayEnabledChanged(bool value)
    {
        // Live apply: the player reads this at each queue exhaustion, so flipping it
        // mid-session arms (or disarms) the very next one — no restart needed.
        if (_player != null) _player.AutoplayEnabled = value;
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
    }

    partial void OnBpmKeyAnalysisEnabledChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        if (value)
        {
            // The only other StartBackfill trigger is LibraryUpdated, so enabling
            // this mid-session on a static library did nothing until the next scan
            // or restart. StartBackfill reads the persisted settings object, so the
            // save (which syncs the flag into it) must complete first.
            _ = SaveThenStartAnalysisAsync();
        }
        else
        {
            App.Services?.GetService<Noctis.Services.AudioAnalysis.AudioAnalysisCoordinator>()?.Stop();
            _ = SaveAsync();
        }
    }

    private async Task SaveThenStartAnalysisAsync()
    {
        await SaveAsync();
        App.Services?.GetService<Noctis.Services.AudioAnalysis.AudioAnalysisCoordinator>()?.StartBackfill();
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
        QueueSettingsSave();
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

    partial void OnExternalOpenAppPathChanged(string value)
    {
        // Sync the live settings object immediately: the track context menu reads the
        // path through GetSettings() on every open, so it must not lag behind the save.
        _settings.ExternalOpenAppPath = value ?? string.Empty;
        if (_suspendSettingPersistence) return;
        QueueSettingsSave();
    }

    [RelayCommand]
    private async Task BrowseExternalOpenAppAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not Avalonia.Controls.Window owner) return;

        var top = Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (top == null) return;

        var picks = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose a program",
            AllowMultiple = false,
        });
        if (picks.Count > 0)
            ExternalOpenAppPath = picks[0].Path.LocalPath;
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
                // Loon first so the artwork URL resolves for the republish below.
                await ConnectLoonAsync();
                // Republish current playback state so the track appears immediately.
                await RepublishDiscordPresenceAsync();
            }
        }
        else
        {
            await _discord!.ClearAsync();
            await _discord.DisconnectAsync();
            await DisconnectLoonAsync();
        }

        await SaveAsync();
    }

    // Loon rides the Discord presence lifecycle (see the startup connect note).
    private async Task ConnectLoonAsync()
    {
        if (_loon == null || _loon.IsConnected) return;
        var url = _settings.LoonServerUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = "https://noctis-loon.duckdns.org";
        // Upgrade a persisted plaintext default from before TLS was enabled on
        // the relay (the server now serves wss:// via Caddy). Custom user-set
        // hosts are left untouched.
        else if (url.Equals("http://noctis-loon.duckdns.org", StringComparison.OrdinalIgnoreCase))
            url = "https://noctis-loon.duckdns.org";
        try
        {
            await _loon.ConnectAsync(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Connection failed: {ex.Message}");
        }
    }

    private async Task DisconnectLoonAsync()
    {
        if (_loon == null) return;
        try
        {
            await _loon.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Disconnect failed: {ex.Message}");
        }
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
            ArtworkUrl: artworkUrl,
            ShowAlbum: DiscordShowAlbum);

        var isPlaying = _player.State == PlaybackState.Playing;
        await _discord.UpdateAsync(dto, _player.Position, _player.Duration, isPlaying);
    }

    partial void OnDiscordShowAlbumChanged(bool value)
    {
        if (_suspendSettingPersistence) return;
        _ = SaveAsync();
        // Re-send the current track so the card reflects the flip without a song change.
        _ = RepublishDiscordPresenceAsync();
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
            // Mirrors the Last.fm auth path: connecting an account is the user asking to
            // scrobble. Without this, the now-default-off toggle would leave a freshly
            // connected account silently not scrobbling.
            ListenBrainzScrobblingEnabled = true;
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
        // Mirror the connect path (which sets this true): leaving the hidden toggle
        // armed meant any token typed after logout scrobbled without validation.
        ListenBrainzScrobblingEnabled = false;
        ListenBrainzToken = "";
        ListenBrainzUsername = "";
        ListenBrainzStatusText = "Not connected";
        ListenBrainzError = "";
        _ = SaveAsync();
    }

    // ── Media server ──

    private DispatcherTimer? _serverErrorDismissTimer;

    /// <summary>
    /// Shows a failure on the status line and schedules it to dissolve back to
    /// "Not connected" after 3 seconds — error nags are tied to the Connect click
    /// that caused them, not to state, so they must not linger (or survive a tab
    /// switch, see OnSelectedSettingsTabChanged).
    /// </summary>
    private void ShowTransientServerError(string message)
    {
        MediaServerStatusText = message;
        HasMediaServerError = true;
        if (_serverErrorDismissTimer == null)
        {
            _serverErrorDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _serverErrorDismissTimer.Tick += (_, _) => ClearTransientServerError();
        }
        _serverErrorDismissTimer.Stop();
        _serverErrorDismissTimer.Start();
    }

    /// <summary>
    /// Reverts an error status to the idle baseline. Safe to call in any state:
    /// no-op unless an error is actually showing, and it never overwrites the
    /// busy/connected status lines. Internal for tests (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal void ClearTransientServerError()
    {
        _serverErrorDismissTimer?.Stop();
        if (!HasMediaServerError) return;
        HasMediaServerError = false;
        if (!IsMediaServerConnected && !IsMediaServerBusy)
            MediaServerStatusText = "Not connected";
    }

    /// <summary>
    /// Maps a picker preset to the protocol client. Jellyfin speaks its own API;
    /// Navidrome, Airsonic, Gonic and "Subsonic (other)" all speak the Subsonic
    /// protocol (SourceType.Navidrome internally). Null falls back to Jellyfin to
    /// mirror the field's default selection.
    /// Internal for tests (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal static SourceType MediaServerOptionToSourceType(string? option) =>
        option is null or "Jellyfin" ? SourceType.Jellyfin : SourceType.Navidrome;

    /// <summary>Validates the typed server details and, on success, persists the connection.</summary>
    [RelayCommand]
    private async Task ConnectMediaServer()
    {
        if (_mediaServer == null || IsMediaServerBusy) return;

        var type = MediaServerOptionToSourceType(MediaServerType);
        if (string.IsNullOrWhiteSpace(MediaServerUrl))
        {
            ShowTransientServerError("Enter the server address.");
            return;
        }
        if (string.IsNullOrWhiteSpace(MediaServerUsername) || string.IsNullOrWhiteSpace(MediaServerPassword))
        {
            ShowTransientServerError("Enter the username and password.");
            return;
        }

        IsMediaServerBusy = true;
        HasMediaServerError = false;
        MediaServerStatusText = "Connecting…";
        try
        {
            var (result, connection) = await _mediaServer.ConnectAsync(
                type, MediaServerUrl, MediaServerUsername, MediaServerPassword, _mediaServerConnection?.Id);
            if (!result.Success)
            {
                ShowTransientServerError(result.Message);
                return;
            }

            // Keep the picked flavor ("Navidrome", "Gonic", …) rather than the client's
            // generic protocol name, so the connected summary echoes what the user chose.
            connection.Name = MediaServerType ?? connection.Name;
            _mediaServerConnection = connection;
            MediaServerUrl = connection.BaseUriOrPath; // normalized by the client
            MediaServerPassword = string.Empty;        // never keep the password in a bound field
            IsMediaServerConnected = true;
            MediaServerStatusText = result.Message;
            _mediaServer.SetActiveConnection(connection);
            MediaServerConnectionChanged?.Invoke(this, EventArgs.Empty);
            await SaveAsync();
        }
        finally
        {
            IsMediaServerBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectMediaServer()
    {
        _mediaServerConnection = null;
        IsMediaServerConnected = false;
        MediaServerPassword = string.Empty;
        MediaServerStatusText = "Not connected";
        HasMediaServerError = false;
        _mediaServer?.SetActiveConnection(null);
        MediaServerConnectionChanged?.Invoke(this, EventArgs.Empty);
        await SaveAsync();
    }

    // ── Equalizer handlers ──

    partial void OnEqualizerEnabledChanged(bool value)
    {
        if (_suppressEqNotify) return;
        ApplyEqualizer();
        QueueEqualizerSave();
    }

    partial void OnEqPreampDbChanged(double value)
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
        EqPreampDb = 0;
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

    // ── Removed tracks (removed from the library with "Keep Files") ──

    /// <summary>Removed-but-kept-on-disk files, shown in a reversible Settings list.</summary>
    public ObservableCollection<RemovedTrackEntry> RemovedTracks { get; } = new();

    /// <summary>True when at least one removed file can be restored (drives the empty-state placeholder).</summary>
    [ObservableProperty] private bool _hasRemovedTracks;

    /// <summary>
    /// Reloads the removed-tracks list from the settings on disk. LibraryService owns
    /// ExcludedFilePaths and writes it on its own AppSettings instance, so the
    /// session-long <see cref="_settings"/> copy here can be stale — always re-read.
    /// </summary>
    public async Task RefreshRemovedTracksAsync()
    {
        IReadOnlyList<RemovedTrackEntry> entries;
        try
        {
            entries = await LibraryRemovalHelper.GetRemovedEntriesAsync(_persistence);
        }
        catch
        {
            // Settings unreadable — keep the current list rather than showing a
            // false "No removed tracks" empty state.
            return;
        }
        RemovedTracks.Clear();
        foreach (var e in entries)
            RemovedTracks.Add(e);
        HasRemovedTracks = RemovedTracks.Count > 0;
    }

    [RelayCommand]
    private async Task RestoreRemovedTrack(RemovedTrackEntry? entry)
    {
        if (entry == null) return;
        // ImportFilesAsync drops the ExcludedFilePaths tombstone for explicitly
        // re-imported paths and adds the track back to the library.
        await _library.ImportFilesAsync(new[] { entry.FilePath });
        await RefreshRemovedTracksAsync();
    }

    // ── Library overview + Storage ──

    /// <summary>
    /// Recomputes the Statistics tab. Fire-and-forget wrapper for the click paths that
    /// can't await (settings reset, scan completion).
    /// </summary>
    public void RefreshLibraryStats() => _ = RefreshLibraryStatsAsync();

    /// <summary>
    /// Recomputes the Statistics tab off the UI thread.
    ///
    /// This was inline work on every Settings open and every click of the Statistics
    /// tab, commented as "cheap": it builds a Dictionary of the entire library, folds
    /// the whole play-history log over it, then runs two GroupBy+OrderBy+Sum passes for
    /// the top-five lists. On a large library that is a visible stall on the click it is
    /// reacting to. Only the snapshot and the property writes stay on the UI thread.
    /// </summary>
    public async Task RefreshLibraryStatsAsync()
    {
        // Snapshot on the caller's (UI) thread: the library collections are mutated by
        // scans and watcher batches, so the background pass must not enumerate them live.
        var tracks = _library.Tracks.ToArray();
        var albums = _library.Albums.ToArray();
        var artistCount = _library.Artists.Count;
        var events = _playHistory.Events; // already an immutable published snapshot

        var stats = await Task.Run(() => ComputeLibraryStats(tracks, albums, artistCount, events))
            .ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() => ApplyLibraryStats(stats));
    }

    /// <summary>Everything the Statistics tab shows, computed without touching the UI.</summary>
    private sealed record LibraryStatsResult(
        int TotalSongs, int TotalArtists, int TotalAlbums,
        string TotalFileSize, string TotalListeningTime, string TotalPlays,
        string TimeListened, string AvgTrackLength, int LikedTracks,
        int LosslessCount, int LossyCount, int HiResCount,
        double LosslessPercentage, string LosslessPercentageText,
        string LossyPercentageText, string HiResPercentageText,
        List<StatItem> TopArtists, List<StatItem> TopAlbums);

    private static LibraryStatsResult ComputeLibraryStats(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<Album> albums,
        int artistCount,
        IReadOnlyList<PlayHistoryEvent> events)
    {
        // Single pass over Tracks: Sum(FileSize) + Sum(Duration) + Count(IsLossless)
        // + Count(IsHiResLossless) all in one iteration. Previously 4 LINQ passes
        // over the same collection plus a redundant tracks.Count subtraction.
        long totalBytes = 0;
        long totalDurationTicks = 0;
        long totalPlays = 0;
        int losslessCount = 0;
        int hiResCount = 0;
        int likedCount = 0;
        var tracksById = new Dictionary<Guid, Track>(tracks.Count);
        foreach (var t in tracks)
        {
            totalBytes += t.FileSize;
            totalDurationTicks += t.Duration.Ticks;
            totalPlays += t.PlayCount;
            if (t.IsLossless) losslessCount++;
            if (t.IsHiResLossless) hiResCount++;
            if (t.IsFavorite) likedCount++;
            tracksById[t.Id] = t;
        }

        // Listening time / average reflect what was actually played (skips excluded),
        // computed from the play log — see ListeningStatsCalculator.
        var listening = ListeningStatsCalculator.Compute(events, tracksById);

        var (topArtists, topAlbums) = ComputeTopPlayed(tracks, albums);

        var pct = tracks.Count > 0 ? (double)losslessCount / tracks.Count : 0;
        return new LibraryStatsResult(
            TotalSongs: tracks.Count,
            TotalArtists: artistCount,
            TotalAlbums: albums.Count,
            TotalFileSize: FormatLibrarySize(totalBytes),
            TotalListeningTime: FormatDuration(TimeSpan.FromTicks(totalDurationTicks)),
            TotalPlays: FormatCount(totalPlays),
            TimeListened: FormatDuration(TimeSpan.FromTicks(listening.TimeListenedTicks)),
            AvgTrackLength: listening.AvgListenedTrackLengthTicks > 0
                ? TimeSpan.FromTicks(listening.AvgListenedTrackLengthTicks).ToString(@"m\:ss")
                : tracks.Count > 0
                    ? TimeSpan.FromTicks(totalDurationTicks / tracks.Count).ToString(@"m\:ss")
                    : "0:00",
            LikedTracks: likedCount,
            LosslessCount: losslessCount,
            LossyCount: tracks.Count - losslessCount,
            HiResCount: hiResCount,
            LosslessPercentage: pct,
            LosslessPercentageText: tracks.Count > 0 ? $"{pct * 100:F0}%" : "0%",
            LossyPercentageText: tracks.Count > 0 ? $"{(1 - pct) * 100:F0}%" : "0%",
            HiResPercentageText: tracks.Count > 0 ? $"{(double)hiResCount / tracks.Count * 100:F0}%" : "0%",
            TopArtists: topArtists,
            TopAlbums: topAlbums);
    }

    private void ApplyLibraryStats(LibraryStatsResult s)
    {
        TotalSongs = s.TotalSongs;
        TotalArtists = s.TotalArtists;
        TotalAlbums = s.TotalAlbums;
        TotalFileSize = s.TotalFileSize;
        TotalListeningTime = s.TotalListeningTime;
        TotalPlays = s.TotalPlays;
        TimeListened = s.TimeListened;
        AvgTrackLength = s.AvgTrackLength;
        LikedTracks = s.LikedTracks;
        TopArtists.ReplaceAll(s.TopArtists);
        TopAlbums.ReplaceAll(s.TopAlbums);
        LosslessCount = s.LosslessCount;
        LossyCount = s.LossyCount;
        HiResCount = s.HiResCount;
        LosslessPercentage = s.LosslessPercentage;
        LosslessPercentageText = s.LosslessPercentageText;
        LossyPercentageText = s.LossyPercentageText;
        HiResPercentageText = s.HiResPercentageText;
        RefreshSnoozedTracks();
        _ = RefreshRemovedTracksAsync();
    }

    private static (List<StatItem> Artists, List<StatItem> Albums) ComputeTopPlayed(
        IReadOnlyList<Track> tracks, IReadOnlyList<Album> allAlbums)
    {
        var artists = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t.Artist.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new StatItem
            {
                Label = g.Key,
                SubLabel = g.Count() == 1 ? "1 track" : $"{g.Count()} tracks",
                Value = g.Sum(t => t.PlayCount),
                ValueLabel = $"{g.Sum(t => t.PlayCount)} plays"
            })
            .Where(i => i.Value > 0)
            .OrderByDescending(i => i.Value)
            .Take(5)
            .ToList();
        ApplyRanks(artists);

        var albumsById = allAlbums.ToDictionary(a => a.Id);
        var albums = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => t.AlbumId)
            .Select(g =>
            {
                albumsById.TryGetValue(g.Key, out var album);
                var plays = g.Sum(t => t.PlayCount);
                return new StatItem
                {
                    Label = album?.Name ?? g.First().Album,
                    SubLabel = album?.Artist ?? g.First().Artist,
                    Value = plays,
                    ValueLabel = $"{plays} plays"
                };
            })
            .Where(i => i.Value > 0)
            .OrderByDescending(i => i.Value)
            .Take(5)
            .ToList();
        ApplyRanks(albums);

        return (artists, albums);
    }

    private static void ApplyRanks(List<StatItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].Rank = i + 1;
    }

    private static string FormatCount(long count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:0.#}M";
        if (count >= 1_000) return $"{count / 1_000.0:0.#}K";
        return count.ToString();
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
    public async Task RefreshStorageInfoAsync(bool forceRefresh = false)
    {
        var dataDir = _persistence.DataDirectory;
        if (!Directory.Exists(dataDir)) return;

        var result = await Task.Run(() =>
        {
            long librarySize = GetFileSize(Path.Combine(dataDir, "library.json"));
            long queueSize = GetFileSize(Path.Combine(dataDir, "queue.json"));
            long playlistsSize = GetFileSize(Path.Combine(dataDir, "playlists.json"));
            long settingsSize = GetFileSize(Path.Combine(dataDir, "settings.json"));
            long artworkSize = GetDirectorySize(Path.Combine(dataDir, "artwork"), forceRefresh);

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
    /// <param name="autoScan">
    /// When false the root is registered without kicking off a library scan. The
    /// drag-and-drop import needs this: it imports the dropped files itself right after,
    /// and a scan running in parallel republishes its own authoritative track list, which
    /// overwrote the freshly imported tracks.
    /// </param>
    public async Task AddFolderPath(string path, bool autoScan = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // The old guard was `MusicFolders.Contains(path)` — ordinal and case-sensitive.
        // On Windows that accepted C:\Music and c:\music\ as two separate scan roots for
        // the same tree, and nothing stopped adding a folder already inside an existing
        // root, so the same files were scanned (and watched) twice.
        path = NormalizeFolderPath(path);

        if (!Directory.Exists(path))
        {
            SetScanStatus("That folder doesn't exist.", autoClear: true);
            return;
        }

        foreach (var existing in MusicFolders)
        {
            var norm = NormalizeFolderPath(existing);
            if (string.Equals(norm, path, StringComparison.OrdinalIgnoreCase))
                return; // already a root — silent, the user just re-picked it

            if (IsUnder(path, norm))
            {
                SetScanStatus($"Already covered by \"{existing}\".", autoClear: true);
                return;
            }

            if (IsUnder(norm, path))
            {
                SetScanStatus($"\"{existing}\" is inside that folder — remove it first.", autoClear: true);
                return;
            }
        }

        MusicFolders.Add(path);
        _settings.MusicFolders = MusicFolders.ToList();
        OnPropertyChanged(nameof(MediaFolderDisplay));
        await SaveAsync();
        MusicFoldersChanged?.Invoke(this, EventArgs.Empty);

        // Auto-scan so the user doesn't have to press "Scan". Routed through the
        // shared flow so the spinner shows and the Scan button disables while it
        // runs. The unchanged-file fast path means only the new folder is read.
        if (autoScan)
            _ = RunLibraryScanAsync();
    }

    /// <summary>Absolute, separator-normalized, no trailing separator. Falls back to the
    /// trimmed input when the path can't be resolved (a UNC host that's offline, say).</summary>
    private static string NormalizeFolderPath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch { return path.Trim(); }
    }

    /// <summary>True when <paramref name="candidate"/> is a descendant of <paramref name="root"/>.
    /// Both must already be normalized. The separator check stops "C:\MusicVideos" from
    /// matching the root "C:\Music".</summary>
    private static bool IsUnder(string candidate, string root)
        => candidate.Length > root.Length
           && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (candidate[root.Length] == Path.DirectorySeparatorChar
               || candidate[root.Length] == Path.AltDirectorySeparatorChar);

    [RelayCommand]
    private async Task RemoveFolder(string folder)
    {
        MusicFolders.Remove(folder);
        _settings.MusicFolders = MusicFolders.ToList();
        OnPropertyChanged(nameof(MediaFolderDisplay));
        await SaveAsync();
        MusicFoldersChanged?.Invoke(this, EventArgs.Empty);

        // Re-scan so tracks from the removed folder drop out of the library.
        _ = RunLibraryScanAsync();
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
    private Task Rescan() => RunLibraryScanAsync();

    /// <summary>
    /// Shared library-scan flow used by the Scan button and by automatic scans
    /// after a media folder is added or removed. Drives <see cref="IsScanning"/>
    /// and <see cref="ScanStatusText"/> so the UI (spinner + disabled button)
    /// reflects every scan, however it was triggered.
    /// </summary>
    private Task RunLibraryScanAsync()
    {
        // Supersede any in-flight scan so an add/remove always re-scans against the
        // current folder set. Without this the old "if (IsScanning) return" dropped
        // the re-scan triggered by removing a folder mid-scan, so its tracks never
        // left the library. Cancel the running scan (it rolls back to "no change")
        // and chain a fresh scan after it unwinds — the two never mutate concurrently.
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var prior = _scanInFlight;
        var task = RunScanCoreAsync(prior, cts);
        _scanInFlight = task;
        return task;
    }

    private async Task RunScanCoreAsync(Task prior, CancellationTokenSource cts)
    {
        // Wait for the superseded scan to finish unwinding before mutating the
        // library, so ScanAsync never runs twice concurrently.
        try { await prior.ConfigureAwait(true); } catch { /* prior was cancelled */ }

        if (cts.IsCancellationRequested) return; // superseded again before we started

        IsScanning = true;
        ScanStatusText = "Scanning Library";
        ScanProgress = 0;

        try
        {
            await _library.ScanAsync(MusicFolders, cts.Token);
            if (cts.IsCancellationRequested) return;

            SetScanStatus(_library.Tracks.Count == 0
                ? "No tracks found."
                : $"{_library.Tracks.Count} tracks found.", autoClear: true);
            RefreshLibraryStats();
            // Fire-and-forget async variant: the forced artwork-cache walk scales
            // with library size and froze the UI right as the scan finished.
            _ = RefreshStorageInfoAsync(forceRefresh: true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan — leave its status/state to that scan.
        }
        catch (Exception ex)
        {
            SetScanStatus($"Scan error: {ex.Message}", autoClear: true);
        }
        finally
        {
            // Only the most recent scan owns IsScanning; an older superseded scan
            // must not flip it off while the new one is still running.
            if (ReferenceEquals(_scanCts, cts))
            {
                IsScanning = false;
                _scanCts = null;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task RebuildIndex()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatusText = "Rebuilding library index";
        ScanProgress = 0;

        try
        {
            await _library.RebuildIndexAsync();
            SetScanStatus(_library.Tracks.Count == 0
                ? "No tracks found."
                : "Indexed Library.", autoClear: true);
            RefreshLibraryStats();
            _ = RefreshStorageInfoAsync(forceRefresh: true);
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
    private async Task ShowResetConfirm()
    {
        // The card only said "Restore defaults and clear saved library data" and the
        // confirm step only added "cannot be undone" — but the reset also wipes
        // playlists.json, and playlists are hand-authored content that no rescan brings
        // back. Name what actually gets destroyed, with the real playlist count.
        await RefreshPlaylistCountAsync();
        ResetConfirmDetail = TotalPlaylists switch
        {
            0 => "This clears your library, queue, playlist covers, cached lyrics and artwork, and restores every setting to its default.",
            1 => "This permanently deletes 1 playlist, and clears your library, queue, playlist covers, cached lyrics and artwork. Every setting returns to its default.",
            _ => $"This permanently deletes all {TotalPlaylists} playlists, and clears your library, queue, playlist covers, cached lyrics and artwork. Every setting returns to its default."
        };
        IsResetConfirmVisible = true;
    }

    /// <summary>Spelled-out consequences shown above the reset confirm buttons.</summary>
    [ObservableProperty] private string _resetConfirmDetail = string.Empty;

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

        // Clear artwork cache (albums + artists). Task.Run for every recursive
        // delete below: they run back-to-back on the UI context and blocked the
        // dispatcher for their whole duration on large libraries or slow disks.
        try
        {
            var artworkDir = Path.Combine(_persistence.DataDirectory, "artwork");
            await Task.Run(() =>
            {
                if (Directory.Exists(artworkDir))
                {
                    Directory.Delete(artworkDir, true);
                    Directory.CreateDirectory(artworkDir);
                    Directory.CreateDirectory(Path.Combine(artworkDir, "artists"));
                }
            });
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
            await Task.Run(() =>
            {
                if (Directory.Exists(lyricsDir))
                {
                    Directory.Delete(lyricsDir, true);
                    Directory.CreateDirectory(lyricsDir);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear lyrics cache: {ex.Message}");
        }

        // Clear playlist covers
        try
        {
            var coversDir = Path.Combine(_persistence.DataDirectory, "playlist_covers");
            await Task.Run(() =>
            {
                if (Directory.Exists(coversDir))
                {
                    Directory.Delete(coversDir, true);
                    Directory.CreateDirectory(coversDir);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear playlist covers: {ex.Message}");
        }

        // Clear offline / streaming cache
        try
        {
            var cacheDir = Path.Combine(_persistence.DataDirectory, "cache");
            await Task.Run(() =>
            {
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, true);
                    Directory.CreateDirectory(cacheDir);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear offline cache: {ex.Message}");
        }

        // Clear audit trail
        try
        {
            var auditDir = Path.Combine(_persistence.DataDirectory, "audit");
            await Task.Run(() =>
            {
                if (Directory.Exists(auditDir))
                {
                    Directory.Delete(auditDir, true);
                    Directory.CreateDirectory(auditDir);
                }
            });
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

        // Preserved crash-session logs go with it.
        CrashJournal.ClearPreserved();

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

            // Theme — reset to default (Gray).
            // SetActiveThemeFlags alone left ActiveCustomThemeId and the CustomThemes
            // collection intact, so ResolveActiveThemeKey() still returned "Custom:<id>"
            // and SyncToSettings wrote it straight back — the reset visibly undid itself.
            ActiveCustomThemeId = null;
            CustomThemes.Clear();
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
            UseEmbeddedArtwork = defaultSettings.UseEmbeddedArtwork;
            OrganizePattern = "{AlbumArtist}/{Album}/{TrackNo} {Title}";
            OrganizeTargetRoot = string.Empty;
            IncludePrereleaseUpdates = false;
            DeveloperMode = false;

            // Everything below was previously left at its pre-reset value, and because
            // SyncToSettings then wrote the stale VM state back over the freshly-defaulted
            // file, none of it actually reset.
            MinimizeToTray = defaultSettings.MinimizeToTray;
            CloseToTray = defaultSettings.CloseToTray;
            StartMinimizedToTray = defaultSettings.StartMinimizedToTray;
            RestoreLastTrackOnStartup = defaultSettings.RestoreLastTrackOnStartup;
            WebRemoteEnabled = defaultSettings.WebRemoteEnabled;
            CollapseAlbumEditions = defaultSettings.CollapseAlbumEditions;
            MergeFeaturedFromTitles = defaultSettings.MergeFeaturedFromTitles;
            ArtistGroupMode = defaultSettings.ArtistGroupMode;
            ReplaceArtistTagSeparators(defaultSettings.ArtistTagSeparators);
            EnableAnimatedCovers = defaultSettings.EnableAnimatedCovers;
            AlbumPageTintEnabled = defaultSettings.AlbumPageTintEnabled;
            NowPlayingArtworkStyle = defaultSettings.NowPlayingArtworkStyle;
            CoverFlowLayout = defaultSettings.CoverFlowLayout;
            MiniPlayerStyle = defaultSettings.MiniPlayerStyle;
            PlaybackBarShowSkipButtons = defaultSettings.PlaybackBarShowSkipButtons;
            PlaybackBarSkipSeconds = defaultSettings.PlaybackBarSkipSeconds;
            PlaybackBarShowPlaybackSpeed = defaultSettings.PlaybackBarShowPlaybackSpeed;
            PlaybackBarShowSleepTimer = defaultSettings.PlaybackBarShowSleepTimer;
            PlaybackBarShowShuffle = defaultSettings.PlaybackBarShowShuffle;
            PlaybackBarIslandWidth = defaultSettings.PlaybackBarWidth;
            LyricsFlowingLightEnabled = defaultSettings.LyricsFlowingLightEnabled;
            LyricsFlowingStyle = defaultSettings.LyricsFlowingStyle;
            LyricsVisualizerEnabled = defaultSettings.LyricsVisualizerEnabled;
            LyricsVisualizerStyle = defaultSettings.LyricsVisualizerStyle;
            LyricsVisualizerArtworkColor = defaultSettings.LyricsVisualizerArtworkColor;
            LanguageChoice = LanguageOptions[0];
            LyricsBackgroundMediaPath = defaultSettings.LyricsBackgroundMediaPath;
            LyricsFullScreenFocusEnabled = defaultSettings.LyricsFullScreenFocusEnabled;
            LyricsJoinSplitWords = defaultSettings.LyricsJoinSplitWords;
            FfmpegPath = defaultSettings.FfmpegPath;
            ExternalOpenAppPath = defaultSettings.ExternalOpenAppPath;
            ReplayGainPreampDb = defaultSettings.ReplayGainPreampDb;
            PlaybackBarBackgroundOpacity = defaultSettings.PlaybackBarBackgroundOpacity;
            MiniPlayerBackgroundOpacity = defaultSettings.MiniPlayerBackgroundOpacity;
            AlbumTileSizeAuto = defaultSettings.AlbumTileSizeAuto;
            AlbumTileTargetSize = defaultSettings.AlbumTileTargetSize;
            // Playback-bar width: clear the pending session value, or SyncToSettings
            // would re-persist the pre-reset width over the freshly defaulted file
            // (the ApplyPlayerSettings call below pushes the default to the bar).
            _playbackBarWidth = null;
            ProfileName = defaultSettings.ProfileName;
            ProfileAvatarPath = defaultSettings.ProfileAvatarPath;

            // Launch-at-login is an OS-level registration, not a settings field — a reset
            // that leaves it enabled means the app keeps starting itself after the user
            // asked for defaults.
            try { Helpers.StartupHelper.SetEnabled(false); } catch { }

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
            // Read from defaultSettings, not literals: these drifted from AppSettings the
            // moment a default changed, so "Reset to Defaults" stopped matching a fresh install.
            AutoplayEnabled = defaultSettings.AutoplayEnabled;
            AllowExplicitContent = defaultSettings.AllowExplicitContent;
            BpmKeyAnalysisEnabled = defaultSettings.BpmKeyAnalysisEnabled;
            WriteAnalysisToTags = defaultSettings.WriteAnalysisToTags;
            ReplayGainMode = defaultSettings.ReplayGainMode;
            TrackTitleMarqueeEnabled = true;
            ArtistMarqueeEnabled = true;
            CoverFlowMarqueeEnabled = true;
            CoverFlowArtistMarqueeEnabled = true;
            CoverFlowAlbumMarqueeEnabled = true;
            LyricsTitleMarqueeEnabled = true;
            LyricsArtistMarqueeEnabled = true;
            MiniPlayerTitleMarqueeEnabled = true;
            MiniPlayerAlbumMarqueeEnabled = true;
            SidebarHoverExpand = defaultSettings.SidebarHoverExpand;
            SidebarAlwaysExpanded = defaultSettings.SidebarAlwaysExpanded;
            LiquidGlassEnabled = defaultSettings.LiquidGlassEnabled;
            TaskbarProgressEnabled = defaultSettings.TaskbarProgressEnabled;

            // Lyrics providers
            LrcLibEnabled = true;
            NetEaseEnabled = true;

            // Metadata providers
            DeezerEnabled = true;
            MusicBrainzEnabled = true;

            // Equalizer
            _suppressEqNotify = true;
            EqualizerEnabled = true;
            SelectedEqPresetIndex = 1; // Flat
            SelectedEqPresetName = "Flat";
            EqPreampDb = defaultSettings.EqPreampDb;
            SyncCustomInVisiblePresets(false);
            SetEqBands(ParametricEqMath.FromGraphicBands(null));
            _suppressEqNotify = false;

            // Music folders
            MusicFolders.Clear();
            FolderRules.Clear();
            OnPropertyChanged(nameof(MediaFolderDisplay));

            // Integrations
            DiscordRichPresenceEnabled = false;
            LastFmScrobblingEnabled = defaultSettings.LastFmScrobblingEnabled;
            LastFmUsername = "";
            IsLastFmConnected = false;
            LastFmStatusText = "Not connected";
            // Without this the session key survived in the service and SyncToSettings
            // re-persisted it (`_settings.LastFmSessionKey = lfm.GetSessionKey()`), so the
            // account stayed connected while the UI read "Not connected".
            _lastFm?.Logout();

            ListenBrainzScrobblingEnabled = defaultSettings.ListenBrainzScrobblingEnabled;
            ListenBrainzToken = "";
            ListenBrainzUsername = "";
            IsListenBrainzConnected = false;
            ListenBrainzStatusText = "Not connected";
            _listenBrainz?.Logout();

            // Media server — drop the connection (SyncToSettings would otherwise
            // re-persist the stale one over the freshly defaulted file).
            _mediaServerConnection = null;
            MediaServerType = MediaServerTypeOptions[0];
            MediaServerUrl = "";
            MediaServerUsername = "";
            MediaServerPassword = "";
            IsMediaServerConnected = false;
            MediaServerStatusText = "Not connected";
            HasMediaServerError = false;
            _mediaServer?.SetActiveConnection(null);
            MediaServerConnectionChanged?.Invoke(this, EventArgs.Empty);

            // Disconnect Discord if connected (Loon rides its lifecycle)
            if (_discord != null)
            {
                _ = _discord.DisconnectAsync();
            }
            _ = DisconnectLoonAsync();

            // Apply audio settings
            ApplyAudioSettings();
            // Push the defaulted playback UI settings (incl. the bar width, which has
            // no OnChanged partial to fire) onto the player.
            ApplyPlayerSettings();

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
    private async Task ClearArtworkCache()
    {
        try
        {
            var artworkDir = Path.Combine(_persistence.DataDirectory, "artwork");
            // Task.Run: the recursive delete scales with library size (one file per
            // album) and blocked the UI thread for its whole duration on large
            // libraries or slow disks.
            await Task.Run(() =>
            {
                if (Directory.Exists(artworkDir))
                {
                    Directory.Delete(artworkDir, true);
                    Directory.CreateDirectory(artworkDir);
                    // ArtistImageService creates artwork/artists once, in its constructor, so
                    // recreating only the parent left every later artist-photo write throwing
                    // DirectoryNotFoundException into a swallowing catch — artist images
                    // silently stopped caching until the app was restarted. ConfirmResetLibrary
                    // already got this right.
                    Directory.CreateDirectory(Path.Combine(artworkDir, "artists"));
                }
            });
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
        IsUpToDate = false;
        DownloadProgress = 0;
        // The button itself now shows "Checking..." while polling, so keep the
        // separate status line empty for this state to avoid duplicate text.
        UpdateStatusText = "";
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
                // Show the result inline on the button ("✓ Up to date")
                // rather than as a separate status line.
                IsUpToDate = true;
                _ = ClearUpdateStatusAfterDelay(3000);
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
                UpdateStatusText = CanInstallInApp
                    ? $"{update.TagName} is available."
                    : $"{update.TagName} is available. {ExternalUpdateHint}";
                IsUpdateAvailable = true;
                DebugLog.Write("Updater", $"Update found: {update.TagName}");
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update check timed out. Try again later.";
            _ = ClearUpdateStatusAfterDelay();
        }
        catch (Exception ex)
        {
            UpdateStatusText = "Couldn't check for updates. Try again later.";
            _ = ClearUpdateStatusAfterDelay();
            DebugLog.Write("Updater", ex);
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
                update, progress, _updateCts.Token, requireChecksums: true);

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
        catch (Exception ex)
        {
            UpdateStatusText = "Download failed. Try again.";
            _ = ClearUpdateStatusAfterDelay();
            DebugLog.Write("Updater", ex);
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
            // Shut down the app so Inno Setup can replace files.
            // TryShutdown (not Shutdown) so ShutdownRequested fires and the
            // graceful-save handler runs — Shutdown() skips it and loses the
            // queue snapshot / history flush / final scrobble on every update.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.TryShutdown(0);
            }
        }
        else
        {
            UpdateStatusText = "Couldn't start installer. Download manually from GitHub.";
            IsReadyToInstall = false;
        }
    }

    private async Task ClearUpdateStatusAfterDelay(int delayMs = 5000)
    {
        await Task.Delay(delayMs);
        if (!IsUpdateAvailable && !IsDownloadingUpdate && !IsReadyToInstall)
        {
            UpdateStatusText = "";
            IsUpToDate = false;   // reverts the button label to "Update"
        }
    }

    // ── Developer Mode (About tab) ──

    [ObservableProperty] private bool _developerMode;

    /// <summary>Rows shown by the version manager: the newest few releases, plus the
    /// rest once "Show older versions" is clicked.</summary>
    public ObservableCollection<DevReleaseItem> DevReleases { get; } = new();

    /// <summary>Full fetched release list backing <see cref="DevReleases"/>.</summary>
    private readonly List<DevReleaseItem> _allReleases = new();

    /// <summary>How many releases show before the "Show older versions" link.</summary>
    private const int VisibleReleaseLimit = 8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenReleases))]
    [NotifyPropertyChangedFor(nameof(ShowOlderVersionsLabel))]
    private int _hiddenReleaseCount;

    public bool HasHiddenReleases => HiddenReleaseCount > 0;

    public string ShowOlderVersionsLabel => $"Show {HiddenReleaseCount} older versions";

    [ObservableProperty] private bool _isLoadingReleases;
    [ObservableProperty] private bool _showDevReleasesEmpty;
    [ObservableProperty] private string _devStatusText = "";
    [ObservableProperty] private bool _isDevDownloading;
    [ObservableProperty] private double _devDownloadProgress;
    [ObservableProperty] private string _devLogText = "";

    /// <summary>Banner above the log pane when a previous session died and its
    /// log was preserved; null hides it. Only the Clear button removes it.</summary>
    [ObservableProperty] private string? _preservedCrashBanner;

    /// <summary>The log pane / Copy Logs content: any preserved crash log from a
    /// previous session first, then the live session log.</summary>
    private static string ComposeDevLogText()
        => CrashJournal.PreservedBlock is { } preserved
            ? preserved + Environment.NewLine + DebugLog.Snapshot()
            : DebugLog.Snapshot();
    [ObservableProperty] private bool _devLogsCopied;

    private CancellationTokenSource? _devCts;

    partial void OnDeveloperModeChanged(bool value)
    {
        _settings.DeveloperMode = value;
        _ = SaveAsync();

        // Mirror LibVLC warnings/errors into the session log while dev mode is
        // on, so "Copy Logs" captures audio-engine complaints (see DebugLog).
        DebugLog.VlcBridgeEnabled = value;

        if (value)
        {
            // Dev mode used to enable the VLC bridge but leave DebugLogger off, so
            // every Playback/KeepAlive/SessionVolume entry was a no-op in the one log
            // users actually send — a device change mid-dropout left no trace at all.
            // Only ever turned on here: the debug panel enables it independently and
            // deliberately keeps it on after closing, so switching dev mode off must
            // not silently kill logging someone else asked for.
            DebugLogger.IsEnabled = true;
            DebugLogger.MirrorPlaybackToSessionLog = true;

            PreservedCrashBanner = CrashJournal.PreservedBanner;
            DevLogText = ComposeDevLogText();
            _ = RefreshReleasesAsync();
        }
    }

    /// <summary>Version equality ignoring the assembly's 4th (revision) component.</summary>
    private static bool IsSameVersion(Version a, Version b) =>
        a.Major == b.Major && a.Minor == b.Minor && Math.Max(a.Build, 0) == Math.Max(b.Build, 0);

    private async Task RefreshReleasesAsync()
    {
        if (_updateService is null || IsLoadingReleases) return;

        IsLoadingReleases = true;
        ShowDevReleasesEmpty = false;
        DevStatusText = "";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var releases = await _updateService.ListReleasesAsync(cts.Token);

            var current = UpdateService.CurrentVersion;
            var latest = UpdateService.PickLatestRelease(releases);
            _allReleases.Clear();
            foreach (var release in releases)
            {
                var isCurrent = IsSameVersion(release.Version, current);
                _allReleases.Add(new DevReleaseItem
                {
                    TagName = release.Info.TagName,
                    VersionDisplay = $"{release.Version.Major}.{release.Version.Minor}.{release.Version.Build}",
                    DateDisplay = release.PublishedAt?.ToLocalTime().ToString("MMM d, yyyy") ?? "",
                    IsPrerelease = release.Info.IsPrerelease,
                    IsCurrent = isCurrent,
                    IsLatest = ReferenceEquals(release, latest),
                    CanInstall = !isCurrent
                                 && UpdateService.SupportsInAppUpdate
                                 && release.Info.InstallerApiUrl is not null,
                    WarningText = release.WarningText,
                    Info = release.Info
                });
            }

            DevReleases.Clear();
            foreach (var item in _allReleases.Take(VisibleReleaseLimit))
                DevReleases.Add(item);
            HiddenReleaseCount = Math.Max(0, _allReleases.Count - VisibleReleaseLimit);

            ShowDevReleasesEmpty = DevReleases.Count == 0;
            DebugLog.Write("VersionManager", $"Loaded {_allReleases.Count} releases from GitHub.");
        }
        catch (Exception ex)
        {
            DevStatusText = "Couldn't load releases. Try again later.";
            ShowDevReleasesEmpty = DevReleases.Count == 0;
            DebugLog.Write("VersionManager", ex);
        }
        finally
        {
            IsLoadingReleases = false;
        }
    }

    /// <summary>Expands the version list to the full fetched history.</summary>
    [RelayCommand]
    private void ShowOlderReleases()
    {
        foreach (var item in _allReleases.Skip(DevReleases.Count))
            DevReleases.Add(item);
        HiddenReleaseCount = 0;
    }

    /// <summary>
    /// Installs the picked release through the same verified pipeline as a normal
    /// update (size + SHA-256 checks), then shuts down so the installer can swap
    /// files. Copies that can't self-install open the release page instead.
    /// </summary>
    [RelayCommand]
    private async Task InstallReleaseAsync(DevReleaseItem? item)
    {
        if (item is null || _updateService is null || IsDevDownloading) return;

        if (!item.CanInstall)
        {
            // Copies that can't self-install: download the installer in-app (same
            // verified pipeline, with progress + cancel) into the user's Downloads
            // folder. Releases without a matching asset open the release page.
            if (item.Info.InstallerApiUrl is null)
                Helpers.PlatformHelper.OpenUrl(item.Info.ReleaseUrl);
            else
                await DownloadReleaseToDownloadsAsync(item);
            return;
        }

        IsDevDownloading = true;
        DevDownloadProgress = 0;
        DevStatusText = $"Downloading {item.TagName}...";
        DebugLog.Write("VersionManager", $"Installing {item.TagName}...");

        try
        {
            _devCts?.Cancel();
            _devCts?.Dispose();
            _devCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var progress = new Progress<double>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    DevDownloadProgress = p;
                    DevStatusText = $"Downloading {item.TagName}... {p:F0}%";
                }));

            // requireChecksums, same as the normal update path. This defaulted to false,
            // so any listed release without a SHA256SUMS asset had a ~100 MB executable
            // run *elevated* after only a Content-Length equality check.
            var installerPath = await _updateService.DownloadInstallerAsync(
                item.Info, progress, _devCts.Token, requireChecksums: true);

            DevStatusText = $"Installing {item.TagName}...";
            if (_updateService.LaunchInstaller(installerPath))
            {
                // TryShutdown so the ShutdownRequested save handler runs (see InstallUpdate).
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.TryShutdown(0);
                }
            }
            else
            {
                DevStatusText = "Couldn't start installer. Download manually from GitHub.";
                DebugLog.Write("VersionManager", "LaunchInstaller returned false.");
            }
        }
        catch (OperationCanceledException)
        {
            DevStatusText = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DevStatusText = "Download failed. Try again.";
            DebugLog.Write("VersionManager", ex);
        }
        finally
        {
            IsDevDownloading = false;
        }
    }

    /// <summary>
    /// Downloads a release's installer to the user's Downloads folder through the
    /// verified pipeline (GitHub-pinned URL, size + SHA-256 checks), then reveals
    /// the file. Used when this copy can't install in place (Scoop / portable).
    /// </summary>
    private async Task DownloadReleaseToDownloadsAsync(DevReleaseItem item)
    {
        if (_updateService is null) return;

        IsDevDownloading = true;
        DevDownloadProgress = 0;
        DevStatusText = $"Downloading {item.TagName}...";

        try
        {
            _devCts?.Cancel();
            _devCts?.Dispose();
            _devCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            // The asset name comes from the GitHub API response, and the asset filter
            // only checks a prefix and a suffix — so "Noctis-..\..\..\evil-Setup.exe"
            // passes it and escapes Downloads (Path.Combine also discards the base
            // entirely if the name is rooted). Reject anything that isn't a bare
            // filename and fall back to a locally-derived name.
            var assetName = item.Info.InstallerAssetName;
            if (string.IsNullOrWhiteSpace(assetName) ||
                !string.Equals(Path.GetFileName(assetName), assetName, StringComparison.Ordinal))
            {
                assetName = $"Noctis-{Helpers.TitleFormatter.SanitizeForFilename(item.TagName)}-installer";
            }

            var destination = Path.Combine(downloads, assetName);

            var progress = new Progress<double>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    DevDownloadProgress = p;
                    DevStatusText = $"Downloading {item.TagName}... {p:F0}%";
                }));

            var path = await _updateService.DownloadInstallerAsync(
                item.Info, progress, _devCts.Token, destination);

            DevStatusText = $"{item.TagName} saved to Downloads.";
            DebugLog.Write("VersionManager", $"Downloaded {item.TagName} to {path}");
            Helpers.PlatformHelper.ShowInFileManager(path);
        }
        catch (OperationCanceledException)
        {
            DevStatusText = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DevStatusText = "Download failed. Try again.";
            DebugLog.Write("VersionManager", ex);
        }
        finally
        {
            IsDevDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDevDownload() => _devCts?.Cancel();

    [RelayCommand]
    private async Task CopyDevLogsAsync()
    {
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null) return;

        try { await clipboard.SetTextAsync(ComposeDevLogText()); } catch { return; }

        DevLogsCopied = true;
        await Task.Delay(1500);
        DevLogsCopied = false;
    }

    [RelayCommand]
    private void ClearDevLogs()
    {
        CrashJournal.ClearPreserved();
        PreservedCrashBanner = null;
        DebugLog.Clear();
        DevLogText = ComposeDevLogText();
    }

    /// <summary>Opens the app data folder, which also holds crash.log.</summary>
    [RelayCommand]
    private void OpenLogsFolder() => Helpers.PlatformHelper.OpenFolder(Helpers.AppPaths.DataRoot);

    [RelayCommand]
    private void OpenGitHub()
    {
        Helpers.PlatformHelper.OpenUrl("https://github.com/heartached/Noctis");
    }

    /// <summary>True briefly after the version is copied — drives the inline
    /// "Copied!" confirmation next to the version number.</summary>
    [ObservableProperty] private bool _versionCopied;

    /// <summary>Copies version + OS/arch info to the clipboard (handy for bug reports).</summary>
    [RelayCommand]
    private async Task CopyVersionInfoAsync()
    {
        var v = UpdateService.CurrentVersion;
        var info = $"Noctis {v.Major}.{v.Minor}.{v.Build} — " +
                   $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                   $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null) return;

        try { await clipboard.SetTextAsync(info); } catch { return; }

        VersionCopied = true;
        await Task.Delay(1500);
        VersionCopied = false;
    }
    /// <summary>Opens the GitHub release page for the available update — used by
    /// Scoop / portable copies that can't safely run the in-app installer.</summary>
    [RelayCommand]
    private void OpenLatestRelease()
    {
        var url = string.IsNullOrEmpty(LatestVersionTag)
            ? "https://github.com/heartached/Noctis/releases/latest"
            : $"https://github.com/heartached/Noctis/releases/tag/{LatestVersionTag}";
        Helpers.PlatformHelper.OpenUrl(url);
    }

    [RelayCommand]
    private void OpenDiscord()
    {
        Helpers.PlatformHelper.OpenUrl("https://discord.gg/BNCDZQUVx7");
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        Helpers.PlatformHelper.OpenUrl("https://noctisapp.cc/");
    }

    [RelayCommand]
    private void OpenStatisticsPage()
    {
        OpenStatisticsRequested?.Invoke(this, EventArgs.Empty);
    }
}

