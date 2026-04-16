using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Loon;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the unified Settings page (single scrollable view).
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
    private ArtistImageService? _artistImageService;
    private UpdateService? _updateService;
    private CancellationTokenSource? _updateCts;
    private string? _downloadedInstallerPath;
    private bool _lastFmAuthInProgress;
    private bool _settingsLoaded;
    private bool _suspendSettingPersistence;
    private CancellationTokenSource? _eqSaveDebounceCts;
    private CancellationTokenSource? _scanStatusClearCts;

    private AppSettings _settings;

    // ── Appearance ──

    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private bool _isSystemTheme;

    // ── Audio Playback ──

    [ObservableProperty] private bool _crossfadeEnabled;
    [ObservableProperty] private double _crossfadeDuration = 6;
    [ObservableProperty] private bool _soundCheckEnabled;
    [ObservableProperty] private bool _trackTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _artistMarqueeEnabled = true;
    [ObservableProperty] private bool _menuTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _menuArtistMarqueeEnabled = true;
    [ObservableProperty] private bool _coverFlowMarqueeEnabled = true;
    [ObservableProperty] private bool _coverFlowArtistMarqueeEnabled = true;
    [ObservableProperty] private bool _coverFlowAlbumMarqueeEnabled = true;
    [ObservableProperty] private bool _lyricsTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _lyricsArtistMarqueeEnabled = true;

    // ── Lyrics Providers ──

    [ObservableProperty] private bool _lrcLibEnabled = true;
    [ObservableProperty] private bool _netEaseEnabled = true;

    // ── Equalizer ──

    [ObservableProperty] private bool _equalizerEnabled = true;
    [ObservableProperty] private int _selectedEqPresetIndex = 1; // 0 = Custom, 1 = Flat, 2+ = VLC preset
    [ObservableProperty] private string _selectedEqPresetName = "Flat";

    /// <summary>Preset names shown in the dropdown. "Custom" is only present while bands deviate from a preset.</summary>
    public ObservableCollection<string> VisibleEqPresets { get; } = CreateDefaultVisiblePresets();

    private static ObservableCollection<string> CreateDefaultVisiblePresets()
    {
        var list = new ObservableCollection<string>();
        for (int i = 1; i < EqPresetNames.Length; i++)
            list.Add(EqPresetNames[i]);
        return list;
    }
    [ObservableProperty] private float _eqBand0;
    [ObservableProperty] private float _eqBand1;
    [ObservableProperty] private float _eqBand2;
    [ObservableProperty] private float _eqBand3;
    [ObservableProperty] private float _eqBand4;
    [ObservableProperty] private float _eqBand5;
    [ObservableProperty] private float _eqBand6;
    [ObservableProperty] private float _eqBand7;
    [ObservableProperty] private float _eqBand8;
    [ObservableProperty] private float _eqBand9;

    private bool _suppressEqNotify;
    private const int EqSaveDebounceMs = 280;

    /// <summary>VLC band center frequencies for display labels.</summary>
    public static readonly string[] EqBandLabels =
        { "60Hz", "170Hz", "310Hz", "600Hz", "1K", "3K", "6K", "12K", "14K", "16K" };

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

    // ── Preferences ──

    [ObservableProperty] private bool _scanOnStartup = true;

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

    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdate;
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _isReadyToInstall;
    [ObservableProperty] private string _latestVersionTag = "";

    // ── Events ──

    /// <summary>Fires when the theme changes so the App can update.</summary>
    public event EventHandler<bool>? ThemeChanged;

    /// <summary>Fires after a full settings reset so the shell can reload playlists, etc.</summary>
    public event EventHandler? SettingsReset;

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
                ScanStatusText = $"Scanning Library {count}...";
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
    }

    /// <summary>Sets the audio player reference for applying audio settings.</summary>
    public void SetAudioPlayer(IAudioPlayer audioPlayer)
    {
        _audioPlayer = audioPlayer;
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

            // Theme
            switch (_settings.Theme)
            {
                case "Light":
                    IsLightTheme = true;
                    IsDarkTheme = false;
                    IsSystemTheme = false;
                    break;
                case "System":
                    IsSystemTheme = true;
                    IsDarkTheme = false;
                    IsLightTheme = false;
                    break;
                default:
                    IsDarkTheme = true;
                    IsLightTheme = false;
                    IsSystemTheme = false;
                    break;
            }

            ScanOnStartup = _settings.ScanOnStartup;

            // Playback
            CrossfadeEnabled = _settings.CrossfadeEnabled;
            CrossfadeDuration = Math.Clamp(_settings.CrossfadeDuration, 1, 12);
            SoundCheckEnabled = _settings.SoundCheckEnabled;
            TrackTitleMarqueeEnabled = _settings.TrackTitleMarqueeEnabled;
            ArtistMarqueeEnabled = _settings.ArtistMarqueeEnabled;
            MenuTitleMarqueeEnabled = _settings.MenuTitleMarqueeEnabled;
            MenuArtistMarqueeEnabled = _settings.MenuArtistMarqueeEnabled;
            CoverFlowMarqueeEnabled = _settings.CoverFlowMarqueeEnabled;
            CoverFlowArtistMarqueeEnabled = _settings.CoverFlowArtistMarqueeEnabled;
            CoverFlowAlbumMarqueeEnabled = _settings.CoverFlowAlbumMarqueeEnabled;
            LyricsTitleMarqueeEnabled = _settings.LyricsTitleMarqueeEnabled;
            LyricsArtistMarqueeEnabled = _settings.LyricsArtistMarqueeEnabled;

            // Lyrics providers
            LrcLibEnabled = _settings.LrcLibEnabled;
            NetEaseEnabled = _settings.NetEaseEnabled;

            // Equalizer
            _suppressEqNotify = true;
            EqualizerEnabled = _settings.EqualizerEnabled;
            int loadedIdx = Math.Clamp(_settings.EqualizerPresetIndex + 1, 0, EqPresetNames.Length - 1);
            SelectedEqPresetIndex = loadedIdx;
            SelectedEqPresetName = EqPresetNames[loadedIdx];
            SyncCustomInVisiblePresets(loadedIdx == 0);
            if (_settings.EqualizerBands is { Length: 10 })
            {
                EqBand0 = _settings.EqualizerBands[0];
                EqBand1 = _settings.EqualizerBands[1];
                EqBand2 = _settings.EqualizerBands[2];
                EqBand3 = _settings.EqualizerBands[3];
                EqBand4 = _settings.EqualizerBands[4];
                EqBand5 = _settings.EqualizerBands[5];
                EqBand6 = _settings.EqualizerBands[6];
                EqBand7 = _settings.EqualizerBands[7];
                EqBand8 = _settings.EqualizerBands[8];
                EqBand9 = _settings.EqualizerBands[9];
            }
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

            if (_discord != null && DiscordRichPresenceEnabled)
            {
                _ = _discord.ConnectAsync();
            }

            // Ensure player gets the persisted startup settings even if no toggle changed.
            ApplyAudioSettings();

            // Apply the persisted theme on startup
            var isDark = IsSystemTheme ? IsSystemDarkMode() : IsDarkTheme;
            ThemeChanged?.Invoke(this, isDark);

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
        if (IsDarkTheme) _settings.Theme = "Dark";
        else if (IsLightTheme) _settings.Theme = "Light";
        else _settings.Theme = "System";

        _settings.ScanOnStartup = ScanOnStartup;
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
        _settings.CrossfadeEnabled = CrossfadeEnabled;
        _settings.CrossfadeDuration = Math.Clamp(CrossfadeDuration, 1, 12);
        _settings.SoundCheckEnabled = SoundCheckEnabled;
        _settings.TrackTitleMarqueeEnabled = TrackTitleMarqueeEnabled;
        _settings.ArtistMarqueeEnabled = ArtistMarqueeEnabled;
        _settings.MenuTitleMarqueeEnabled = MenuTitleMarqueeEnabled;
        _settings.MenuArtistMarqueeEnabled = MenuArtistMarqueeEnabled;
        _settings.CoverFlowMarqueeEnabled = CoverFlowMarqueeEnabled;
        _settings.CoverFlowArtistMarqueeEnabled = CoverFlowArtistMarqueeEnabled;
        _settings.CoverFlowAlbumMarqueeEnabled = CoverFlowAlbumMarqueeEnabled;
        _settings.LyricsTitleMarqueeEnabled = LyricsTitleMarqueeEnabled;
        _settings.LyricsArtistMarqueeEnabled = LyricsArtistMarqueeEnabled;
        _settings.LrcLibEnabled = LrcLibEnabled;
        _settings.NetEaseEnabled = NetEaseEnabled;
        _settings.EqualizerEnabled = EqualizerEnabled;
        _settings.EqualizerPresetIndex = SelectedEqPresetIndex - 1;
        _settings.EqualizerBands = GetEqBands();
        _settings.DiscordRichPresenceEnabled = DiscordRichPresenceEnabled;
        _settings.LastFmScrobblingEnabled = LastFmScrobblingEnabled;
        _settings.LastFmUsername = LastFmUsername;
        if (_lastFm is LastFmService lfm)
            _settings.LastFmSessionKey = lfm.GetSessionKey() ?? "";
    }

    /// <summary>Returns the loaded settings object.</summary>
    public AppSettings GetSettings() => _settings;

    /// <summary>Updates the volume setting in the internal settings object.</summary>
    public void SetVolume(int volume) => _settings.Volume = volume;

    private void ApplyAudioSettings()
    {
        _audioPlayer?.SetNormalization(SoundCheckEnabled);
        _audioPlayer?.SetCrossfade(CrossfadeEnabled, (int)Math.Round(CrossfadeDuration));
        int vlcPresetIndex = SelectedEqPresetIndex - 1;
        _audioPlayer?.SetAdvancedEqualizer(EqualizerEnabled, vlcPresetIndex, GetEqBands());
    }

    private void ApplyPlayerSettings()
    {
        if (_player == null) return;
        _player.TrackTitleMarqueeEnabled = TrackTitleMarqueeEnabled;
        _player.ArtistMarqueeEnabled = ArtistMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalMenuTitleScrollEnabled = MenuTitleMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalMenuArtistScrollEnabled = MenuArtistMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalCoverFlowScrollEnabled = CoverFlowMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalCoverFlowArtistScrollEnabled = CoverFlowArtistMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalCoverFlowAlbumScrollEnabled = CoverFlowAlbumMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalLyricsTitleScrollEnabled = LyricsTitleMarqueeEnabled;
        Controls.MarqueeTextBlock.GlobalLyricsArtistScrollEnabled = LyricsArtistMarqueeEnabled;
    }

    private void ApplyEqualizer()
    {
        int vlcPresetIndex = SelectedEqPresetIndex - 1;
        _audioPlayer?.SetAdvancedEqualizer(EqualizerEnabled, vlcPresetIndex, GetEqBands());
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
        if (index < 0) { ApplyEqualizer(); return; }

        // VLC preset index: our index - 1 (index 0 = "Custom", 1 = "Flat" = VLC preset 0)
        // Pass the preset index directly — VLC loads the preset's own bands.
        int vlcPresetIndex = index - 1;
        _audioPlayer?.SetAdvancedEqualizer(true, vlcPresetIndex, new float[10]);
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

    private float[] GetEqBands() =>
        new[] { EqBand0, EqBand1, EqBand2, EqBand3, EqBand4, EqBand5, EqBand6, EqBand7, EqBand8, EqBand9 };

    private void SetEqBands(float[] bands)
    {
        if (bands is not { Length: 10 }) return;
        _suppressEqNotify = true;
        EqBand0 = bands[0]; EqBand1 = bands[1]; EqBand2 = bands[2]; EqBand3 = bands[3]; EqBand4 = bands[4];
        EqBand5 = bands[5]; EqBand6 = bands[6]; EqBand7 = bands[7]; EqBand8 = bands[8]; EqBand9 = bands[9];
        _suppressEqNotify = false;
    }

    // ── Theme commands ──

    [RelayCommand]
    private void SetDarkTheme()
    {
        IsDarkTheme = true;
        IsLightTheme = false;
        IsSystemTheme = false;
        ThemeChanged?.Invoke(this, true);
        _ = SaveAsync();
    }

    [RelayCommand]
    private void SetLightTheme()
    {
        IsDarkTheme = false;
        IsLightTheme = true;
        IsSystemTheme = false;
        ThemeChanged?.Invoke(this, false);
        _ = SaveAsync();
    }

    [RelayCommand]
    private void SetSystemTheme()
    {
        IsDarkTheme = false;
        IsLightTheme = false;
        IsSystemTheme = true;
        var isDark = IsSystemDarkMode();
        ThemeChanged?.Invoke(this, isDark);
        _ = SaveAsync();
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

    partial void OnCrossfadeEnabledChanged(bool value)
    {
        ApplyAudioSettings();
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

    partial void OnMenuTitleMarqueeEnabledChanged(bool value)
    {
        ApplyPlayerSettings();
        _ = SaveAsync();
    }

    partial void OnMenuArtistMarqueeEnabledChanged(bool value)
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
        if (_lastFmAuthInProgress) return;

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
        _ = PollLastFmAuthAsync();
    }

    private async Task PollLastFmAuthAsync()
    {
        if (_lastFm == null) return;
        if (_lastFmAuthInProgress) return;
        _lastFmAuthInProgress = true;
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            var failedAttempts = 0;
            while (DateTime.UtcNow < deadline)
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

                await Task.Delay(2000);
            }

            if (!IsLastFmConnected)
                LastFmStatusText = "Not connected";
        }
        finally
        {
            _lastFmAuthInProgress = false;
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

        if (value > 0)
            LoadPresetBands(value - 1);

        ApplyEqualizer();
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

        if (idx > 0)
        {
            LoadPresetBands(idx - 1);
            SyncCustomInVisiblePresets(false);
        }

        ApplyEqualizer();
        QueueEqualizerSave();
    }

    private void SyncCustomInVisiblePresets(bool shouldShowCustom)
    {
        bool hasCustom = VisibleEqPresets.Count > 0 && VisibleEqPresets[0] == "Custom";
        if (shouldShowCustom && !hasCustom)
            VisibleEqPresets.Insert(0, "Custom");
        else if (!shouldShowCustom && hasCustom)
            VisibleEqPresets.RemoveAt(0);
    }

    private void LoadPresetBands(int vlcPresetIndex)
    {
        try
        {
            using var tempEq = new LibVLCSharp.Shared.Equalizer((uint)vlcPresetIndex);
            var bands = new float[10];
            for (uint i = 0; i < 10; i++)
                bands[i] = Math.Clamp(tempEq.Amp(i), -12f, 12f);
            SetEqBands(bands);
        }
        catch
        {
            SetEqBands(new float[10]);
        }
    }

    private void OnEqBandChanged()
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

    partial void OnEqBand0Changed(float value) => OnEqBandChanged();
    partial void OnEqBand1Changed(float value) => OnEqBandChanged();
    partial void OnEqBand2Changed(float value) => OnEqBandChanged();
    partial void OnEqBand3Changed(float value) => OnEqBandChanged();
    partial void OnEqBand4Changed(float value) => OnEqBandChanged();
    partial void OnEqBand5Changed(float value) => OnEqBandChanged();
    partial void OnEqBand6Changed(float value) => OnEqBandChanged();
    partial void OnEqBand7Changed(float value) => OnEqBandChanged();
    partial void OnEqBand8Changed(float value) => OnEqBandChanged();
    partial void OnEqBand9Changed(float value) => OnEqBandChanged();

    [RelayCommand]
    private void ResetEqualizer()
    {
        _suppressEqNotify = true;
        SelectedEqPresetIndex = 1; // "Flat"
        SelectedEqPresetName = "Flat";
        SyncCustomInVisiblePresets(false);
        _suppressEqNotify = false;

        SetEqBands(new float[10]);
        ApplyEqualizer();
        QueueEqualizerSave();
    }

    // ── Library overview + Storage ──

    public void RefreshLibraryStats()
    {
        var tracks = _library.Tracks;
        TotalSongs = tracks.Count;
        TotalArtists = _library.Artists.Count;
        TotalAlbums = _library.Albums.Count;

        // Library size
        var totalBytes = tracks.Sum(t => t.FileSize);
        TotalFileSize = FormatLibrarySize(totalBytes);

        // Total duration
        var totalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        TotalListeningTime = FormatDuration(totalDuration);

        // Audio quality
        LosslessCount = tracks.Count(t => t.IsLossless);
        LossyCount = tracks.Count - LosslessCount;
        HiResCount = tracks.Count(t => t.IsHiResLossless);
        if (tracks.Count > 0)
        {
            var pct = (double)LosslessCount / tracks.Count;
            LosslessPercentage = pct;
            LosslessPercentageText = $"{pct * 100:F0}%";
            LossyPercentageText = $"{(1 - pct) * 100:F0}%";
            HiResPercentageText = $"{(double)HiResCount / tracks.Count * 100:F0}%";
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

    public void RefreshStorageInfo()
    {
        var dataDir = _persistence.DataDirectory;
        if (!Directory.Exists(dataDir)) return;

        long librarySize = GetFileSize(Path.Combine(dataDir, "library.json"));
        long queueSize = GetFileSize(Path.Combine(dataDir, "queue.json"));
        long playlistsSize = GetFileSize(Path.Combine(dataDir, "playlists.json"));
        long settingsSize = GetFileSize(Path.Combine(dataDir, "settings.json"));
        long artworkSize = GetDirectorySize(Path.Combine(dataDir, "artwork"));

        StorageLibraryData = FormatBytes(librarySize + queueSize);
        StorageArtwork = FormatBytes(artworkSize);
        StoragePlaylists = FormatBytes(playlistsSize);
        StorageSettings = FormatBytes(settingsSize);
        StorageTotal = FormatBytes(librarySize + queueSize + playlistsSize + settingsSize + artworkSize);
    }

    private static long GetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception ex) { Debug.WriteLine($"[Settings] GetFileSize failed for '{path}': {ex.Message}"); return 0; }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
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
    }

    [RelayCommand]
    private async Task RemoveFolder(string folder)
    {
        MusicFolders.Remove(folder);
        _settings.MusicFolders = MusicFolders.ToList();
        OnPropertyChanged(nameof(MediaFolderDisplay));
        await SaveAsync();
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
            RefreshStorageInfo();
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
            RefreshStorageInfo();
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to clear artwork cache: {ex.Message}");
        }

        // Clear lyrics cache
        try
        {
            var lyricsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Noctis", "lyrics_cache");
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

            // Theme
            IsDarkTheme = true;
            IsLightTheme = false;
            IsSystemTheme = false;

            // Preferences
            ScanOnStartup = true;

            // Playback
            CrossfadeEnabled = false;
            CrossfadeDuration = 6;
            SoundCheckEnabled = false;
            TrackTitleMarqueeEnabled = true;
            ArtistMarqueeEnabled = true;
            MenuTitleMarqueeEnabled = true;
            MenuArtistMarqueeEnabled = true;
            CoverFlowMarqueeEnabled = true;
            CoverFlowArtistMarqueeEnabled = true;
            CoverFlowAlbumMarqueeEnabled = true;
            LyricsTitleMarqueeEnabled = true;
            LyricsArtistMarqueeEnabled = true;

            // Lyrics providers
            LrcLibEnabled = true;
            NetEaseEnabled = true;

            // Equalizer
            _suppressEqNotify = true;
            EqualizerEnabled = true;
            SelectedEqPresetIndex = 1; // Flat
            SelectedEqPresetName = "Flat";
            SyncCustomInVisiblePresets(false);
            EqBand0 = 0; EqBand1 = 0; EqBand2 = 0; EqBand3 = 0; EqBand4 = 0;
            EqBand5 = 0; EqBand6 = 0; EqBand7 = 0; EqBand8 = 0; EqBand9 = 0;
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

            // Disconnect Discord if connected
            if (_discord != null)
            {
                _ = _discord.DisconnectAsync();
            }

            // Apply audio settings
            ApplyAudioSettings();

            // Apply theme
            ThemeChanged?.Invoke(this, true); // Dark theme
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

            var update = await _updateService.CheckForUpdateAsync(_updateCts.Token);

            if (update is null)
            {
                UpdateStatusText = "You're on the latest version.";
                _ = ClearUpdateStatusAfterDelay();
            }
            else if (update.InstallerApiUrl is null && update.InstallerUrl is null)
            {
                LatestVersionTag = update.TagName;
                UpdateStatusText = $"{update.TagName} available — installer not found. Visit GitHub.";
            }
            else
            {
                LatestVersionTag = update.TagName;
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
            var update = await _updateService.CheckForUpdateAsync(_updateCts.Token);
            var downloadUrl = update?.InstallerApiUrl ?? update?.InstallerUrl;
            if (downloadUrl is null)
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
                downloadUrl, update!.InstallerSize, progress, _updateCts.Token);

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
}

