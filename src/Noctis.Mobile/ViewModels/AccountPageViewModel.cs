using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Services;
using Noctis.Services.Server;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings → Account: signs the phone in to the owner's Noctis desktop and, once signed in,
/// shows the account (the desktop's profile picture and name, the email, PC, last sync) over Sync
/// now, Download everything, Send phone songs to computer, the downloads' size, Remove all
/// downloads and Sign out.
/// Sign-in is two steps: the address is probed for the server certificate's fingerprint,
/// which the user compares with the one on the computer, and only a confirmed fingerprint
/// is signed in with (and pinned by the service). Or, with no typing at all, Scan QR Code reads
/// the code the computer shows under Connect a phone: its addresses, its certificate pin and a
/// one-time secret, which the service sends only to that pinned certificate. Every call goes through
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
    /// <summary>This page started a sync whose Done/Failed has not arrived yet (see OnSyncProgress).</summary>
    private bool _ownSyncEndPending;
    private bool _downloadRunning;
    private NoctisDownloadProgress? _lastDownload;
    private CancellationTokenSource? _uploadCts;

    public AccountPageViewModel(ShellViewModel shell, INoctisAccountService account)
    {
        Shell = shell;
        _account = account;
        _marshal = shell.Marshal;
        _account.StateChanged += OnStateChanged;
        _account.SyncProgress += OnSyncProgress;
        _account.DownloadProgress += OnDownloadProgress;
        _account.UploadProgress += OnUploadProgress;
        Refresh();
        // Signed out by the sync at app start (which only logs): say why the form is back.
        if (!IsSignedIn && _account.WasSignedOutByComputer) SetError(Describe(NoctisErrorKind.SignedOut, signedIn: false));
    }

    public ShellViewModel Shell { get; }
    public override string Title => "Account";

    // ── Signed out: the form ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    private string _serverAddress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    private string _userName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    private string _password = string.Empty;

    /// <summary>Sign In is offered once all three are filled in and the address reads as one;
    /// a wrong address says why under its field when the field is left (<see cref="CheckAddress"/>).</summary>
    public bool CanSignIn => IsIdle && NormalizeServerAddress(ServerAddress) != null
                             && UserName.Trim().Length > 0 && Password.Length > 0;

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
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanSync), nameof(CanDownload), nameof(CanUpload), nameof(CanSignIn),
        nameof(CanRemoveDownloads), nameof(ShowStatusLine), nameof(IsFormBusy))]
    private bool _isBusy;

    /// <summary>A QR sign-in is running: the Scan button shows the spinner, not Sign In; its
    /// errors are about the code, not the typed fields.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormBusy))]
    private bool _isSigningInByQr;

    /// <summary>The typed sign-in's own busy state (Sign In's spinner).</summary>
    public bool IsFormBusy => IsBusy && !IsSigningInByQr;

    public bool IsIdle => !IsBusy;

    /// <summary>What the busy step is doing ("Connecting to your computer…"): on the Sign In and
    /// Trust buttons while signing in, as a line under the tiles once signed in.</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    public bool ShowStatusLine => IsBusy && IsSignedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(HasAddressError), nameof(HasCredentialsError), nameof(HasGeneralError))]
    private string _errorText = string.Empty;

    /// <summary>The field <see cref="ErrorText"/> is about: shown under it, cleared when it is edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddressError), nameof(HasCredentialsError), nameof(HasGeneralError))]
    private AccountField _errorField;

    public bool HasError => ErrorText.Length > 0;
    public bool HasAddressError => HasError && ErrorField == AccountField.Address;
    public bool HasCredentialsError => HasError && ErrorField == AccountField.Credentials;
    /// <summary>An error about no one field (a sync, a changed certificate): the line under the cards.</summary>
    public bool HasGeneralError => HasError && ErrorField == AccountField.None;

    // ── Signed in ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut), nameof(IsFormVisible), nameof(CanUseServer), nameof(ShowSyncAction), nameof(CanSync), nameof(CanUpload),
        nameof(ShowStatusLine))]
    private bool _isSignedIn;

    public bool IsSignedOut => !IsSignedIn;

    /// <summary>The pinned certificate no longer matches: the only way on is Sign out and a new,
    /// confirmed sign-in — never a silent re-trust — so Sync and Download are hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseServer), nameof(ShowSyncAction), nameof(CanSync), nameof(CanUpload))]
    private bool _isCertificateChanged;

    public bool CanUseServer => IsSignedIn && !IsCertificateChanged;

    /// <summary>The row's accent "Sync now": gone while a sync runs (its stage and bar show instead).</summary>
    public bool ShowSyncAction => CanUseServer && !IsSyncing;

    /// <summary>"owner on Studio PC".</summary>
    [ObservableProperty] private string _accountText = string.Empty;

    /// <summary>The header: the desktop's profile name, large (the account's email when it has none).</summary>
    [ObservableProperty] private string _displayName = string.Empty;

    /// <summary>The header's smaller line under the name: the account's email, when the name shown is not it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmailText))]
    private string _emailText = string.Empty;

    public bool HasEmailText => EmailText.Length > 0;

    /// <summary>The header's second line: "on Studio PC".</summary>
    [ObservableProperty] private string _serverText = string.Empty;

    /// <summary>The profile card's computer row: "Studio PC".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignOutQuestion))]
    private string _computerName = string.Empty;

    /// <summary>The sign-out card's question: "Sign out of Studio PC?".</summary>
    public string SignOutQuestion => $"Sign out of {ComputerName}?";

    /// <summary>The desktop's profile picture (a local file the service cached), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar), nameof(ShowInitials), nameof(ShowPersonGlyph))]
    private string? _avatarPath;

    public bool HasAvatar => !string.IsNullOrEmpty(AvatarPath);

    /// <summary>No picture: the accent circle shows these ("OW" for "owner", "JD" for "John Doe").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInitials), nameof(ShowPersonGlyph))]
    private string _initials = string.Empty;

    public bool ShowInitials => !HasAvatar && Initials.Length > 0;
    public bool ShowPersonGlyph => !HasAvatar && Initials.Length == 0;

    /// <summary>"Synced 5 min ago" (<see cref="SyncedText"/>, as Settings' profile card says it).</summary>
    [ObservableProperty] private string _lastSyncText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSync), nameof(ShowSyncAction))]
    private bool _isSyncing;
    [ObservableProperty] private string _syncText = string.Empty;

    /// <summary>How far the running sync's counted stage is (0..1); with no count yet the bar runs indeterminate.</summary>
    [ObservableProperty] private double _syncFraction;
    [ObservableProperty] private bool _isSyncCounted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    /// <summary>Songs downloaded so far of the running Download everything (0..1).</summary>
    [ObservableProperty] private double _downloadFraction;
    [ObservableProperty] private bool _isDownloadCounted;

    /// <summary>Something is downloaded: Remove downloads has something to remove.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveDownloads))]
    private bool _hasDownloads;

    /// <summary>Sync now / Download everything: not while one already runs, and not while
    /// signing in or out (a download started mid-sign-out ran on until the revoked key failed it).
    /// Sync also not once the computer's certificate changed: only Sign out leads on from there.</summary>
    public bool CanSync => IsIdle && !IsSyncing && CanUseServer;
    public bool CanDownload => IsIdle && !IsDownloading;
    [ObservableProperty] private string _downloadText = string.Empty;

    /// <summary>A send of the phone's songs runs (from this page, or one still running when it opened).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadLabel), nameof(UploadTileLabel))]
    private bool _isUploading;

    /// <summary>The send row's status: "Sending 3 of 40", then "Sent 40 · 2 already on your computer · 1 failed".</summary>
    [ObservableProperty] private string _uploadText = string.Empty;

    /// <summary>The send row's label: a second tap while sending stops it (what was sent stays sent).</summary>
    public string UploadLabel => IsUploading ? "Stop sending" : "Send phone songs to computer";

    /// <summary>The send tile's short label (its spoken name stays <see cref="UploadLabel"/>).</summary>
    public string UploadTileLabel => IsUploading ? "Stop sending" : "Send to computer";

    /// <summary>Not while signing in or out, nor once the computer's certificate changed.</summary>
    public bool CanUpload => IsIdle && CanUseServer;

    /// <summary>"Downloaded: 12 songs · 340 MB" (the Downloaded row's spoken name).</summary>
    [ObservableProperty] private string _storageText = string.Empty;

    /// <summary>The Downloaded row's value: "12 songs · 340 MB".</summary>
    [ObservableProperty] private string _storageValue = string.Empty;

    [ObservableProperty] private bool _isConfirmingSignOut;
    [ObservableProperty] private bool _alsoRemoveDownloads;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveDownloads))]
    private bool _isConfirmingRemoveDownloads;

    /// <summary>Remove downloads: only with something downloaded (with nothing it still asked,
    /// then removed nothing), not while its question is open, and not mid sign-in or sign-out.</summary>
    public bool CanRemoveDownloads => HasDownloads && !IsConfirmingRemoveDownloads && IsIdle;

    // ── Sign in ──

    /// <summary>Step one: probe the address for its certificate fingerprint.</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy) return;   // Enter on the password while the check already runs
        SetError(string.Empty);
        var url = NormalizeServerAddress(ServerAddress);
        if (url == null)
        {
            SetError(Describe(NoctisErrorKind.InvalidAddress, signedIn: false), AccountField.Address);
            return;
        }
        if (UserName.Trim().Length == 0 || Password.Length == 0)
        {
            SetError("Enter your email or username and your password.", AccountField.Credentials);
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

    /// <summary>Scan QR Code is offered: the phone has a scanner (Android: Google's code scanner).</summary>
    public bool CanScanQr => Shell.QrScanner != null;

    /// <summary>
    /// Scan QR Code: no address, email, password or code to compare. The scanned text is
    /// untrusted: only an exact Noctis pairing code is used (<see cref="PairingPayload.TryParse"/>),
    /// and it is never logged. The service then sends its one-time secret only to the
    /// certificate the code pins, then the phone syncs as after a typed sign-in.
    /// </summary>
    [RelayCommand]
    private async Task ScanQrCodeAsync()
    {
        if (IsBusy || Shell.QrScanner is not { } scanner) return;
        SetError(string.Empty);
        QrScanResult scan;
        try
        {
            scan = await scanner.ScanAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"QR scan failed: {ex.GetType().Name}");
            scan = new QrScanResult(QrScanOutcome.Unavailable);
        }
        if (scan.Outcome == QrScanOutcome.Cancelled) return;
        if (scan.Outcome == QrScanOutcome.Unavailable)
        {
            SetError(ScannerUnavailableText);
            return;
        }
        if (!PairingPayload.TryParse(scan.Text, out var pairing) || pairing is null)
        {
            SetError(NotAPairingCodeText);
            return;
        }

        IsSigningInByQr = true;
        bool signedIn;
        try
        {
            signedIn = await RunAsync("Signing in…", () => _account.SignInWithPairingAsync(pairing));
        }
        finally { IsSigningInByQr = false; }
        if (!signedIn) return;
        // Whatever was typed into the form is not needed any more.
        PendingFingerprint = null;
        _probedUrl = null;
        Password = string.Empty;
        Refresh();
        await SyncCoreAsync();
    }

    internal const string ScannerUnavailableText =
        "The scanner isn't ready on this phone. Try again in a minute, or sign in with your computer's address below.";

    internal const string NotAPairingCodeText =
        "That isn't a Noctis sign-in code. Scan the code under Connect a phone on your computer.";

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

    /// <summary>The address field was left (or Next pressed on it): a typed address that cannot
    /// be one says so under the field then, not only once Sign In is pressed.</summary>
    public void CheckAddress()
    {
        if (ServerAddress.Trim().Length > 0 && NormalizeServerAddress(ServerAddress) == null)
            SetError(Describe(NoctisErrorKind.InvalidAddress, signedIn: false), AccountField.Address);
    }

    // An error under a field goes once that field is edited: it stayed up, red, over a corrected
    // address until the next Sign In.
    partial void OnServerAddressChanged(string value) => ClearErrorAbout(AccountField.Address);
    partial void OnUserNameChanged(string value) => ClearErrorAbout(AccountField.Credentials);
    partial void OnPasswordChanged(string value) => ClearErrorAbout(AccountField.Credentials);

    private void ClearErrorAbout(AccountField field)
    {
        if (HasError && ErrorField == field) SetError(string.Empty);
    }

    private void SetError(string text, AccountField field = AccountField.None)
    {
        ErrorField = field;
        ErrorText = text;
    }

    // ── Signed in ──

    [RelayCommand]
    private Task SyncNowAsync() => SyncCoreAsync();

    private async Task SyncCoreAsync()
    {
        if (!_account.IsSignedIn || _syncRunning || _account.IsSyncing) return;
        SetError(string.Empty);
        _syncRunning = true;
        _ownSyncEndPending = true;
        IsSyncing = true;
        SyncText = "Syncing…";
        try
        {
            var result = await _account.SyncNowAsync();
            SyncText = $"Synced {Count(result.Songs, "song")} and {Count(result.Playlists, "playlist")}";
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.SyncDisabled)
        {
            // The owner switched sync off on the computer: a choice, not a failure. The songs
            // still came over (the catalog and plays go through either way).
            SyncText = "Songs updated. Sync is off on your computer.";
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
        SetError(string.Empty);
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

    /// <summary>Sends the phone's own songs the computer lacks; tapped again while sending, stops.
    /// Concurrent executions allowed: that second tap must reach the command to stop the first.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendSongsAsync()
    {
        if (_uploadCts is { } running)
        {
            running.Cancel();
            return;
        }
        if (!_account.IsSignedIn) return;
        SetError(string.Empty);
        using var cts = new CancellationTokenSource();
        // A send this page did not start (running when it opened) shows "Stop sending" too: the
        // service hands back the running send, and a cancelled token stops it. Uncancelled, the
        // tap joined the send instead and only a second tap stopped it.
        if (_account.IsUploading) cts.Cancel();
        _uploadCts = cts;
        IsUploading = true;
        UploadText = "Checking what your computer has…";
        try
        {
            UploadText = DescribeUpload(await _account.SendLocalSongsAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            UploadText = "Stopped. Tap again to send the rest.";
        }
        catch (Exception ex)
        {
            UploadText = string.Empty;
            ShowError(ex);
        }
        finally
        {
            _uploadCts = null;
            IsUploading = _account.IsUploading;
        }
    }

    // The question up now (the shell's glass prompt), so a confirm from elsewhere takes it down.
    private PromptViewModel? _question;

    private void Ask(PromptViewModel question)
    {
        _question = question;
        Shell.ShowPrompt(question);
    }

    private void DropQuestion()
    {
        if (_question is { } q) Shell.ClosePrompt(q);
        _question = null;
    }

    /// <summary>One question at a time, on the glass prompt (owner 10-20: no more cards inside
    /// the page): asking to remove the downloads closes a pending sign-out question.</summary>
    [RelayCommand]
    private void AskRemoveDownloads()
    {
        DropQuestion();   // the one before is cancelled first, so its reset cannot undo this one
        IsConfirmingSignOut = false;
        IsConfirmingRemoveDownloads = true;
        Ask(new PromptViewModel(Shell, "Remove all downloads?",
            $"{StorageValue} leave this phone. They stay on your computer and still stream.",
            new[] { new PromptAction("Remove Downloads", _ => ConfirmRemoveDownloadsCommand.ExecuteAsync(null), isDestructive: true) },
            cancelled: () => IsConfirmingRemoveDownloads = false));
    }

    [RelayCommand]
    private void CancelRemoveDownloads()
    {
        IsConfirmingRemoveDownloads = false;
        DropQuestion();
    }

    [RelayCommand]
    private async Task ConfirmRemoveDownloadsAsync()
    {
        IsConfirmingRemoveDownloads = false;
        DropQuestion();
        await RunAsync("Removing downloads…", () => _account.RemoveAllDownloadsAsync());
        // The last download's report goes too: it put "Downloads finished" back under the
        // Download everything card over a phone that had just removed them all.
        if (!IsDownloading) _lastDownload = null;
        DownloadText = string.Empty;
        RefreshDownloads();
    }

    [RelayCommand]
    private void AskSignOut()
    {
        DropQuestion();
        IsConfirmingRemoveDownloads = false;
        AlsoRemoveDownloads = false;
        IsConfirmingSignOut = true;
        // Apple's way to ask "and the downloads too?": a second red action, only with something
        // downloaded to remove (the switch row asked it with nothing on the phone).
        var actions = new List<PromptAction> { new("Sign Out", _ => ConfirmSignOutCommand.ExecuteAsync(null), isDestructive: true) };
        if (HasDownloads)
            actions.Add(new PromptAction("Sign Out and Remove Downloads", _ =>
            {
                AlsoRemoveDownloads = true;
                return ConfirmSignOutCommand.ExecuteAsync(null);
            }, isDestructive: true));
        Ask(new PromptViewModel(Shell, SignOutQuestion, "Its songs leave this phone's library. You can sign in again any time.",
            actions, cancelled: () => IsConfirmingSignOut = false));
    }

    [RelayCommand]
    private void CancelSignOut()
    {
        IsConfirmingSignOut = false;
        DropQuestion();
    }

    [RelayCommand]
    private async Task ConfirmSignOutAsync()
    {
        IsConfirmingSignOut = false;
        DropQuestion();
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
        StatusText = status;
        IsBusy = true;
        SetError(string.Empty);
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
        // Signing in, an error about the address or the password goes under that field; a QR
        // sign-in typed neither, so its errors are the page's.
        SetError(kind is { } k ? (IsSigningInByQr ? DescribeQr(k) : Describe(k, signedIn)) : "Something went wrong. Try again.",
            kind is { } f && !signedIn && !IsSigningInByQr ? FieldFor(f) : AccountField.None);
        Refresh();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _marshal(Refresh);

    private void OnSyncProgress(object? sender, NoctisSyncProgress progress) => _marshal(() =>
    {
        // The finished text comes from the result (or the error); a late Done/Failed must not overwrite it.
        if (progress.Stage is NoctisSyncStage.Done or NoctisSyncStage.Failed or NoctisSyncStage.Idle)
        {
            // This page's own sync ends with one Done or Failed. It is posted before the task
            // completes, but on a phone it can land after the page's catch already reset
            // _syncRunning, so it is matched by flag: it read as a background sync failing
            // and replaced the page's own text ("Songs updated…") with "didn't finish".
            SyncFraction = 0;
            IsSyncCounted = false;
            if (_ownSyncEndPending && progress.Stage != NoctisSyncStage.Idle) _ownSyncEndPending = false;
            else if (!_syncRunning && progress.Stage == NoctisSyncStage.Done) SyncText = "Sync finished";
            // A background sync (the one at app start) that failed left its last stage up
            // ("Getting covers… 812 of 1332") with no error; this page's own syncs show theirs.
            else if (!_syncRunning && progress.Stage == NoctisSyncStage.Failed) SyncText = "The last sync didn't finish. Tap Sync now to try again.";
            IsSyncing = _syncRunning || _account.IsSyncing;
            return;
        }
        SyncText = DescribeStage(progress);
        IsSyncCounted = progress.Total > 0;
        SyncFraction = progress.Total > 0 ? Math.Clamp(progress.Done / (double)progress.Total, 0, 1) : 0;
        IsSyncing = true;
    });

    private void OnDownloadProgress(object? sender, NoctisDownloadProgress progress) => _marshal(() =>
    {
        // Sign-out's last report (nothing running, signed out) is not a download that finished:
        // kept, it put "Downloads finished" back on the page at the next sign-in.
        _lastDownload = progress.Pending == 0 && !_account.IsSignedIn ? null : progress;
        RefreshDownloads();
    });

    private void OnUploadProgress(object? sender, NoctisUploadProgress progress) => _marshal(() =>
    {
        // The send's last report is posted before its task completes, but the page's await
        // resumes first: landing after the result it put "Sent 40 of 40" over the summary and
        // the label back on "Stop sending". A send that is over has nothing left to report.
        if (_uploadCts is null && !_account.IsUploading) return;
        IsUploading = true;
        UploadText = DescribeUploadProgress(progress);
    });

    private void Refresh()
    {
        var account = _account.Account;
        var wasSignedIn = IsSignedIn;
        IsSignedIn = _account.IsSignedIn && account != null;
        if (wasSignedIn && !IsSignedIn && _account.WasSignedOutByComputer)
            SetError(Describe(NoctisErrorKind.SignedOut, signedIn: false));
        var shownName = account == null ? string.Empty : ShownName(account);
        AccountText = account == null ? string.Empty : $"{shownName} on {ServerLabel(account)}";
        DisplayName = shownName;
        EmailText = account != null && !string.Equals(shownName, account.UserName, StringComparison.OrdinalIgnoreCase)
            ? account.UserName : string.Empty;
        ServerText = account == null ? string.Empty : $"on {ServerLabel(account)}";
        ComputerName = account == null ? string.Empty : ServerLabel(account);
        Initials = InitialsOf(shownName);
        AvatarPath = IsSignedIn ? _account.AvatarPath : null;
        RefreshSyncTime();
        IsSyncing = _syncRunning || _account.IsSyncing;
        IsUploading = _uploadCts != null || _account.IsUploading;
        if (!IsSignedIn)
        {
            IsCertificateChanged = false;
            IsConfirmingSignOut = false;
            IsConfirmingRemoveDownloads = false;
            DropQuestion();
        }
        RefreshDownloads();
    }

    /// <summary>"Synced 5 min ago" goes stale while the page stays open: the page re-reads it
    /// once a minute (AccountPage's ticker), as the desktop's phone list does.</summary>
    public void RefreshSyncTime() =>
        LastSyncText = SyncedText(_account.Account?.LastSyncUtc, DateTime.UtcNow);

    private void RefreshDownloads()
    {
        StorageValue = $"{Count(_account.DownloadedCount, "song")} · {FormatBytes(_account.DownloadedBytes)}";
        StorageText = $"Downloaded: {StorageValue}";
        HasDownloads = _account.DownloadedCount > 0;
        var pending = _lastDownload is { Pending: > 0 };
        IsDownloading = _downloadRunning || pending;
        IsDownloadCounted = pending;
        DownloadFraction = _lastDownload is { Pending: > 0 } d ? d.Completed / (double)(d.Completed + d.Pending) : 0;
        if (_lastDownload is not { } p) return;
        // Songs whose file is gone from the computer are told apart: a retry cannot fetch them.
        var otherFailures = p.Failed - p.Missing;
        var failed = (p.Missing > 0 ? $" · {p.Missing} missing on your computer" : string.Empty)
                     + (otherFailures > 0 ? $" · {otherFailures} failed" : string.Empty);
        DownloadText = pending
            ? $"Downloading {p.Completed} of {p.Completed + p.Pending}{failed}"
            : _downloadRunning ? DownloadText : $"Downloads finished{failed}";
    }

    public override void OnClosed()
    {
        // A sign-out or remove-downloads question does not outlive the page (DismissSheet does
        // not close prompts).
        IsConfirmingSignOut = false;
        IsConfirmingRemoveDownloads = false;
        DropQuestion();
        _account.StateChanged -= OnStateChanged;
        _account.SyncProgress -= OnSyncProgress;
        _account.DownloadProgress -= OnDownloadProgress;
        _account.UploadProgress -= OnUploadProgress;
        Password = string.Empty;
    }

    // ── Wording ──

    /// <summary>The message for a failed call. <paramref name="signedIn"/> separates a pinned
    /// certificate that changed (sign out to trust a new one) from one that changed mid-sign-in.</summary>
    internal static string Describe(NoctisErrorKind kind, bool signedIn) => kind switch
    {
        NoctisErrorKind.Unreachable =>
            "Can't reach your computer. Check that Noctis is open on it, Noctis on your phone is turned on, and both are on the same Wi-Fi.",
        NoctisErrorKind.CertificateChanged => signedIn
            ? "Your computer's security code changed. Sign out and sign in again to trust the new one."
            : "Your computer's security code changed while signing in. Try again and check the code.",
        NoctisErrorKind.BadCredentials => "Wrong email, username or password.",
        NoctisErrorKind.LockedOut => "Too many failed sign-ins. Wait a few minutes, then try again.",
        NoctisErrorKind.SyncDisabled => "Sync is off on your computer. Turn on Sync with your phone in Settings, Account & Devices.",
        NoctisErrorKind.SignedOut => "Your computer signed this phone out. Sign in again.",
        NoctisErrorKind.InvalidAddress =>
            "Enter your computer's address as shown under Connect a phone, like 192.168.1.20 or https://192.168.1.20:4747.",
        NoctisErrorKind.StorageFull =>
            "Your phone is almost full, so downloads stopped (1.5 GB is always left free). Remove some downloads or free up space.",
        NoctisErrorKind.FileMissing => "This song's file is missing on your computer.",
        NoctisErrorKind.NotNoctis =>
            "Something answered at that address, but it isn't Noctis. Check the address and port shown under Connect a phone.",
        NoctisErrorKind.UnknownHost =>
            "No computer answers to that name. Type the address shown under Connect a phone, like 192.168.1.20.",
        NoctisErrorKind.Outdated => "Noctis on your computer is too old to connect a phone. Update it, then try again.",
        NoctisErrorKind.WifiRequired => "Downloads are set to Wi-Fi only. Connect to Wi-Fi, or change this in Settings, Downloads.",
        _ => "Your computer couldn't finish that. Try again in a moment.",
    };

    /// <summary>A failed QR sign-in: the code's own failures, else the usual wording.</summary>
    internal static string DescribeQr(NoctisErrorKind kind) => kind switch
    {
        NoctisErrorKind.PairingExpired =>
            "This code has expired or was already used. Scan the new code your computer shows now.",
        NoctisErrorKind.CertificateChanged =>
            "The computer at that address isn't the one showing the code, so nothing was sent. Check that your phone is on the same Wi-Fi, then scan again.",
        NoctisErrorKind.Unreachable or NoctisErrorKind.NotNoctis or NoctisErrorKind.UnknownHost =>
            "Can't reach your computer. Check that your phone is on the same Wi-Fi as your computer, then scan again.",
        _ => Describe(kind, signedIn: false),
    };

    /// <summary>Which sign-in field an error is about: the address (it is wrong, or nothing
    /// Noctis answers there) or the email and password; anything else is the page's.</summary>
    internal static AccountField FieldFor(NoctisErrorKind kind) => kind switch
    {
        NoctisErrorKind.InvalidAddress or NoctisErrorKind.Unreachable or NoctisErrorKind.NotNoctis
            or NoctisErrorKind.UnknownHost => AccountField.Address,
        NoctisErrorKind.BadCredentials or NoctisErrorKind.LockedOut => AccountField.Credentials,
        _ => AccountField.None,
    };

    internal static string DescribeStage(NoctisSyncProgress progress)
    {
        var counts = progress.Total > 0 ? $" {progress.Done} of {progress.Total}" : string.Empty;
        return progress.Stage switch
        {
            NoctisSyncStage.Catalog => $"Getting your library…{counts}",
            NoctisSyncStage.Covers => $"Getting covers…{counts}",
            NoctisSyncStage.State => "Syncing favorites and ratings…",
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

    /// <summary>The computer's security code, grouped exactly as the computer shows it (SecurityCode).</summary>
    internal static string FormatFingerprint(string? fingerprint) => SecurityCode.Format(fingerprint);

    /// <summary>"Sending 3 of 40" (the song going out now); "Checking what your computer has…" before.</summary>
    internal static string DescribeUploadProgress(NoctisUploadProgress progress) =>
        progress.Total <= 0 ? "Checking what your computer has…"
        : progress.Done >= progress.Total ? $"Sent {progress.Total} of {progress.Total}"
        : $"Sending {progress.Done + 1} of {progress.Total}";

    /// <summary>"Sent 40 · 2 already on your computer · 1 failed"; a send with nothing to do says why.</summary>
    internal static string DescribeUpload(NoctisUploadResult result)
    {
        if (result.Sent + result.AlreadyThere + result.Failed == 0)
            return result.Previously > 0 ? "Everything is already on your computer" : "No songs on this phone to send";
        var text = new StringBuilder($"Sent {result.Sent}");
        if (result.AlreadyThere > 0) text.Append($" · {result.AlreadyThere} already on your computer");
        if (result.Failed > 0) text.Append($" · {result.Failed} failed");
        return text.ToString();
    }

    /// <summary>The account's name as shown: the desktop's profile name, else the email it signed in with.</summary>
    internal static string ShownName(NoctisAccount account) =>
        string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName!;

    /// <summary>The desktop's own name when it gave one, else the address's host.</summary>
    internal static string ServerLabel(NoctisAccount account) =>
        !string.IsNullOrWhiteSpace(account.ServerName) ? account.ServerName
        : Uri.TryCreate(account.ServerUrl, UriKind.Absolute, out var uri) ? uri.Host
        : account.ServerUrl;

    /// <summary>Up to two letters for the avatar fallback: the first letters of the first two
    /// words ("John Doe" → "JD"), else the first two of one word ("owner" → "OW"); empty for none.</summary>
    internal static string InitialsOf(string? name)
    {
        // An email (a desktop with no profile name): the part before the @. The domain's dots
        // split it into words, so "tester@example.com" read "TC".
        var at = (name ?? string.Empty).IndexOf('@');
        if (at > 0) name = name![..at];
        var words = (name ?? string.Empty).Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var letters = words.Length >= 2
            ? new[] { FirstLetter(words[0]), FirstLetter(words[1]) }
            : words.Length == 1 ? words[0].EnumerateRunes().Where(Rune.IsLetterOrDigit).Take(2).Select(r => r.ToString()).ToArray()
            : Array.Empty<string?>();
        return string.Concat(letters.Where(l => l != null)).ToUpperInvariant();

        static string? FirstLetter(string word) => word.EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(r => r.ToString()).FirstOrDefault();
    }

    internal static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>The phone's one wording for the last sync, on this page and Settings' profile
    /// card alike: "Synced just now", "Synced 5 min ago", "Synced 3 hr ago", "Synced yesterday",
    /// "Synced 4 days ago", "Synced 2 Oct"; "Not synced yet" before the first sync.</summary>
    internal static string SyncedText(DateTime? syncedUtc, DateTime nowUtc)
    {
        if (syncedUtc is not { } synced) return "Not synced yet";
        var ago = nowUtc - synced;
        if (ago < TimeSpan.FromMinutes(1)) return "Synced just now";
        if (ago < TimeSpan.FromHours(1)) return $"Synced {(int)ago.TotalMinutes} min ago";
        var day = synced.ToLocalTime().Date;
        var today = nowUtc.ToLocalTime().Date;
        if (day == today) return $"Synced {(int)ago.TotalHours} hr ago";
        if (day == today.AddDays(-1)) return "Synced yesterday";
        if (day > today.AddDays(-7)) return $"Synced {(today - day).Days} days ago";
        return $"Synced {day:d MMM}";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>The sign-in field an error is shown under (<see cref="AccountPageViewModel.ErrorField"/>).</summary>
public enum AccountField { None, Address, Credentials }
