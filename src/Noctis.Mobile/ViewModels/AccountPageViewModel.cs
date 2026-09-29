using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services.Account;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings → Account: signs the phone in to the owner's Noctis desktop and, once signed in,
/// shows the link with Sync now, Download everything, the downloads' size and Sign out.
/// Sign-in is two steps: the address is probed for the server certificate's fingerprint,
/// which the user compares with the one on the computer, and only a confirmed fingerprint
/// is signed in with (and pinned by the service). Every call goes through
/// <see cref="INoctisAccountService"/>; its events arrive on any thread and are marshalled.
/// </summary>
public sealed partial class AccountPageViewModel : MobilePage
{
    /// <summary>The desktop server's port when the address names none.</summary>
    internal const int DefaultPort = 4747;

    private readonly INoctisAccountService _account;
    private readonly Action<Action> _marshal;
    private string? _probedUrl;
    private bool _syncRunning;
    private bool _downloadRunning;
    private NoctisDownloadProgress? _lastDownload;

    public AccountPageViewModel(ShellViewModel shell, INoctisAccountService account)
    {
        Shell = shell;
        _account = account;
        _marshal = shell.Marshal;
        _account.StateChanged += OnStateChanged;
        _account.SyncProgress += OnSyncProgress;
        _account.DownloadProgress += OnDownloadProgress;
        Refresh();
    }

    public ShellViewModel Shell { get; }
    public override string Title => "Account";

    // ── Signed out: the form ──

    [ObservableProperty] private string _serverAddress = string.Empty;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    /// <summary>The fingerprint the address presented, waiting for the user to compare it with
    /// the computer's; null when no check is pending.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingFingerprint), nameof(IsFormVisible), nameof(FingerprintDisplay))]
    private string? _pendingFingerprint;

    public bool IsConfirmingFingerprint => PendingFingerprint != null;

    /// <summary>The whole fingerprint, eight bytes to a line so it can be read off in pieces.</summary>
    public string FingerprintDisplay => FormatFingerprint(PendingFingerprint);

    public bool IsFormVisible => !IsSignedIn && !IsConfirmingFingerprint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    /// <summary>What the busy step is doing ("Connecting…").</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    public bool HasError => ErrorText.Length > 0;

    // ── Signed in ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut), nameof(IsFormVisible), nameof(CanUseServer))]
    private bool _isSignedIn;

    public bool IsSignedOut => !IsSignedIn;

    /// <summary>The pinned certificate no longer matches: the only way on is Sign out and a new,
    /// confirmed sign-in — never a silent re-trust — so Sync and Download are hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseServer))]
    private bool _isCertificateChanged;

    public bool CanUseServer => IsSignedIn && !IsCertificateChanged;

    /// <summary>"owner on Studio PC".</summary>
    [ObservableProperty] private string _accountText = string.Empty;
    [ObservableProperty] private string _lastSyncText = string.Empty;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncText = string.Empty;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _downloadText = string.Empty;

    /// <summary>"Downloaded: 12 songs · 340 MB".</summary>
    [ObservableProperty] private string _storageText = string.Empty;

    [ObservableProperty] private bool _isConfirmingSignOut;
    [ObservableProperty] private bool _alsoRemoveDownloads;
    [ObservableProperty] private bool _isConfirmingRemoveDownloads;

    // ── Sign in ──

    /// <summary>Step one: probe the address for its certificate fingerprint.</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        ErrorText = string.Empty;
        var url = NormalizeServerAddress(ServerAddress);
        if (url == null)
        {
            ErrorText = Describe(NoctisErrorKind.InvalidAddress, signedIn: false);
            return;
        }
        if (UserName.Trim().Length == 0 || Password.Length == 0)
        {
            ErrorText = "Enter your account name and password.";
            return;
        }
        ServerAddress = url;
        await RunAsync("Connecting to your computer…", async () =>
        {
            var fingerprint = await _account.ProbeFingerprintAsync(url);
            _probedUrl = url;
            PendingFingerprint = fingerprint;
        });
    }

    /// <summary>Step two: the user says the fingerprints match. Sign in with that fingerprint,
    /// then sync straight away so the desktop's library appears.</summary>
    [RelayCommand]
    private async Task TrustAndSignInAsync()
    {
        if (PendingFingerprint is not { } fingerprint || _probedUrl is not { } url) return;
        var signedIn = await RunAsync("Signing in…",
            () => _account.SignInAsync(url, UserName.Trim(), Password, fingerprint));
        PendingFingerprint = null;
        _probedUrl = null;
        if (!signedIn) return;
        // The service keeps only the device key; the password leaves memory with the field.
        Password = string.Empty;
        Refresh();
        await SyncCoreAsync();
    }

    [RelayCommand]
    private void CancelFingerprint()
    {
        PendingFingerprint = null;
        _probedUrl = null;
    }

    // ── Signed in ──

    [RelayCommand]
    private Task SyncNowAsync() => SyncCoreAsync();

    private async Task SyncCoreAsync()
    {
        if (!_account.IsSignedIn || _syncRunning || _account.IsSyncing) return;
        ErrorText = string.Empty;
        _syncRunning = true;
        IsSyncing = true;
        SyncText = "Syncing…";
        try
        {
            var result = await _account.SyncNowAsync();
            SyncText = $"Synced {Count(result.Songs, "song")} and {Count(result.Playlists, "playlist")}";
        }
        catch (Exception ex)
        {
            SyncText = string.Empty;
            ShowError(ex);
        }
        finally
        {
            _syncRunning = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        if (!_account.IsSignedIn || IsDownloading) return;
        ErrorText = string.Empty;
        _downloadRunning = true;
        IsDownloading = true;
        DownloadText = "Starting downloads…";
        var finished = false;
        try
        {
            await _account.DownloadAllAsync();
            finished = true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _downloadRunning = false;
            // Nothing to fetch raises no progress: the counts would never replace "Starting…".
            if (_lastDownload == null) DownloadText = finished ? "Downloads finished" : string.Empty;
            RefreshDownloads();
        }
    }

    [RelayCommand] private void AskRemoveDownloads() => IsConfirmingRemoveDownloads = true;

    [RelayCommand] private void CancelRemoveDownloads() => IsConfirmingRemoveDownloads = false;

    [RelayCommand]
    private async Task ConfirmRemoveDownloadsAsync()
    {
        IsConfirmingRemoveDownloads = false;
        await RunAsync("Removing downloads…", () => _account.RemoveAllDownloadsAsync());
        DownloadText = string.Empty;
        RefreshDownloads();
    }

    [RelayCommand]
    private void AskSignOut()
    {
        AlsoRemoveDownloads = false;
        IsConfirmingSignOut = true;
    }

    [RelayCommand] private void CancelSignOut() => IsConfirmingSignOut = false;

    [RelayCommand]
    private async Task ConfirmSignOutAsync()
    {
        IsConfirmingSignOut = false;
        var removeDownloads = AlsoRemoveDownloads;
        await RunAsync("Signing out…", () => _account.SignOutAsync(removeDownloads));
        SyncText = string.Empty;
        DownloadText = string.Empty;
        Refresh();
    }

    // ── Plumbing ──

    /// <summary>One busy step: status shown, errors worded, busy cleared. False when it failed.</summary>
    private async Task<bool> RunAsync(string status, Func<Task> work)
    {
        IsBusy = true;
        StatusText = status;
        ErrorText = string.Empty;
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            StatusText = string.Empty;
        }
    }

    private void ShowError(Exception ex)
    {
        if (ex is OperationCanceledException) return;
        var kind = (ex as NoctisServerException)?.Kind;
        DebugLog.Write("Account", $"{kind?.ToString() ?? ex.GetType().Name}: {ex.Message}");
        var signedIn = _account.IsSignedIn;
        if (kind == NoctisErrorKind.CertificateChanged && signedIn) IsCertificateChanged = true;
        ErrorText = kind is { } k ? Describe(k, signedIn) : "Something went wrong. Try again.";
        Refresh();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _marshal(Refresh);

    private void OnSyncProgress(object? sender, NoctisSyncProgress progress) => _marshal(() =>
    {
        // The finished text comes from the result (or the error); a late Done/Failed must not overwrite it.
        if (progress.Stage is NoctisSyncStage.Done or NoctisSyncStage.Failed or NoctisSyncStage.Idle)
        {
            if (!_syncRunning && progress.Stage == NoctisSyncStage.Done) SyncText = "Sync finished";
            IsSyncing = _syncRunning || _account.IsSyncing;
            return;
        }
        SyncText = DescribeStage(progress);
        IsSyncing = true;
    });

    private void OnDownloadProgress(object? sender, NoctisDownloadProgress progress) => _marshal(() =>
    {
        _lastDownload = progress;
        RefreshDownloads();
    });

    private void Refresh()
    {
        var account = _account.Account;
        IsSignedIn = _account.IsSignedIn && account != null;
        AccountText = account == null ? string.Empty : $"{account.UserName} on {ServerLabel(account)}";
        LastSyncText = account?.LastSyncUtc is { } synced ? $"Last synced {FormatWhen(synced)}" : "Not synced yet";
        IsSyncing = _syncRunning || _account.IsSyncing;
        if (!IsSignedIn)
        {
            IsCertificateChanged = false;
            IsConfirmingSignOut = false;
            IsConfirmingRemoveDownloads = false;
        }
        RefreshDownloads();
    }

    private void RefreshDownloads()
    {
        StorageText = $"Downloaded: {Count(_account.DownloadedCount, "song")} · {FormatBytes(_account.DownloadedBytes)}";
        var pending = _lastDownload is { Pending: > 0 };
        IsDownloading = _downloadRunning || pending;
        if (_lastDownload is not { } p) return;
        var failed = p.Failed > 0 ? $" · {p.Failed} failed" : string.Empty;
        DownloadText = pending
            ? $"Downloading {p.Completed} of {p.Completed + p.Pending}{failed}"
            : _downloadRunning ? DownloadText : $"Downloads finished{failed}";
    }

    public override void OnClosed()
    {
        _account.StateChanged -= OnStateChanged;
        _account.SyncProgress -= OnSyncProgress;
        _account.DownloadProgress -= OnDownloadProgress;
        Password = string.Empty;
    }

    // ── Wording ──

    /// <summary>The message for a failed call. <paramref name="signedIn"/> separates a pinned
    /// certificate that changed (sign out to trust a new one) from one that changed mid-sign-in.</summary>
    internal static string Describe(NoctisErrorKind kind, bool signedIn) => kind switch
    {
        NoctisErrorKind.Unreachable =>
            "Can't reach your computer. Check it's on, Noctis is open, Pair a device is on, and both are on the same Wi-Fi.",
        NoctisErrorKind.CertificateChanged => signedIn
            ? "Your computer's certificate changed. Sign out and sign in again to trust the new one."
            : "Your computer's certificate changed while signing in. Try again and check the fingerprint.",
        NoctisErrorKind.BadCredentials => "Wrong account name or password.",
        NoctisErrorKind.LockedOut => "Too many failed sign-ins. Wait a few minutes, then try again.",
        NoctisErrorKind.SyncDisabled => "Turn on Sync on your computer (Settings → Account & Devices).",
        NoctisErrorKind.SignedOut => "This phone was removed on your computer. Sign in again.",
        NoctisErrorKind.InvalidAddress =>
            "Enter your computer's address as shown in Pair a device, like 192.168.1.20 or https://192.168.1.20:4747.",
        _ => "Your computer couldn't finish that. Try again in a moment.",
    };

    internal static string DescribeStage(NoctisSyncProgress progress)
    {
        var counts = progress.Total > 0 ? $" {progress.Done} of {progress.Total}" : string.Empty;
        return progress.Stage switch
        {
            NoctisSyncStage.Catalog => $"Getting your library…{counts}",
            NoctisSyncStage.Covers => $"Getting covers…{counts}",
            NoctisSyncStage.State => "Syncing favourites and ratings…",
            NoctisSyncStage.Playlists => "Syncing playlists…",
            NoctisSyncStage.Plays => "Sending plays…",
            _ => "Syncing…",
        };
    }

    /// <summary>
    /// The typed address as "https://host:port", or null. Accepts a bare host or IP (with an
    /// optional port; <see cref="DefaultPort"/> when none) or an https URL of just that. A
    /// path, query, fragment or user info is refused rather than dropped, as is any scheme but
    /// https (the server speaks nothing else and cleartext is off).
    /// </summary>
    internal static string? NormalizeServerAddress(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            if (!s[..scheme].Equals("https", StringComparison.OrdinalIgnoreCase)) return null;
            s = s[(scheme + 3)..];
        }
        if (s.EndsWith('/')) s = s[..^1];   // "https://host:4747/" is the root, not a path
        if (s.Length == 0 || s.EndsWith(':') || s.IndexOfAny(['/', '?', '#', '@', '\\', ' ']) >= 0) return null;
        var explicitPort = s.StartsWith('[') ? s.Contains("]:", StringComparison.Ordinal) : s.Contains(':');
        if (!Uri.TryCreate("https://" + s, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        var port = explicitPort ? uri.Port : DefaultPort;
        if (port is <= 0 or > 65535) return null;
        return $"https://{uri.Host}:{port}";
    }

    /// <summary>"AB:CD:…" as lines of eight bytes; anything else as it came.</summary>
    internal static string FormatFingerprint(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return string.Empty;
        var bytes = fingerprint.Split(':');
        return bytes.Length < 2 ? fingerprint : string.Join("\n", bytes.Chunk(8).Select(line => string.Join(":", line)));
    }

    /// <summary>The desktop's own name when it gave one, else the address's host.</summary>
    internal static string ServerLabel(NoctisAccount account) =>
        !string.IsNullOrWhiteSpace(account.ServerName) ? account.ServerName
        : Uri.TryCreate(account.ServerUrl, UriKind.Absolute, out var uri) ? uri.Host
        : account.ServerUrl;

    internal static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    private static string FormatWhen(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? $"today at {local:t}" : $"{local:g}";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
