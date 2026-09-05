using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsStudio;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Noctis.Services.YouTube;

namespace Noctis.ViewModels;

/// <summary>
/// Settings for the 2026-09 feature batch: Account &amp; Sync tab, multi-channel upmix,
/// YouTube downloads and Lyrics Studio. Kept in a partial so the main file only carries
/// the tab plumbing and the load/save hooks.
/// </summary>
public partial class SettingsViewModel
{
    // ── Multi-channel upmix (Audio tab, Windows gapless engine) ──

    public bool IsUpmixSupported => OperatingSystem.IsWindows();

    [ObservableProperty] private string _upmixMode = "Off";

    public bool IsUpmixOff { get => UpmixMode == "Off"; set { if (value) UpmixMode = "Off"; } }
    public bool IsUpmixDuplicate { get => UpmixMode == "Duplicate"; set { if (value) UpmixMode = "Duplicate"; } }
    public bool IsUpmixSurround { get => UpmixMode == "Surround"; set { if (value) UpmixMode = "Surround"; } }

    partial void OnUpmixModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsUpmixOff));
        OnPropertyChanged(nameof(IsUpmixDuplicate));
        OnPropertyChanged(nameof(IsUpmixSurround));
        if (!_settingsLoaded) return;
        _audioPlayer?.SetUpmixMode(value);
        _ = SaveAsync();
    }

    // ── Account & Sync ──

    private ILibrarySyncService? _sync;
    private ILibrarySyncService? Sync => _sync ??= ResolveSync();

    private ILibrarySyncService? ResolveSync()
    {
        var sync = App.Services?.GetService<ILibrarySyncService>();
        if (sync is not null)
            sync.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshSyncStatus);
        return sync;
    }

    [ObservableProperty] private bool _syncEnabled;
    [ObservableProperty] private string _syncDeviceName = string.Empty;
    [ObservableProperty] private string _syncStatusText = string.Empty;
    [ObservableProperty] private string _syncDeviceIdText = string.Empty;
    public ObservableCollection<SyncDeviceRow> SyncDevices { get; } = new();
    public bool HasSyncDevices => SyncDevices.Count > 0;

    /// <summary>The account this computer's owner uses on their devices: the first admin, else the first account.</summary>
    public ServerUser? PrimaryAccount => ServerUsersList.FirstOrDefault(u => u.IsAdmin) ?? ServerUsersList.FirstOrDefault();
    public bool HasPrimaryAccount => PrimaryAccount is not null;
    public string PrimaryAccountInitial => PrimaryAccount is { Name.Length: > 0 } u ? u.Name[..1].ToUpperInvariant() : "?";
    public string PrimaryAccountSince => PrimaryAccount is { } u ? $"Since {u.CreatedUtc.ToLocalTime():MMMM yyyy}" : string.Empty;
    public IReadOnlyList<ServerUser> OtherAccounts => ServerUsersList.Where(u => !ReferenceEquals(u, PrimaryAccount)).ToList();
    public bool HasOtherAccounts => OtherAccounts.Count > 0;

    [ObservableProperty] private bool _isPairingVisible;
    [ObservableProperty] private bool _isChangePasswordVisible;
    [ObservableProperty] private string _changePasswordValue = string.Empty;

    /// <summary>Called by the main file whenever the account list is rebuilt.</summary>
    private void RaiseAccountDerivedProperties()
    {
        OnPropertyChanged(nameof(PrimaryAccount));
        OnPropertyChanged(nameof(HasPrimaryAccount));
        OnPropertyChanged(nameof(PrimaryAccountInitial));
        OnPropertyChanged(nameof(PrimaryAccountSince));
        OnPropertyChanged(nameof(OtherAccounts));
        OnPropertyChanged(nameof(HasOtherAccounts));
    }

    partial void OnSyncEnabledChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.SyncEnabled = value;
        _ = SaveAsync();
        if (value)
        {
            // First enable: put the existing favourites/ratings/play counts into the ledger so
            // a phone that connects today sees the library as it is, not only future changes.
            var tracks = _library.Tracks.ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    var playlists = await _persistence.LoadPlaylistsAsync();
                    if (Sync is { } sync) await sync.SeedAsync(tracks, playlists);
                }
                catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "Sync.SeedFailed", ex.Message); }
            });
        }
        RefreshSyncStatus();
    }

    partial void OnSyncDeviceNameChanged(string value)
    {
        if (!_settingsLoaded) return;
        _settings.SyncDeviceName = value ?? string.Empty;
        QueueSettingsSave();
    }

    private void RefreshSyncStatus()
    {
        SyncDeviceIdText = Sync?.DeviceId ?? string.Empty;
        SyncDevices.Clear();
        if (Sync is not { } sync) { SyncStatusText = string.Empty; OnPropertyChanged(nameof(HasSyncDevices)); return; }
        try
        {
            var devices = sync.Devices().Where(d => !string.Equals(d.Id, sync.DeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var d in devices) SyncDevices.Add(new SyncDeviceRow(d));
            if (!SyncEnabled) SyncStatusText = "Off. Turn on to share favourites, ratings, play counts and playlists with your other devices.";
            else if (!NoctisServerEnabled) SyncStatusText = "Waiting for Noctis Server — devices sync through it. Turn it on below.";
            else if (devices.Count == 0) SyncStatusText = "On. No device has synced yet — sign in from the Noctis app on your phone.";
            else SyncStatusText = $"On · {devices.Count} device{(devices.Count == 1 ? "" : "s")} · {sync.CurrentSeq} changes in the ledger";
        }
        catch (Exception ex)
        {
            SyncStatusText = $"Sync ledger unavailable — {ex.Message}";
        }
        OnPropertyChanged(nameof(HasSyncDevices));
    }

    [RelayCommand]
    private void TogglePairing() => IsPairingVisible = !IsPairingVisible;

    [RelayCommand]
    private void TurnOnNoctisServer()
    {
        NoctisServerEnabled = true;
        IsPairingVisible = true;
    }

    [RelayCommand]
    private void OpenAccountSyncTab() => SelectSettingsTab(TabAccountSync);

    [RelayCommand]
    private void ToggleChangePassword()
    {
        IsChangePasswordVisible = !IsChangePasswordVisible;
        ChangePasswordValue = string.Empty;
    }

    [RelayCommand]
    private void ChangePrimaryPassword()
    {
        if (PrimaryAccount is not { } user) return;
        ServerUserError = string.Empty;
        try
        {
            ServerUsers.ChangePassword(user.Name, ChangePasswordValue);
            ChangePasswordValue = string.Empty;
            IsChangePasswordVisible = false;
        }
        catch (Exception ex) { ServerUserError = ex.Message; }
    }

    /// <summary>Create-your-account button on the Account &amp; Sync tab (first account is the admin).</summary>
    [RelayCommand]
    private void CreatePrimaryAccount()
    {
        AddServerUserCommand.Execute(null);
        RefreshSyncStatus();
    }

    public sealed record SyncDeviceRow(SyncDevice Device)
    {
        public string Name => string.IsNullOrWhiteSpace(Device.Name) ? Device.Id : Device.Name;
        public string LastSeenText
        {
            get
            {
                var ago = DateTime.UtcNow - Device.LastSeenUtc;
                if (ago < TimeSpan.FromMinutes(1)) return "Synced just now";
                if (ago < TimeSpan.FromHours(1)) return $"Synced {(int)ago.TotalMinutes} min ago";
                if (ago < TimeSpan.FromDays(1)) return $"Synced {(int)ago.TotalHours} h ago";
                return $"Synced {Device.LastSeenUtc.ToLocalTime():d MMM, HH:mm}";
            }
        }
    }

    // ── YouTube downloads (Library tab) ──

    [ObservableProperty] private string _youTubeDownloadFolder = string.Empty;
    [ObservableProperty] private string _ytDlpPath = string.Empty;
    [ObservableProperty] private string _ytDlpStatus = string.Empty;
    [ObservableProperty] private bool _isInstallingYtDlp;
    [ObservableProperty] private double _ytDlpInstallProgress;

    public string YouTubeDownloadFolderHint =>
        App.Services?.GetService<IYouTubeImportService>()?.ResolveDownloadFolder() is { Length: > 0 } f ? f : "Add a music folder first";

    partial void OnYouTubeDownloadFolderChanged(string value)
    {
        if (!_settingsLoaded) return;
        _settings.YouTubeDownloadFolder = value ?? string.Empty;
        QueueSettingsSave();
        OnPropertyChanged(nameof(YouTubeDownloadFolderHint));
    }

    partial void OnYtDlpPathChanged(string value)
    {
        if (!_settingsLoaded) return;
        _settings.YtDlpPath = value ?? string.Empty;
        QueueSettingsSave();
        _ = RefreshYtDlpStatusAsync();
    }

    private async Task RefreshYtDlpStatusAsync()
    {
        var svc = App.Services?.GetService<IYouTubeImportService>();
        if (svc is null) { YtDlpStatus = string.Empty; return; }
        var path = svc.Tool.Resolve();
        if (path is null) { YtDlpStatus = "Not installed — Noctis can download it for you (about 15 MB)."; return; }
        var version = await svc.Tool.GetVersionAsync(CancellationToken.None);
        YtDlpStatus = version is null ? $"Found at {path} but it could not run." : $"yt-dlp {version} · {path}";
    }

    [RelayCommand]
    private async Task InstallYtDlp()
    {
        var svc = App.Services?.GetService<IYouTubeImportService>();
        if (svc is null || IsInstallingYtDlp) return;
        IsInstallingYtDlp = true;
        YtDlpInstallProgress = 0;
        YtDlpStatus = "Downloading yt-dlp…";
        try
        {
            await svc.Tool.InstallAsync(new Progress<double>(p => Dispatcher.UIThread.Post(() => YtDlpInstallProgress = p)), CancellationToken.None);
            await RefreshYtDlpStatusAsync();
        }
        catch (Exception ex) { YtDlpStatus = $"Install failed — {ex.Message}"; }
        finally { IsInstallingYtDlp = false; }
    }

    [RelayCommand]
    private async Task BrowseYouTubeFolder()
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var top = owner is null ? null : Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (top is null) return;
        var picks = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Folder for YouTube downloads",
            AllowMultiple = false,
        });
        if (picks.Count > 0) YouTubeDownloadFolder = picks[0].Path.LocalPath;
    }

    [RelayCommand]
    private Task OpenYouTubeDownloader() => MetadataHelper.OpenYouTubeDownloadDialog();

    // ── Lyrics Studio ──

    public IReadOnlyList<WhisperModelInfo> LyricsModelOptions => WhisperModelManager.Catalog;
    public IReadOnlyList<SpeechLanguageOption> LyricsLanguageOptions => LyricsStudioViewModel.Languages;

    [ObservableProperty] private WhisperModelInfo _lyricsStudioModel = WhisperModelManager.Info(WhisperModelSize.Base);
    [ObservableProperty] private SpeechLanguageOption _lyricsStudioLanguage = LyricsStudioViewModel.Languages[0];
    [ObservableProperty] private bool _lyricsStudioWordTimings = true;
    [ObservableProperty] private bool _lyricsStudioEmbedTags;
    [ObservableProperty] private string _lyricsModelStatus = string.Empty;
    [ObservableProperty] private bool _isLyricsModelInstalled;
    [ObservableProperty] private bool _isDownloadingLyricsModel;
    [ObservableProperty] private double _lyricsModelProgress;
    [ObservableProperty] private string _lyricsStudioStats = string.Empty;
    [ObservableProperty] private string _lyricsStudioFfmpegStatus = string.Empty;

    private ILyricsStudioEngine? LyricsEngine => App.Services?.GetService<ILyricsStudioEngine>();

    partial void OnLyricsStudioModelChanged(WhisperModelInfo value)
    {
        RefreshLyricsModelStatus();
        if (!_settingsLoaded) return;
        _settings.LyricsStudioModel = value.Size.ToString();
        QueueSettingsSave();
    }

    partial void OnLyricsStudioLanguageChanged(SpeechLanguageOption value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioLanguage = value.Code;
        QueueSettingsSave();
    }

    partial void OnLyricsStudioWordTimingsChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioWordTimings = value;
        QueueSettingsSave();
    }

    partial void OnLyricsStudioEmbedTagsChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioEmbedTags = value;
        QueueSettingsSave();
    }

    /// <summary>The dialog changed model/language/format: mirror it here and persist.</summary>
    public void ApplyLyricsStudioSettings(LyricsStudioPrefs prefs)
    {
        LyricsStudioModel = WhisperModelManager.Info(WhisperModelManager.Parse(prefs.Model));
        LyricsStudioLanguage = LyricsStudioViewModel.Languages.FirstOrDefault(l => l.Code == prefs.Language) ?? LyricsStudioViewModel.Languages[0];
        LyricsStudioWordTimings = prefs.WordTimings;
    }

    private void RefreshLyricsModelStatus()
    {
        var engine = LyricsEngine;
        if (engine is null) { LyricsModelStatus = string.Empty; IsLyricsModelInstalled = false; return; }
        IsLyricsModelInstalled = engine.Models.IsInstalled(LyricsStudioModel.Size);
        LyricsModelStatus = IsLyricsModelInstalled
            ? $"Installed · {LyricsStudioModel.Description}"
            : $"Not installed ({LyricsStudioModel.SizeText}) · {LyricsStudioModel.Description}";
        LyricsStudioFfmpegStatus = engine.HasFfmpeg ? string.Empty : "ffmpeg is needed to decode songs — set its path under Audio → Audio tools.";
    }

    private void RefreshLyricsStudioStats()
    {
        var local = _library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        if (local.Count == 0) { LyricsStudioStats = string.Empty; return; }
        var synced = 0; var plain = 0; var none = 0;
        foreach (var t in local)
        {
            if (!string.IsNullOrWhiteSpace(t.SyncedLyrics)) synced++;
            else if (!string.IsNullOrWhiteSpace(t.Lyrics)) plain++;
            else none++;
        }
        LyricsStudioStats = $"{synced} songs with synced lyrics · {plain} with plain lyrics only · {none} without lyrics";
    }

    [RelayCommand]
    private async Task DownloadLyricsModel()
    {
        var engine = LyricsEngine;
        if (engine is null || IsDownloadingLyricsModel) return;
        IsDownloadingLyricsModel = true;
        LyricsModelProgress = 0;
        var model = LyricsStudioModel;
        LyricsModelStatus = $"Downloading the {model.DisplayName} model ({model.SizeText})…";
        try
        {
            await engine.Models.DownloadAsync(model.Size, new Progress<double>(p => Dispatcher.UIThread.Post(() => LyricsModelProgress = p)), CancellationToken.None);
        }
        catch (Exception ex)
        {
            LyricsModelStatus = $"Download failed — {ex.Message}";
            IsDownloadingLyricsModel = false;
            return;
        }
        IsDownloadingLyricsModel = false;
        RefreshLyricsModelStatus();
    }

    [RelayCommand]
    private void DeleteLyricsModel()
    {
        LyricsEngine?.Models.Delete(LyricsStudioModel.Size);
        RefreshLyricsModelStatus();
    }

    /// <summary>Opens Lyrics Studio with the songs that have no synced lyrics yet (first 40, so a run stays reviewable).</summary>
    [RelayCommand]
    private Task OpenLyricsStudioForMissing()
    {
        var tracks = _library.Tracks
            .Where(t => t.SourceType == SourceType.Local && string.IsNullOrWhiteSpace(t.SyncedLyrics))
            .Take(40)
            .ToList();
        return tracks.Count == 0 ? Task.CompletedTask : MetadataHelper.OpenLyricsStudio(tracks);
    }

    // ── Load / save hooks (called from the main file) ──

    private void LoadFeatureSettings()
    {
        UpmixMode = GaplessSink.ParseUpmixMode(_settings.UpmixMode).ToString();

        if (string.IsNullOrWhiteSpace(_settings.SyncDeviceId))
            _settings.SyncDeviceId = Guid.NewGuid().ToString("N")[..12];
        SyncEnabled = _settings.SyncEnabled;
        SyncDeviceName = string.IsNullOrWhiteSpace(_settings.SyncDeviceName) ? Environment.MachineName : _settings.SyncDeviceName;

        YouTubeDownloadFolder = _settings.YouTubeDownloadFolder ?? string.Empty;
        YtDlpPath = _settings.YtDlpPath ?? string.Empty;

        LyricsStudioModel = WhisperModelManager.Info(WhisperModelManager.Parse(_settings.LyricsStudioModel));
        LyricsStudioLanguage = LyricsStudioViewModel.Languages.FirstOrDefault(l => l.Code.Equals(_settings.LyricsStudioLanguage, StringComparison.OrdinalIgnoreCase))
                               ?? LyricsStudioViewModel.Languages[0];
        LyricsStudioWordTimings = _settings.LyricsStudioWordTimings;
        LyricsStudioEmbedTags = _settings.LyricsStudioEmbedTags;
    }

    private void SaveFeatureSettings()
    {
        _settings.UpmixMode = UpmixMode ?? "Off";
        _settings.SyncEnabled = SyncEnabled;
        _settings.SyncDeviceName = SyncDeviceName ?? string.Empty;
        _settings.YouTubeDownloadFolder = YouTubeDownloadFolder ?? string.Empty;
        _settings.YtDlpPath = YtDlpPath ?? string.Empty;
        _settings.LyricsStudioModel = LyricsStudioModel.Size.ToString();
        _settings.LyricsStudioLanguage = LyricsStudioLanguage.Code;
        _settings.LyricsStudioWordTimings = LyricsStudioWordTimings;
        _settings.LyricsStudioEmbedTags = LyricsStudioEmbedTags;
    }

    /// <summary>Tab opened: refresh what the tab shows.</summary>
    private void OnFeatureTabOpened(string tab)
    {
        if (tab == TabAccountSync)
        {
            RefreshServerUsers();
            RefreshSyncStatus();
        }
        else if (tab == TabLyricsStudio)
        {
            RefreshLyricsModelStatus();
            RefreshLyricsStudioStats();
        }
        else if (tab == TabLibrary)
        {
            _ = RefreshYtDlpStatusAsync();
            OnPropertyChanged(nameof(YouTubeDownloadFolderHint));
        }
    }
}
