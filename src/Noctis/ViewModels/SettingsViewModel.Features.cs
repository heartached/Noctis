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

namespace Noctis.ViewModels;

/// <summary>
/// Settings for the 2026-09 feature batch: Account &amp; Sync tab, multi-channel upmix
/// and Lyrics Studio. Kept in a partial so the main file only carries
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

    // ── Headphone surround (Audio tab; VLC's binauralizer ships with the Windows build) ──

    public bool IsHeadphoneSurroundSupported => OperatingSystem.IsWindows();

    [ObservableProperty] private bool _headphoneSurround;

    /// <summary>The saved choice differs from what the running player started with: it is a
    /// LibVLC instance option, so it applies at the next launch.</summary>
    public bool HeadphoneSurroundNeedsRestart =>
        _audioPlayer != null && HeadphoneSurround != _audioPlayer.HeadphoneSurroundActive;

    partial void OnHeadphoneSurroundChanged(bool value)
    {
        OnPropertyChanged(nameof(HeadphoneSurroundNeedsRestart));
        OnPropertyChanged(nameof(MultichannelOutputNeedsRestart));
        if (!_settingsLoaded) return;
        _ = SaveAsync();
    }

    // ── Multichannel output (Audio tab, Windows; GitHub #200) ──

    [ObservableProperty] private bool _multichannelOutput;

    /// <summary>The saved choice differs from what the running player started with: the engine's
    /// channel count is fixed when its sink opens, so it applies at the next launch. Headphone
    /// surround overrides it there, so the effective value is compared.</summary>
    public bool MultichannelOutputNeedsRestart =>
        _audioPlayer != null && (MultichannelOutput && !HeadphoneSurround) != _audioPlayer.MultichannelOutputActive;

    partial void OnMultichannelOutputChanged(bool value)
    {
        OnPropertyChanged(nameof(MultichannelOutputNeedsRestart));
        if (!_settingsLoaded) return;
        _ = SaveAsync();
    }

    // ── Account & Sync ──

    private ILibrarySyncService? _sync;
    private ILibrarySyncService? Sync => _sync ??= ResolveSync();

    /// <summary>Tests hand in a ledger; the app resolves it from <see cref="App.Services"/>.</summary>
    internal ILibrarySyncService? SyncServiceOverride { get => _sync; set => _sync = value; }

    private ILibrarySyncService? ResolveSync()
    {
        var sync = App.Services?.GetService<ILibrarySyncService>();
        if (sync is not null)
            sync.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPhoneDevicesInBackground);
        return sync;
    }

    /// <summary>False while the account features are parked (<see cref="Services.AccountFeatures"/>):
    /// the Account &amp; Sync cards show disabled under a Coming soon badge.</summary>
    public bool AccountFeaturesEnabled => AccountFeatures.Enabled;

    [ObservableProperty] private bool _syncEnabled;
    [ObservableProperty] private string _syncDeviceName = string.Empty;
    /// <summary>The line under the sync switch: "Last synced 4 h ago", "Not synced yet", empty while off.</summary>
    [ObservableProperty] private string _syncStatusText = string.Empty;
    [ObservableProperty] private string _syncDeviceIdText = string.Empty;

    /// <summary>The account this computer's owner uses on their devices: the first admin, else the first account.</summary>
    public ServerUser? PrimaryAccount => ServerUsersList.FirstOrDefault(u => u.IsAdmin) ?? ServerUsersList.FirstOrDefault();
    public bool HasPrimaryAccount => PrimaryAccount is not null;
    /// <summary>The account's name, empty without one (PrimaryAccount.Name logged a binding warning then).</summary>
    public string PrimaryAccountName => PrimaryAccount?.Name ?? string.Empty;
    public string PrimaryAccountInitial => PrimaryAccount is { Name.Length: > 0 } u ? u.Name[..1].ToUpperInvariant() : "?";
    /// <summary>"Since November 2026", in the app language (it was English with the OS's month names).</summary>
    public string PrimaryAccountSince => PrimaryAccount is { } u
        ? Localization.Loc.T("AccountSync.Since", u.CreatedUtc.ToLocalTime().ToString("MMMM yyyy", Localization.Loc.Instance.Culture))
        : string.Empty;
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
        OnPropertyChanged(nameof(PrimaryAccountName));
        OnPropertyChanged(nameof(PrimaryAccountInitial));
        OnPropertyChanged(nameof(PrimaryAccountSince));
        OnPropertyChanged(nameof(OtherAccounts));
        OnPropertyChanged(nameof(HasOtherAccounts));
        // The first account made with the pairing panel open: its QR can show now; the main
        // account removed: the code for it goes.
        if (IsPairingVisible && HasPairingQr != HasPrimaryAccount) RefreshPairingQr();
    }

    partial void OnSyncEnabledChanged(bool value)
    {
        // Parked: nothing turns sync on, and the stored flag is left as it is.
        if (!AccountFeatures.Enabled) { if (value) SyncEnabled = false; return; }
        if (!_settingsLoaded) return;
        _settings.SyncEnabled = value;
        _syncTurnedOffByUser = !value;
        _ = SaveAsync();
        if (value)
        {
            // First enable: put the existing favourites/ratings/play counts into the ledger so
            // a phone that connects today sees the library as it is, not only future changes.
            // One seed at a time: on, off, on used to run two over each other, and a seed of a
            // big library holds the ledger for seconds (one write per song).
            var tracks = _library.Tracks.ToList();
            var sync = Sync;
            _syncSeed = _syncSeed.ContinueWith(_ => SeedSyncLedgerAsync(sync, tracks), TaskScheduler.Default).Unwrap();
        }
        // Off the UI thread: the ledger read waited out the seed's writes on the UI thread,
        // 0.2-1.3 s with a 20,000-song library, which froze the switch mid-slide.
        RefreshPhoneDevicesInBackground();
    }

    private Task _syncSeed = Task.CompletedTask;

    private async Task SeedSyncLedgerAsync(ILibrarySyncService? sync, List<Track> tracks)
    {
        // Switched off again before this seed's turn: nothing to do (the next "on" seeds).
        if (sync is null || !SyncEnabled) return;
        try
        {
            var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
            await sync.SeedAsync(tracks, playlists).ConfigureAwait(false);
        }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "Sync.SeedFailed", ex.Message); }
    }

    partial void OnSyncDeviceNameChanged(string value)
    {
        if (!_settingsLoaded) return;
        _settings.SyncDeviceName = value ?? string.Empty;
        QueueSettingsSave();
    }

    // ── Phone app: one list of phones, and when they last synced ──

    public ObservableCollection<PhoneDeviceRow> PhoneDevices { get; } = new();
    public bool HasPhoneDevices => PhoneDevices.Count > 0;

    /// <summary>The phone card's body (pairing, devices, sync): the server is on and listening.</summary>
    public bool IsPhoneCardOpen => NoctisServerEnabled && NoctisServerUrl.Length > 0;

    /// <summary>
    /// Phones signed in with a device key, each with when it last synced, then phones the sync
    /// ledger still knows but whose key is gone (signed out, removed, or a password change). The
    /// two used to be separate lists in separate cards and could disagree (a phone "Synced 4 h
    /// ago" under "No device has signed in yet"). Also sets the "Last synced" line.
    /// </summary>
    private void RefreshPhoneDevices()
    {
        _phoneDevicesGeneration++;
        ApplyPhoneDevices(ReadPhoneDevices(ServerUsers, Sync));
    }

    /// <summary>Newest refresh wins: a background read that lands after a newer one is dropped.</summary>
    private int _phoneDevicesGeneration;

    /// <summary>
    /// <see cref="RefreshPhoneDevices"/> with the two database reads on a worker thread, for the
    /// switches and the ledger's own change events. The ledger is SQLite and a seed or a phone's
    /// push holds it while it writes; read on the UI thread, that wait froze the window.
    /// </summary>
    private async void RefreshPhoneDevicesInBackground()
    {
        var generation = ++_phoneDevicesGeneration;
        // Sync off needs no read to know the line is empty: clear it with the switch.
        if (!SyncEnabled) SyncStatusText = string.Empty;
        var users = ServerUsers;
        var sync = Sync;
        PhoneDevicesSnapshot snapshot;
        try { snapshot = await Task.Run(() => ReadPhoneDevices(users, sync)); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "Phone.DevicesRead", ex.Message); return; }
        if (generation != _phoneDevicesGeneration) return;
        ApplyPhoneDevices(snapshot);
    }

    private sealed record PhoneDevicesSnapshot(IReadOnlyList<ServerDevice> Keys, string? KeysError,
        IReadOnlyList<SyncDevice> Synced, string? LedgerError, string? DeviceId);

    private static PhoneDevicesSnapshot ReadPhoneDevices(ServerUserStore users, ILibrarySyncService? sync)
    {
        IReadOnlyList<ServerDevice> keys = Array.Empty<ServerDevice>();
        string? keysError = null;
        try { keys = users.Devices(); }
        catch (Exception ex) { keysError = ex.Message; }

        IReadOnlyList<SyncDevice> synced = Array.Empty<SyncDevice>();
        string? ledgerError = null;
        string? deviceId = null;
        if (sync is not null)
        {
            deviceId = sync.DeviceId;
            try { synced = sync.Devices().Where(d => !string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)).ToList(); }
            catch (Exception ex) { ledgerError = ex.Message; }
        }
        return new PhoneDevicesSnapshot(keys, keysError, synced, ledgerError, deviceId);
    }

    private void ApplyPhoneDevices(PhoneDevicesSnapshot snapshot)
    {
        if (snapshot.KeysError is { } keysError) ShowServerUserError(keysError);
        if (snapshot.DeviceId is { } deviceId) SyncDeviceIdText = deviceId;
        var synced = snapshot.Synced;
        var ledgerError = snapshot.LedgerError;

        var now = DateTime.UtcNow;
        var rows = BuildPhoneDeviceRows(snapshot.Keys, synced, SyncEnabled, now);
        // Same rows (the usual case for a switch flip): leave the list alone rather than
        // rebuilding every row's template under the switch's slide.
        if (!rows.SequenceEqual(PhoneDevices))
        {
            PhoneDevices.Clear();
            foreach (var row in rows) PhoneDevices.Add(row);
            OnPropertyChanged(nameof(HasPhoneDevices));
        }
        SyncStatusText = ledgerError is not null ? Localization.Loc.T("Phone.SyncFailed", ledgerError)
            : !SyncEnabled ? string.Empty
            : synced.Count == 0 ? Localization.Loc.T("Phone.NotSyncedYet")
            : Localization.Loc.T("Phone.LastSynced", Ago(synced.Max(d => d.LastSeenUtc), now));
    }

    internal static IReadOnlyList<PhoneDeviceRow> BuildPhoneDeviceRows(
        IReadOnlyList<ServerDevice> keys, IReadOnlyList<SyncDevice> synced, bool syncOn, DateTime nowUtc)
    {
        // A phone's ledger id is the device id it signed in with (NoctisServer: keyDevice.DeviceId).
        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var rows = new List<PhoneDeviceRow>();
        foreach (var key in keys)
        {
            var ledger = synced.FirstOrDefault(s => Same(s.Id, key.DeviceId));
            var status = syncOn && ledger is not null
                ? Localization.Loc.T("Phone.Synced", Ago(ledger.LastSeenUtc, nowUtc))
                : Localization.Loc.T("Phone.Connected", Ago(key.LastUsedUtc ?? key.CreatedUtc, nowUtc));
            rows.Add(new PhoneDeviceRow(key.DeviceName, status, key));
        }
        foreach (var s in synced.Where(s => !keys.Any(k => Same(k.DeviceId, s.Id))))
            rows.Add(new PhoneDeviceRow(string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name, Localization.Loc.T("Phone.SignedOut"), null, s.Id));
        return rows;
    }

    /// <summary>"just now", "5 min ago", "3 h ago", or "on 10/9/2026 2:05 PM" in the app language's own date order.</summary>
    internal static string Ago(DateTime utc, DateTime nowUtc)
    {
        var ago = nowUtc - utc;
        if (ago < TimeSpan.FromMinutes(1)) return Localization.Loc.T("Time.JustNow");
        if (ago < TimeSpan.FromHours(1)) return Localization.Loc.T("Time.MinutesAgo", (int)ago.TotalMinutes);
        if (ago < TimeSpan.FromDays(1)) return Localization.Loc.T("Time.HoursAgo", (int)ago.TotalHours);
        // "g": a fixed "d MMM" read "9 10月" in Japanese and Chinese.
        return Localization.Loc.T("Time.OnDate", utc.ToLocalTime().ToString("g", Localization.Loc.Instance.Culture));
    }

    /// <summary>"5 min ago" goes stale while the page stays open: re-read once a minute while
    /// the Account &amp; Devices tab is showing (OnFeatureTabOpened starts and stops it).</summary>
    private DispatcherTimer? _phoneDevicesTicker;

    private void SetPhoneDevicesTicker(bool on)
    {
        if (!on) { _phoneDevicesTicker?.Stop(); return; }
        _phoneDevicesTicker ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => { if (IsAccountDevicesTabVisible) RefreshPhoneDevices(); else _phoneDevicesTicker?.Stop(); });
        _phoneDevicesTicker.Start();
    }

    /// <summary>"Connect a phone": shows the address, the QR and the security code to compare on
    /// the phone. It no longer turns sync on behind the user's back: that is the switch under
    /// the devices (and it comes on with the server the first time, see TurnOnSyncByDefault).</summary>
    [RelayCommand(CanExecute = nameof(AccountFeaturesEnabled))]
    private void TogglePairing()
    {
        IsPairingVisible = !IsPairingVisible;
        if (!IsPairingVisible) return;
        if (!NoctisServerEnabled) NoctisServerEnabled = true;
        RefreshPhoneDevices();
    }

    /// <summary>The user switched sync off (kept here, not only on _settings: SaveAsync re-bases
    /// _settings on the file and writes back only what this view model owns).</summary>
    private bool _syncTurnedOffByUser;

    /// <summary>Phones let in: sync comes on with them unless the user switched it off before.</summary>
    private void TurnOnSyncByDefault()
    {
        if (AccountFeatures.Enabled && !SyncEnabled && !_syncTurnedOffByUser) SyncEnabled = true;
    }

    /// <summary>Signs the device out: its key stops working on its next request. A phone already
    /// signed out leaves the list instead (the ledger never dropped one, so they piled up); what
    /// it synced stays.</summary>
    [RelayCommand]
    private void RemoveSignedInDevice(PhoneDeviceRow row)
    {
        try
        {
            if (row.Key is { } key) ServerUsers.RevokeDevice(key.User, key.DeviceId);
            else if (row.LedgerId is { } id) Sync?.ForgetDevice(id);
            else return;
        }
        catch (Exception ex) { ShowServerUserError(ex.Message); }
        RefreshPhoneDevices();
    }

    /// <summary>One phone in the list. No <see cref="Key"/> = the ledger knows it (as <see cref="LedgerId"/>) but it is signed out.</summary>
    public sealed record PhoneDeviceRow(string Name, string StatusText, ServerDevice? Key, string? LedgerId = null)
    {
        public bool IsSignedIn => Key is not null;
    }

    [RelayCommand]
    private void ToggleChangePassword()
    {
        IsChangePasswordVisible = !IsChangePasswordVisible;
        ChangePasswordValue = string.Empty;
        IsPasswordRevealed = false;
    }

    /// <summary>The eye in the password boxes (create account, change password): show what was typed.</summary>
    [ObservableProperty] private bool _isPasswordRevealed;

    [RelayCommand]
    private void TogglePasswordReveal() => IsPasswordRevealed = !IsPasswordRevealed;

    [RelayCommand]
    private void ChangePrimaryPassword()
    {
        if (PrimaryAccount is not { } user) return;
        ServerUserError = string.Empty;
        try
        {
            // Also signs out every device and drops the API key (ServerUserStore.ChangePassword).
            ServerUsers.ChangePassword(user.Name, ChangePasswordValue);
            ChangePasswordValue = string.Empty;
            IsChangePasswordVisible = false;
            RefreshServerUsers();
        }
        catch (Exception ex) { ServerUserError = ex.Message; }
    }

    /// <summary>Create-your-account button on the Account &amp; Sync tab (first account is the admin).</summary>
    [RelayCommand(CanExecute = nameof(AccountFeaturesEnabled))]
    private void CreatePrimaryAccount()
    {
        AddServerUserCommand.Execute(null);
        RefreshPhoneDevices();
    }

    // YouTube downloads moved to the official "Add from YouTube" plugin (10-29). Their
    // settings.json fields (YouTubeDownloadFolder, YtDlpPath) stay as legacy: nothing here
    // reads or writes them any more, so a saved value survives untouched. YtDlpPath is still
    // honoured by the lyrics-video yt-dlp (Program.cs).

    // ── Lyrics Studio ──

    public IReadOnlyList<SpeechLanguageOption> LyricsLanguageOptions => LyricsStudioViewModel.Languages;

    /// <summary>The one speech model (09-29); kept so the saved preference is rewritten as Medium.</summary>
    [ObservableProperty] private WhisperModelInfo _lyricsStudioModel = WhisperModelManager.Lullaby;
    [ObservableProperty] private SpeechLanguageOption _lyricsStudioLanguage = LyricsStudioViewModel.Languages[0];
    [ObservableProperty] private bool _lyricsStudioWordTimings = true;
    [ObservableProperty] private bool _lyricsStudioEmbedTags;
    [ObservableProperty] private bool _lyricsStudioOnlineLyrics = true;
    [ObservableProperty] private bool _lyricsStudioSaveTtml;
    /// <summary>The Studio's live preview pane is open (set from the Studio itself).</summary>
    [ObservableProperty] private bool _lyricsStudioLivePreview = true;
    /// <summary>Experimental: the speech model on the graphics card (set from the Studio itself).</summary>
    [ObservableProperty] private bool _lyricsStudioUseGpu;
    /// <summary>Skip songs that already carry the format being written.</summary>
    [ObservableProperty] private bool _lyricsStudioSkipAlreadyTimed = true;
    [ObservableProperty] private string _lyricsStudioStats = string.Empty;
    /// <summary>Per-format song counts for the Lyrics Studio card, empty until the count finishes.</summary>
    [ObservableProperty] private IReadOnlyList<LyricsStudioCount> _lyricsStudioCounts = Array.Empty<LyricsStudioCount>();

    partial void OnLyricsStudioModelChanged(WhisperModelInfo value)
    {
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

    partial void OnLyricsStudioSkipAlreadyTimedChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioSkipAlreadyTimed = value;
        QueueSettingsSave();
    }

    partial void OnLyricsStudioEmbedTagsChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioEmbedTags = value;
        QueueSettingsSave();
    }

    partial void OnLyricsStudioOnlineLyricsChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioOnlineLyrics = value;
        QueueSettingsSave();
    }

    partial void OnLyricsStudioSaveTtmlChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioSaveTtml = value;
        QueueSettingsSave();
    }

    partial void OnLyricsStudioLivePreviewChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioLivePreview = value;
        QueueSettingsSave();
    }

    partial void OnLyricsStudioUseGpuChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _settings.LyricsStudioUseGpu = value;
        QueueSettingsSave();
    }

    /// <summary>The dialog changed model/language/format: mirror it here and persist.</summary>
    public void ApplyLyricsStudioSettings(LyricsStudioPrefs prefs)
    {
        LyricsStudioModel = WhisperModelManager.Info(WhisperModelManager.Parse(prefs.Model));
        LyricsStudioLanguage = LyricsStudioViewModel.Languages.FirstOrDefault(l => l.Code == prefs.Language) ?? LyricsStudioViewModel.Languages[0];
        LyricsStudioWordTimings = prefs.WordTimings;
        LyricsStudioSkipAlreadyTimed = prefs.SkipAlreadyTimed;
        LyricsStudioEmbedTags = prefs.EmbedTags;
        LyricsStudioOnlineLyrics = prefs.OnlineLyrics;
        LyricsStudioSaveTtml = prefs.SaveTtml;
        LyricsStudioLivePreview = prefs.LivePreview;
        LyricsStudioUseGpu = prefs.UseGpu;
    }

    private CancellationTokenSource? _lyricsStatsCts;

    /// <summary>
    /// Counts the library's lyric formats off the UI thread. The count touches disk for
    /// every local track (sidecar listing + stored lyrics), which froze the window for
    /// seconds on large libraries when it ran inline on tab open. Re-opening the tab
    /// cancels a pass still running.
    /// </summary>
    private void RefreshLyricsStudioStats()
    {
        _lyricsStatsCts?.Cancel();
        var local = _library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        if (local.Count == 0) { LyricsStudioStats = string.Empty; LyricsStudioCounts = Array.Empty<LyricsStudioCount>(); return; }

        var cts = _lyricsStatsCts = new CancellationTokenSource();
        var ct = cts.Token;
        LyricsStudioStats = $"Counting lyrics across {local.Count} songs…";
        _ = Task.Run(() =>
        {
            IReadOnlyList<LyricsStudioCount> counts;
            try { counts = BuildLyricsStudioCounts(ExistingLyricsLoader.DetectFormats(local, ct)); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.StatsFailed", ex.Message);
                counts = Array.Empty<LyricsStudioCount>();
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                LyricsStudioCounts = counts;
                LyricsStudioStats = string.Empty;
            });
        }, ct);
    }

    /// <summary>The four format buckets the Lyrics Studio card shows as small stat tiles.</summary>
    internal static IReadOnlyList<LyricsStudioCount> BuildLyricsStudioCounts(IReadOnlyList<LyricsFormat> formats)
    {
        int elrc = 0, lrc = 0, plain = 0, none = 0;
        foreach (var f in formats)
        {
            switch (f)
            {
                case LyricsFormat.Elrc: elrc++; break;
                case LyricsFormat.Lrc: lrc++; break;
                case LyricsFormat.Plain: plain++; break;
                default: none++; break;
            }
        }
        return new[]
        {
            new LyricsStudioCount(elrc, Localization.Loc.T("LyricsStudioSettings.CountWord"), "ELRC"),
            new LyricsStudioCount(lrc, Localization.Loc.T("LyricsStudioSettings.CountLine"), "LRC"),
            new LyricsStudioCount(plain, Localization.Loc.T("LyricsStudioSettings.CountPlain"), ""),
            new LyricsStudioCount(none, Localization.Loc.T("LyricsStudioSettings.CountNone"), ""),
        };
    }

    internal static string FormatLyricsStudioStats(IReadOnlyList<LyricsFormat> formats)
    {
        int elrc = 0, lrc = 0, plain = 0, none = 0;
        foreach (var f in formats)
        {
            switch (f)
            {
                case LyricsFormat.Elrc: elrc++; break;
                case LyricsFormat.Lrc: lrc++; break;
                case LyricsFormat.Plain: plain++; break;
                default: none++; break;
            }
        }
        return $"{elrc} with word timings (ELRC) · {lrc} with line timings (LRC) · {plain} plain only · {none} without lyrics";
    }

    /// <summary>Opens Lyrics Studio with the songs that lack the chosen format — ELRC when word timings are on, any timed LRC when off (first 40, so a run stays reviewable).</summary>
    [RelayCommand]
    private Task OpenLyricsStudioForMissing() => MetadataHelper.OpenLyricsStudioForLibrary(_settings.LyricsStudioWordTimings);

    // ── Load / save hooks (called from the main file) ──

    private void LoadFeatureSettings()
    {
        UpmixMode = GaplessSink.ParseUpmixMode(_settings.UpmixMode).ToString();
        HeadphoneSurround = _settings.HeadphoneSurround;
        MultichannelOutput = _settings.MultichannelOutput;
        LoadChannelMixSettings();

        if (string.IsNullOrWhiteSpace(_settings.SyncDeviceId))
            _settings.SyncDeviceId = Guid.NewGuid().ToString("N")[..12];
        SyncEnabled = AccountFeatures.Enabled && _settings.SyncEnabled;
        _syncTurnedOffByUser = _settings.SyncTurnedOffByUser;
        SyncDeviceName = string.IsNullOrWhiteSpace(_settings.SyncDeviceName) ? Environment.MachineName : _settings.SyncDeviceName;

        LyricsStudioModel = WhisperModelManager.Info(WhisperModelManager.Parse(_settings.LyricsStudioModel));
        LyricsStudioLanguage = LyricsStudioViewModel.Languages.FirstOrDefault(l => l.Code.Equals(_settings.LyricsStudioLanguage, StringComparison.OrdinalIgnoreCase))
                               ?? LyricsStudioViewModel.Languages[0];
        LyricsStudioWordTimings = _settings.LyricsStudioWordTimings;
        LyricsStudioSkipAlreadyTimed = _settings.LyricsStudioSkipAlreadyTimed;
        LyricsStudioEmbedTags = _settings.LyricsStudioEmbedTags;
        LyricsStudioOnlineLyrics = _settings.LyricsStudioOnlineLyrics;
        LyricsStudioSaveTtml = _settings.LyricsStudioSaveTtml;
        LyricsStudioLivePreview = _settings.LyricsStudioLivePreview;
        LyricsStudioUseGpu = _settings.LyricsStudioUseGpu;
    }

    private void SaveFeatureSettings()
    {
        _settings.UpmixMode = UpmixMode ?? "Off";
        _settings.HeadphoneSurround = HeadphoneSurround;
        _settings.MultichannelOutput = MultichannelOutput;
        SaveChannelMixSettings();
        if (AccountFeatures.Enabled) _settings.SyncEnabled = SyncEnabled;
        _settings.SyncTurnedOffByUser = _syncTurnedOffByUser;
        _settings.SyncDeviceName = SyncDeviceName ?? string.Empty;
        _settings.LyricsStudioModel = LyricsStudioModel.Size.ToString();
        _settings.LyricsStudioLanguage = LyricsStudioLanguage.Code;
        _settings.LyricsStudioWordTimings = LyricsStudioWordTimings;
        _settings.LyricsStudioSkipAlreadyTimed = LyricsStudioSkipAlreadyTimed;
        _settings.LyricsStudioEmbedTags = LyricsStudioEmbedTags;
        _settings.LyricsStudioOnlineLyrics = LyricsStudioOnlineLyrics;
        _settings.LyricsStudioSaveTtml = LyricsStudioSaveTtml;
        _settings.LyricsStudioLivePreview = LyricsStudioLivePreview;
        _settings.LyricsStudioUseGpu = LyricsStudioUseGpu;
    }

    /// <summary>Tab opened: refresh what the tab shows.</summary>
    private void OnFeatureTabOpened(string tab)
    {
        SetPhoneDevicesTicker(tab == TabAccountDevices);
        if (tab == TabAccountDevices)
        {
            RefreshServerUsers(); // also the phone list
        }
    }
}

/// <summary>One bucket of the Lyrics Studio library count: how many songs, what format, and a short tag.</summary>
public sealed record LyricsStudioCount(int Count, string Label, string Tag);
