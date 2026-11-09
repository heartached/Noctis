using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings → Account over a fake account service: sign-in with the fingerprint check,
/// error wording, sync, downloads, sign-out; the sheet's download actions; desktop plays.</summary>
public class MobileAccountTests
{
    private const string Fingerprint =
        "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    /// <summary>In-memory INoctisAccountService: desktop songs are the noctis-remote:// paths.</summary>
    internal sealed class FakeAccount : INoctisAccountService
    {
        public NoctisAccount? Account { get; set; }
        public bool IsSignedIn => Account != null;
        public bool IsSyncing { get; set; }
        public bool WasSignedOutByComputer { get; set; }
        public string? AvatarPath { get; set; }
        public ComputerScrobbling? ComputerScrobbling { get; set; }

        public event EventHandler? StateChanged;

        public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
        public event EventHandler<NoctisSyncProgress>? SyncProgress;
        /// <summary>A progress event the way a phone delivers it: posted, so it can land after the sync's task already completed.</summary>
        public void RaiseLateProgress(NoctisSyncStage stage) => SyncProgress?.Invoke(this, new NoctisSyncProgress(stage, 0, 0));
        public event EventHandler<NoctisDownloadProgress>? DownloadProgress;
        public event EventHandler? PlaylistsChanged { add { } remove { } }

        public Exception? ProbeError { get; set; }
        public Exception? SignInError { get; set; }
        public Exception? SyncError { get; set; }
        public NoctisSyncResult SyncResult { get; set; } = new(2, 1, 0, 0, 0);

        public List<string> Probes { get; } = new();
        public List<(string Url, string User, string Password, string Fingerprint)> SignIns { get; } = new();
        public List<bool> SignOuts { get; } = new();
        public int Syncs { get; private set; }
        public List<(Track Track, DateTime Utc)> Plays { get; } = new();
        public HashSet<Guid> DownloadedIds { get; } = new();
        public List<List<Track>> DownloadCalls { get; } = new();
        public List<List<Track>> RemoveCalls { get; } = new();
        public int DownloadAllCalls { get; private set; }
        public int RemoveAllCalls { get; private set; }

        public static NoctisAccount SignedInAs(string user = "owner", string serverName = "Studio PC") => new()
        {
            ServerUrl = "https://192.168.1.20:4747", UserName = user, DeviceKey = "nk_test", Fingerprint = Fingerprint,
            DeviceId = "device-0001", DeviceName = "Test Phone", ServerName = serverName,
        };

        /// <summary>Set: the probe waits for this before answering (the page shows "Connecting…").</summary>
        public TaskCompletionSource? ProbeGate { get; set; }

        public async Task<string> ProbeFingerprintAsync(string serverUrl, CancellationToken ct = default)
        {
            Probes.Add(serverUrl);
            if (ProbeGate != null) await ProbeGate.Task.WaitAsync(ct);
            if (ProbeError != null) throw ProbeError;
            return Fingerprint;
        }

        public Task SignInAsync(string serverUrl, string userName, string password, string confirmedFingerprint, CancellationToken ct = default)
        {
            SignIns.Add((serverUrl, userName, password, confirmedFingerprint));
            if (SignInError != null) return Task.FromException(SignInError);
            Account = SignedInAs(userName) with { ServerUrl = serverUrl, Fingerprint = confirmedFingerprint };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public List<Noctis.Services.Server.PairingPayload> PairingSignIns { get; } = new();

        public Task SignInWithPairingAsync(Noctis.Services.Server.PairingPayload pairing, CancellationToken ct = default)
        {
            PairingSignIns.Add(pairing);
            if (SignInError != null) return Task.FromException(SignInError);
            Account = SignedInAs("owner") with { ServerUrl = pairing.ServerUrls.First(), Fingerprint = pairing.Fingerprint };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SignOutAsync(bool removeDownloads, CancellationToken ct = default)
        {
            SignOuts.Add(removeDownloads);
            Account = null;
            AvatarPath = null;
            if (removeDownloads) DownloadedIds.Clear();
            // As the service does (NoctisAccountService.SignOutAsync): a final report with nothing running.
            DownloadProgress?.Invoke(this, new NoctisDownloadProgress(0, 0, 0, DownloadedBytes));
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<NoctisSyncResult> SyncNowAsync(CancellationToken ct = default)
        {
            Syncs++;
            SyncProgress?.Invoke(this, new NoctisSyncProgress(NoctisSyncStage.Catalog, 1, 2));
            if (SyncError != null) return Task.FromException<NoctisSyncResult>(SyncError);
            Account = Account! with { LastSyncUtc = DateTime.UtcNow };
            SyncProgress?.Invoke(this, new NoctisSyncProgress(NoctisSyncStage.Done, 2, 2));
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(SyncResult);
        }

        public void RecordPlay(Track track, DateTime playedUtc) => Plays.Add((track, playedUtc));
        /// <summary>Plays recorded to wait for their verdict, and the verdicts given.</summary>
        public List<Track> Started { get; } = new();
        public List<(Track Track, PlayScrobble Verdict)> Decided { get; } = new();
        public void RecordStartedPlay(Track track, DateTime playedUtc) { Started.Add(track); RecordPlay(track, playedUtc); }
        public void DecidePlay(Track track, PlayScrobble verdict) => Decided.Add((track, verdict));

        public List<PlayQueueShare> SavedQueues { get; } = new();
        public Task SavePlayQueueAsync(PlayQueueShare share, CancellationToken ct = default)
        {
            SavedQueues.Add(share);
            return Task.CompletedTask;
        }

        public void RaiseSyncProgress(NoctisSyncProgress progress) => SyncProgress?.Invoke(this, progress);

        public bool IsRemote(Track track) => track.FilePath.StartsWith("noctis-remote://", StringComparison.Ordinal);
        public bool IsDownloaded(Track track) => DownloadedIds.Contains(track.Id);
        public long DownloadedBytes => DownloadedIds.Count * 5L * 1024 * 1024;
        public int DownloadedCount => DownloadedIds.Count;

        public Task DownloadAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
        {
            var list = tracks.ToList();
            DownloadCalls.Add(list);
            foreach (var t in list) DownloadedIds.Add(t.Id);
            return Task.CompletedTask;
        }

        public Task DownloadAllAsync(CancellationToken ct = default)
        {
            DownloadAllCalls++;
            DownloadProgress?.Invoke(this, new NoctisDownloadProgress(2, 1, 0, 0));
            DownloadedIds.Add(Guid.NewGuid());
            DownloadedIds.Add(Guid.NewGuid());
            DownloadProgress?.Invoke(this, new NoctisDownloadProgress(0, 2, 1, DownloadedBytes));
            return Task.CompletedTask;
        }

        public Task RemoveDownloadsAsync(IEnumerable<Track> tracks)
        {
            var list = tracks.ToList();
            RemoveCalls.Add(list);
            foreach (var t in list) DownloadedIds.Remove(t.Id);
            return Task.CompletedTask;
        }

        public Task RemoveAllDownloadsAsync()
        {
            RemoveAllCalls++;
            DownloadedIds.Clear();
            return Task.CompletedTask;
        }

        public string? ResolvePlaybackUri(string filePath) => null;

        public void RaiseDownloadProgress(NoctisDownloadProgress progress) => DownloadProgress?.Invoke(this, progress);

        public bool IsUploading { get; set; }
        public event EventHandler<NoctisUploadProgress>? UploadProgress;
        public NoctisUploadResult UploadResult { get; set; } = new(40, 2, 1, 0);
        public Exception? UploadError { get; set; }
        public int UploadCalls { get; private set; }

        /// <summary>Set: the send reports "2 of 40 done" then waits for this (or the token) before finishing.</summary>
        public TaskCompletionSource? UploadGate { get; set; }

        public async Task<NoctisUploadResult> SendLocalSongsAsync(CancellationToken ct = default)
        {
            UploadCalls++;
            IsUploading = true;
            try
            {
                if (UploadError != null) throw UploadError;
                UploadProgress?.Invoke(this, new NoctisUploadProgress(0, 0));
                UploadProgress?.Invoke(this, new NoctisUploadProgress(2, 40));
                if (UploadGate != null) await UploadGate.Task.WaitAsync(ct);
                return UploadResult;
            }
            finally { IsUploading = false; }
        }
    }

    private static Track RemoteSong(string title)
    {
        var t = MobileFixtures.Song(title);
        t.FilePath = "noctis-remote://tr-" + t.Id.ToString("N");
        return t;
    }

    private static ShellViewModel ShellWith(MobileFixtures.Rig rig, INoctisAccountService? account) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };

    private static AccountPageViewModel OpenAccount(ShellViewModel shell)
    {
        shell.OpenAccountCommand.Execute(null);
        return Assert.IsType<AccountPageViewModel>(shell.CurrentPage);
    }

    // ── Address ──

    [Theory]
    [InlineData("192.168.1.20", "https://192.168.1.20:4747")]
    [InlineData(" 192.168.1.20:5000 ", "https://192.168.1.20:5000")]
    [InlineData("https://192.168.1.20:4747", "https://192.168.1.20:4747")]
    [InlineData("https://192.168.1.20:4747/", "https://192.168.1.20:4747")]
    [InlineData("HTTPS://Studio-PC.local", "https://studio-pc.local:4747")]
    [InlineData("[fe80::1]", "https://[fe80::1]:4747")]
    [InlineData("[fe80::1]:6000", "https://[fe80::1]:6000")]
    [InlineData("http://192.168.1.20:4747", null)]           // https only: cleartext is off
    [InlineData("ftp://192.168.1.20", null)]
    [InlineData("https://192.168.1.20:4747/rest", null)]      // paths are refused, not dropped
    [InlineData("192.168.1.20/rest/ping", null)]
    [InlineData("https://192.168.1.20:4747?x=1", null)]
    [InlineData("https://192.168.1.20:4747#top", null)]
    [InlineData("https://user:pw@192.168.1.20:4747", null)]
    [InlineData("192.168.1.20:", null)]
    [InlineData("192.168.1.20:70000", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void NormalizeServerAddress_AcceptsAHostOrAnHttpsRootOnly(string typed, string? expected)
    {
        Assert.Equal(expected, AccountPageViewModel.NormalizeServerAddress(typed));
    }

    [Fact]
    public void Fingerprint_IsShownWhole_InFourDigitGroups_LikeTheComputer()
    {
        var shown = AccountPageViewModel.FormatFingerprint(Fingerprint);
        var lines = shown.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.Equal(4, l.Split(' ').Length));
        Assert.Equal("ABCD EF01 2345 6789", lines[0]);
        Assert.Equal(Fingerprint.Replace(":", ""), shown.Replace("\n", "").Replace(" ", ""));
        Assert.Equal(Noctis.Services.Server.SecurityCode.Format(Fingerprint), shown);
    }

    // ── Sign in ──

    [Fact]
    public async Task SignIn_ProbesThenConfirms_ThenSignsInAndSyncs_AndForgetsThePassword()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        var page = OpenAccount(ShellWith(rig, account));
        Assert.True(page.IsFormVisible);
        Assert.False(page.IsSignedIn);

        page.ServerAddress = "192.168.1.20";
        page.UserName = " owner ";
        page.Password = "hunter2";
        await page.SignInCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "https://192.168.1.20:4747" }, account.Probes);
        Assert.Empty(account.SignIns);                                   // nothing sent before the user compares
        Assert.True(page.IsConfirmingFingerprint);
        Assert.False(page.IsFormVisible);
        Assert.Equal(Fingerprint.Replace(":", ""), page.FingerprintDisplay.Replace("\n", "").Replace(" ", ""));
        Assert.Equal("https://192.168.1.20:4747", page.ServerAddress);

        await page.TrustAndSignInCommand.ExecuteAsync(null);

        var signIn = Assert.Single(account.SignIns);
        Assert.Equal(("https://192.168.1.20:4747", "owner", "hunter2", Fingerprint), signIn);
        Assert.Equal(1, account.Syncs);
        Assert.True(page.IsSignedIn);
        Assert.False(page.IsConfirmingFingerprint);
        Assert.Equal(string.Empty, page.Password);
        Assert.Equal("owner on Studio PC", page.AccountText);
        Assert.Equal("Synced 2 songs and 1 playlist", page.SyncText);
        Assert.Equal("Synced just now", page.LastSyncText);
        Assert.False(page.IsBusy);
        Assert.False(page.HasError);
    }

    [Fact]
    public async Task SignIn_Cancel_AtTheFingerprint_SendsNothing()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        var page = OpenAccount(ShellWith(rig, account));
        page.ServerAddress = "192.168.1.20";
        page.UserName = "owner";
        page.Password = "pw";
        await page.SignInCommand.ExecuteAsync(null);

        page.CancelFingerprintCommand.Execute(null);

        Assert.Empty(account.SignIns);
        Assert.True(page.IsFormVisible);
        Assert.False(page.IsSignedIn);
    }

    [Fact]
    public async Task SignIn_BadAddress_OrMissingFields_NeverProbes()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        var page = OpenAccount(ShellWith(rig, account));
        page.ServerAddress = "https://192.168.1.20:4747/rest";
        page.UserName = "owner";
        page.Password = "pw";
        await page.SignInCommand.ExecuteAsync(null);
        Assert.Equal(AccountPageViewModel.Describe(NoctisErrorKind.InvalidAddress, false), page.ErrorText);

        page.ServerAddress = "192.168.1.20";
        page.Password = "";
        await page.SignInCommand.ExecuteAsync(null);
        Assert.True(page.HasError);
        Assert.Empty(account.Probes);
    }

    [Theory]
    [InlineData(NoctisErrorKind.Unreachable, "Can't reach your computer. Check that Noctis is open on it, Noctis on your phone is turned on, and both are on the same Wi-Fi.")]
    [InlineData(NoctisErrorKind.CertificateChanged, "Your computer's security code changed while signing in. Try again and check the code.")]
    [InlineData(NoctisErrorKind.BadCredentials, "Wrong email, username or password.")]
    [InlineData(NoctisErrorKind.LockedOut, "Too many failed sign-ins. Wait a few minutes, then try again.")]
    [InlineData(NoctisErrorKind.SyncDisabled, "Sync is off on your computer. Turn on Sync with your phone in Settings, Account & Devices.")]
    [InlineData(NoctisErrorKind.SignedOut, "Your computer signed this phone out. Sign in again.")]
    [InlineData(NoctisErrorKind.InvalidAddress, "Enter your computer's address as shown under Connect a phone, like 192.168.1.20 or https://192.168.1.20:4747.")]
    [InlineData(NoctisErrorKind.Server, "Your computer couldn't finish that. Try again in a moment.")]
    [InlineData(NoctisErrorKind.NotNoctis, "Something answered at that address, but it isn't Noctis. Check the address and port shown under Connect a phone.")]
    [InlineData(NoctisErrorKind.UnknownHost, "No computer answers to that name. Type the address shown under Connect a phone, like 192.168.1.20.")]
    [InlineData(NoctisErrorKind.Outdated, "Noctis on your computer is too old to connect a phone. Update it, then try again.")]
    public async Task SignIn_EachErrorKind_IsWorded(NoctisErrorKind kind, string message)
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { SignInError = new NoctisServerException(kind, "detail") };
        var page = OpenAccount(ShellWith(rig, account));
        page.ServerAddress = "192.168.1.20";
        page.UserName = "owner";
        page.Password = "pw";
        await page.SignInCommand.ExecuteAsync(null);
        await page.TrustAndSignInCommand.ExecuteAsync(null);

        Assert.Equal(message, page.ErrorText);
        Assert.False(page.IsSignedIn);
        Assert.False(page.IsBusy);
        Assert.False(page.IsConfirmingFingerprint);                       // a retry re-checks the fingerprint
        Assert.Equal(0, account.Syncs);
    }

    [Fact]
    public async Task Probe_Failure_IsWorded_AndLeavesTheForm()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { ProbeError = new NoctisServerException(NoctisErrorKind.Unreachable, "timeout") };
        var page = OpenAccount(ShellWith(rig, account));
        page.ServerAddress = "10.0.0.2";
        page.UserName = "owner";
        page.Password = "pw";
        await page.SignInCommand.ExecuteAsync(null);

        Assert.StartsWith("Can't reach your computer.", page.ErrorText);
        Assert.True(page.IsFormVisible);
        Assert.Equal("pw", page.Password);                                 // kept for the retry
    }

    [Fact]
    public void SignInButton_IsOffered_OnlyWithAllThreeFieldsAndAnAddress()
    {
        using var rig = MobileFixtures.MakeRig();
        var page = OpenAccount(ShellWith(rig, new FakeAccount()));
        Assert.False(page.CanSignIn);
        page.ServerAddress = "192.168.1.20";
        page.UserName = "owner";
        Assert.False(page.CanSignIn);                                      // no password yet
        page.Password = "pw";
        Assert.True(page.CanSignIn);
        page.ServerAddress = "http://192.168.1.20";                         // not an address the phone takes
        Assert.False(page.CanSignIn);
        page.ServerAddress = "192.168.1.20:4747";
        page.UserName = "   ";
        Assert.False(page.CanSignIn);
    }

    /// <summary>Each error shows under the field it is about, and goes once that field is edited
    /// (before, one red line under the Sign In card, still up over the corrected address).</summary>
    [Fact]
    public async Task SignIn_Errors_ShowUnderTheirField_AndGoWhenItIsEdited()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { ProbeError = new NoctisServerException(NoctisErrorKind.Unreachable, "timeout") };
        var page = OpenAccount(ShellWith(rig, account));
        page.ServerAddress = "10.0.0.2";
        page.UserName = "owner";
        page.Password = "pw";
        await page.SignInCommand.ExecuteAsync(null);
        Assert.True(page.HasAddressError);
        Assert.False(page.HasCredentialsError);
        Assert.False(page.HasGeneralError);
        page.Password = "pw2";                                             // another field: stays
        Assert.True(page.HasAddressError);
        page.ServerAddress = "10.0.0.3";                                   // its field: gone
        Assert.False(page.HasError);

        account.ProbeError = null;
        account.SignInError = new NoctisServerException(NoctisErrorKind.BadCredentials, "no");
        await page.SignInCommand.ExecuteAsync(null);
        await page.TrustAndSignInCommand.ExecuteAsync(null);
        Assert.True(page.HasCredentialsError);
        Assert.Equal("Wrong email, username or password.", page.ErrorText);
        page.ServerAddress = "10.0.0.4";                                   // not its field: stays
        Assert.True(page.HasCredentialsError);
        page.Password = "right";
        Assert.False(page.HasError);

        account.SignInError = new NoctisServerException(NoctisErrorKind.CertificateChanged, "pin");
        await page.SignInCommand.ExecuteAsync(null);
        await page.TrustAndSignInCommand.ExecuteAsync(null);
        Assert.True(page.HasGeneralError);                                 // about no one field
    }

    [Fact]
    public void LeavingTheAddressField_SaysWhatIsWrongWithIt()
    {
        using var rig = MobileFixtures.MakeRig();
        var page = OpenAccount(ShellWith(rig, new FakeAccount()));
        page.CheckAddress();
        Assert.False(page.HasError);                                       // empty: nothing to say yet
        page.ServerAddress = "https://192.168.1.20:4747/rest";
        page.CheckAddress();
        Assert.True(page.HasAddressError);
        Assert.Equal(AccountPageViewModel.Describe(NoctisErrorKind.InvalidAddress, false), page.ErrorText);
        page.ServerAddress = "192.168.1.20";
        page.CheckAddress();
        Assert.False(page.HasError);
    }

    [AvaloniaFact]
    public async Task SignIn_View_EnterWalksTheFields_ThenSignsIn_AndTheButtonShowsProgress()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { ProbeGate = new TaskCompletionSource() };
        var shell = ShellWith(rig, account);
        var window = MobileFixtures.Mount(shell, out var view);
        shell.OpenAccountCommand.Execute(null);
        window.UpdateLayout();
        var page = MobileFixtures.Find<AccountPage>(view);
        var vm = (AccountPageViewModel)shell.CurrentPage!;
        var address = MobileFixtures.Named<TextBox>(page, "ServerAddressBox");
        var user = MobileFixtures.Named<TextBox>(page, "UserNameBox");
        var password = MobileFixtures.Named<TextBox>(page, "PasswordBox");
        var button = MobileFixtures.Named<Button>(page, "SignInButton");
        Assert.False(button.IsEnabled);                                    // nothing typed yet

        void Enter(TextBox box)
        {
            Thread.Sleep(300);   // past the guard against one key press moving twice
            box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        }

        address.Focus();
        vm.ServerAddress = "192.168.1.20";
        Enter(address);
        Assert.True(user.IsFocused);
        vm.UserName = "owner";
        Enter(user);
        Assert.True(password.IsFocused);
        vm.Password = "pw";
        window.UpdateLayout();
        Assert.True(button.IsEnabled);
        Enter(password);
        // Let the button's opacity transition (greyed while the fields were empty) run out.
        for (var i = 0; i < 40; i++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        window.UpdateLayout();

        Assert.Single(account.Probes);
        Assert.True(vm.IsBusy);
        Assert.False(button.IsEnabled);
        Assert.Equal(1, button.Opacity);                                   // busy, not greyed out
        Assert.True(MobileFixtures.Named<TextBlock>(page, "SignInStatus").IsEffectivelyVisible);
        Assert.Equal("Connecting to your computer…", MobileFixtures.Named<TextBlock>(page, "SignInStatus").Text);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "SignInLabel").IsEffectivelyVisible);

        account.ProbeGate.SetResult();
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(vm.IsConfirmingFingerprint);
        Assert.True(MobileFixtures.Named<Button>(page, "TrustButton").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void SignIn_View_AnAddressErrorSitsUnderTheAddress()
    {
        using var rig = MobileFixtures.MakeRig();
        var shell = ShellWith(rig, new FakeAccount());
        var window = MobileFixtures.Mount(shell, out var view);
        shell.OpenAccountCommand.Execute(null);
        var vm = (AccountPageViewModel)shell.CurrentPage!;
        vm.ServerAddress = "ftp://host";
        vm.CheckAddress();
        window.UpdateLayout();
        var page = MobileFixtures.Find<AccountPage>(view);
        Assert.True(MobileFixtures.Named<TextBlock>(page, "AddressErrorLabel").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "ErrorLabel").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "CredentialsErrorLabel").IsEffectivelyVisible);
        window.Close();
    }

    /// <summary>Bug (10-14, emulator): signed out with nothing downloaded, a song list's filter
    /// still offered Downloaded, and picking it emptied the list ("0 of 11"). Signed out with
    /// copies kept (10-20), their songs are gone from the library: still nothing to show.</summary>
    [Fact]
    public void SongsFilter_OffersDownloaded_OnlySignedIn_OrWhileAListedSongIsDownloaded()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Local") });
        var account = new FakeAccount();
        var list = new SongListPageViewModel(ShellWith(rig, account), "Songs", () => rig.Library.TrackList);
        Assert.False(list.CanFilterDownloaded);

        account.DownloadedIds.Add(Guid.NewGuid());          // signed out, a kept copy of a song no longer listed
        Assert.False(list.CanFilterDownloaded);

        account.DownloadedIds.Add(rig.Library.TrackList[0].Id);   // signed out by the computer: songs and copies stay
        Assert.True(list.CanFilterDownloaded);

        account.DownloadedIds.Clear();
        account.Account = FakeAccount.SignedInAs();
        Assert.True(list.CanFilterDownloaded);
    }

    // ── Signed in ──

    [Fact]
    public async Task SignedIn_CertificateChanged_OffersOnlySignOut()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.True(page.CanUseServer);

        account.SyncError = new NoctisServerException(NoctisErrorKind.CertificateChanged, "pin mismatch");
        await page.SyncNowCommand.ExecuteAsync(null);

        Assert.Equal("Your computer's security code changed. Sign out and sign in again to trust the new one.", page.ErrorText);
        Assert.True(page.IsCertificateChanged);
        Assert.False(page.CanUseServer);                                   // Sync and Download hidden
        Assert.True(page.IsSignedIn);                                      // Sign out stays
        Assert.False(page.IsSyncing);
        // The profile card's computer row and the send tile stay on screen, so they turn off.
        Assert.False(page.CanSync);
        Assert.False(page.CanUpload);
    }

    /// <summary>Remove all downloads and Sign out each ask first; asking one closes the other,
    /// so two red confirm buttons never stand open together.</summary>
    [Fact]
    public void ConfirmQuestions_AreOneAtATime()
    {
        using var rig = MobileFixtures.MakeRig();
        var page = OpenAccount(ShellWith(rig, new FakeAccount { Account = FakeAccount.SignedInAs() }));

        page.AskRemoveDownloadsCommand.Execute(null);
        Assert.True(page.IsConfirmingRemoveDownloads);
        page.AskSignOutCommand.Execute(null);
        Assert.True(page.IsConfirmingSignOut);
        Assert.False(page.IsConfirmingRemoveDownloads);
        page.AskRemoveDownloadsCommand.Execute(null);
        Assert.True(page.IsConfirmingRemoveDownloads);
        Assert.False(page.IsConfirmingSignOut);
    }

    /// <summary>Settings' profile card: the account's name, its computer, initials (no picture);
    /// signed out, the invitation and the person glyph. It follows sign-in and sign-out.</summary>
    [Fact]
    public async Task SettingsProfileCard_ShowsTheAccount_AndFollowsSignInAndOut()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        var now = DateTime.UtcNow;
        var settings = new SettingsPageViewModel(ShellWith(rig, account)) { UtcNow = () => now };

        Assert.Equal("Sign in to your computer", settings.AccountTitle);
        Assert.Equal("Sync your library with Noctis on your PC", settings.AccountSubtitle);
        Assert.True(settings.ShowAccountGlyph);
        Assert.False(settings.ShowAccountInitials);

        await account.SignInAsync("https://192.168.1.20:4747", "John Doe", "pw", Fingerprint);
        Assert.Equal("John Doe", settings.AccountTitle);
        Assert.Equal("Studio PC · Not synced yet", settings.AccountSubtitle);
        Assert.False(settings.ShowSyncedDot);
        account.Account = account.Account! with { LastSyncUtc = now.AddMinutes(-5) };
        account.RaiseStateChanged();
        Assert.Equal("Synced with Studio PC", settings.AccountSubtitle);
        Assert.True(settings.ShowSyncedDot);
        Assert.Equal("Signed in as John Doe · Studio PC, Synced 5 min ago", settings.AccountRowName);
        Assert.Equal("JD", settings.AccountInitials);
        Assert.True(settings.ShowAccountInitials);
        Assert.False(settings.HasAccountAvatar);

        account.AvatarPath = "/data/avatar.png";
        account.RaiseStateChanged();
        Assert.Equal("/data/avatar.png", settings.AccountAvatarPath);
        Assert.False(settings.ShowAccountInitials);
        Assert.False(settings.ShowAccountGlyph);

        await account.SignOutAsync(removeDownloads: false);
        Assert.Equal("Sign in to your computer", settings.AccountTitle);
        Assert.Null(settings.AccountAvatarPath);
        Assert.True(settings.ShowAccountGlyph);
        settings.OnClosed();
    }

    [Fact]
    public async Task SyncNow_SyncOffOnTheComputer_IsANoteNotAnError()
    {
        // The computer's "Sync with your phone" switch is the owner's choice: the songs still
        // came over (catalog and plays go through), so no red error for it.
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), SyncError = new NoctisServerException(NoctisErrorKind.SyncDisabled, "50") };
        var page = OpenAccount(ShellWith(rig, account));

        await page.SyncNowCommand.ExecuteAsync(null);

        Assert.False(page.HasError);
        Assert.True(page.CanUseServer);
        Assert.Equal("Songs updated. Sync is off on your computer.", page.SyncText);
    }

    [Theory]
    [InlineData(NoctisSyncStage.Done)]    // what the service now raises when sync is off on the computer
    [InlineData(NoctisSyncStage.Failed)]  // what it raised before
    public async Task SyncNow_SyncOffOnTheComputer_TheLateEndOfThatSyncKeepsTheNote(NoctisSyncStage lateEnd)
    {
        // On the emulator the sync's own end arrived after the page's catch had run and
        // replaced "Songs updated…" with "The last sync didn't finish. Tap Sync now to try again."
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), SyncError = new NoctisServerException(NoctisErrorKind.SyncDisabled, "50") };
        var page = OpenAccount(ShellWith(rig, account));

        await page.SyncNowCommand.ExecuteAsync(null);
        account.RaiseLateProgress(lateEnd);

        Assert.Equal("Songs updated. Sync is off on your computer.", page.SyncText);
        Assert.False(page.HasError);
    }

    [Fact]
    public async Task SyncNow_Failed_TheLateEndOfThatSyncKeepsTheErrorAlone()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), SyncError = new NoctisServerException(NoctisErrorKind.Unreachable, "x") };
        var page = OpenAccount(ShellWith(rig, account));

        await page.SyncNowCommand.ExecuteAsync(null);
        account.RaiseLateProgress(NoctisSyncStage.Failed);

        Assert.True(page.HasError);
        Assert.Equal(string.Empty, page.SyncText);
    }

    [Fact]
    public async Task ABackgroundSyncFailing_AfterThePagesOwnSync_StillSaysSo()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));
        await page.SyncNowCommand.ExecuteAsync(null);   // the fake raises its Done during the sync

        account.RaiseLateProgress(NoctisSyncStage.Failed); // a later sync the page did not start

        Assert.Equal("The last sync didn't finish. Tap Sync now to try again.", page.SyncText);
    }

    [Fact]
    public void SignedOutByTheComputer_AtAppStart_TheAccountPageSaysWhy()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { WasSignedOutByComputer = true };
        var page = OpenAccount(ShellWith(rig, account));

        Assert.False(page.IsSignedIn);
        Assert.Equal("Your computer signed this phone out. Sign in again.", page.ErrorText);
    }

    [Fact]
    public void SignedOutByTheComputer_WhileThePageIsOpen_TheAccountPageSaysWhy()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.False(page.HasError);

        account.Account = null;
        account.WasSignedOutByComputer = true;
        account.RaiseStateChanged();

        Assert.Equal("Your computer signed this phone out. Sign in again.", page.ErrorText);
    }

    [Fact]
    public void SignedOutByHand_SaysNothing()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));

        account.Account = null;
        account.RaiseStateChanged();

        Assert.False(page.HasError);
    }

    [Fact]
    public void SyncProgress_ShowsTheStage()
    {
        Assert.Equal("Getting your library… 10 of 200", AccountPageViewModel.DescribeStage(new(NoctisSyncStage.Catalog, 10, 200)));
        Assert.Equal("Getting covers…", AccountPageViewModel.DescribeStage(new(NoctisSyncStage.Covers, 0, 0)));
        Assert.Equal("Syncing playlists…", AccountPageViewModel.DescribeStage(new(NoctisSyncStage.Playlists, 0, 0)));
    }

    [Fact]
    public void SyncProgress_FromAnotherSync_ShowsThenClears()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));

        account.IsSyncing = true;                                          // e.g. the startup sync
        account.RaiseSyncProgress(new NoctisSyncProgress(NoctisSyncStage.Catalog, 3, 10));
        Assert.True(page.IsSyncing);
        Assert.Equal("Getting your library… 3 of 10", page.SyncText);

        account.IsSyncing = false;
        account.RaiseSyncProgress(new NoctisSyncProgress(NoctisSyncStage.Done, 10, 10));
        Assert.False(page.IsSyncing);
        Assert.Equal("Sync finished", page.SyncText);
    }

    [Fact]
    public void SyncProgress_FromAnotherSyncThatFails_DoesNotLeaveItsLastStageUp()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));

        account.IsSyncing = true;                                          // the startup sync
        account.RaiseSyncProgress(new NoctisSyncProgress(NoctisSyncStage.Covers, 812, 1332));
        account.IsSyncing = false;
        account.RaiseSyncProgress(new NoctisSyncProgress(NoctisSyncStage.Failed, 812, 1332));

        Assert.False(page.IsSyncing);
        Assert.DoesNotContain("812", page.SyncText);
        Assert.Contains("didn't finish", page.SyncText);
    }

    [Fact]
    public void SyncAndDownloadButtons_AreOffWhileBusy()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.True(page.CanSync);
        Assert.True(page.CanDownload);

        page.IsBusy = true;                                                // e.g. signing out

        Assert.False(page.CanSync);
        Assert.False(page.CanDownload);
    }

    [Fact]
    public async Task DownloadEverything_ShowsProgress_AndTheSpaceUsed()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.Equal("Downloaded: 0 songs · 0 MB", page.StorageText);

        await page.DownloadAllCommand.ExecuteAsync(null);

        Assert.Equal(1, account.DownloadAllCalls);
        Assert.False(page.IsDownloading);
        Assert.Equal("Downloads finished · 1 failed", page.DownloadText);
        Assert.Equal("Downloaded: 2 songs · 10 MB", page.StorageText);
    }

    [Fact]
    public void DownloadResult_TellsSongsMissingOnTheComputer_ApartFromOtherFailures()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));

        account.RaiseDownloadProgress(new NoctisDownloadProgress(0, 0, 77, 0, Missing: 77));
        Assert.Equal("Downloads finished · 77 missing on your computer", page.DownloadText);

        account.RaiseDownloadProgress(new NoctisDownloadProgress(3, 1, 3, 0, Missing: 2));
        Assert.Equal("Downloading 1 of 4 · 2 missing on your computer · 1 failed", page.DownloadText);
        Assert.Equal("This song's file is missing on your computer.", AccountPageViewModel.Describe(NoctisErrorKind.FileMissing, true));
    }

    [Fact]
    public async Task SendSongs_ShowsProgress_ThenTheCounts()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), UploadGate = new TaskCompletionSource() };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.Equal("Send phone songs to computer", page.UploadLabel);
        Assert.True(page.CanUpload);

        var send = page.SendSongsCommand.ExecuteAsync(null);
        Assert.True(page.IsUploading);
        Assert.Equal("Sending 3 of 40", page.UploadText);                     // two done, the third going out
        Assert.Equal("Stop sending", page.UploadLabel);

        account.UploadGate.SetResult();
        await send;

        Assert.Equal(1, account.UploadCalls);
        Assert.False(page.IsUploading);
        Assert.Equal("Sent 40 · 2 already on your computer · 1 failed", page.UploadText);
        Assert.Equal("Send phone songs to computer", page.UploadLabel);
        Assert.False(page.HasError);
    }

    [Fact]
    public async Task SendSongs_TappedAgainWhileSending_Stops_AndSaysTheRestWaits()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), UploadGate = new TaskCompletionSource() };
        var page = OpenAccount(ShellWith(rig, account));

        var send = page.SendSongsCommand.ExecuteAsync(null);
        Assert.True(page.SendSongsCommand.CanExecute(null));                  // the second tap reaches the command
        await page.SendSongsCommand.ExecuteAsync(null);
        await send;

        Assert.Equal(1, account.UploadCalls);
        Assert.False(page.IsUploading);
        Assert.Equal("Stopped. Tap again to send the rest.", page.UploadText);
        Assert.False(page.HasError);
    }

    /// <summary>
    /// The service reports each song from its own thread and the page's Marshal posts the report
    /// to the UI thread; the page's await of the send resumes ahead of such a post (Avalonia runs
    /// the continuation first, probed headless). The last report then landed after the summary.
    /// </summary>
    [Fact]
    public async Task SendSongs_LastProgressLandingAfterTheResult_KeepsTheSummary()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), UploadGate = new TaskCompletionSource() };
        var posted = new List<Action>();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = posted.Add,
            TintFactory = rig.Shell.TintFactory,
        };
        var page = OpenAccount(shell);

        var send = page.SendSongsCommand.ExecuteAsync(null);
        account.UploadGate.SetResult();
        await send;
        foreach (var post in posted.ToList()) post();                         // the reports, landing late

        Assert.Equal("Sent 40 · 2 already on your computer · 1 failed", page.UploadText);
        Assert.False(page.IsUploading);
        Assert.Equal("Send phone songs to computer", page.UploadLabel);
    }

    [Fact]
    public async Task SendSongs_PageReopenedMidSend_StopSendingStopsIt()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), UploadGate = new TaskCompletionSource() };
        var shell = ShellWith(rig, account);
        var first = OpenAccount(shell);
        var send = first.SendSongsCommand.ExecuteAsync(null);

        // The Account page left and opened again while the send runs: it offers to stop it.
        var page = new AccountPageViewModel(shell, account);
        Assert.Equal("Stop sending", page.UploadLabel);

        var stop = page.SendSongsCommand.ExecuteAsync(null);

        Assert.True(stop.IsCompleted);                                         // stopped, not waiting on the send
        await stop;
        Assert.Equal("Stopped. Tap again to send the rest.", page.UploadText);
        account.UploadGate.SetResult();
        await send;
    }

    [Theory]
    [InlineData(0, 0, 0, 3, "Everything is already on your computer")]
    [InlineData(0, 0, 0, 0, "No songs on this phone to send")]
    [InlineData(5, 0, 0, 2, "Sent 5")]
    [InlineData(0, 4, 0, 0, "Sent 0 · 4 already on your computer")]
    [InlineData(1, 0, 2, 0, "Sent 1 · 2 failed")]
    public void SendSongs_Result_IsWorded(int sent, int already, int failed, int previously, string text)
        => Assert.Equal(text, AccountPageViewModel.DescribeUpload(new NoctisUploadResult(sent, already, failed, previously)));

    [Fact]
    public async Task SendSongs_SyncOffOnTheComputer_IsWorded()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), UploadError = new NoctisServerException(NoctisErrorKind.SyncDisabled, "50") };
        var page = OpenAccount(ShellWith(rig, account));

        await page.SendSongsCommand.ExecuteAsync(null);

        Assert.Equal("Sync is off on your computer. Turn on Sync with your phone in Settings, Account & Devices.", page.ErrorText);
        Assert.Equal(string.Empty, page.UploadText);
        Assert.False(page.IsUploading);
    }

    [Fact]
    public void Header_ShowsTheDesktopsProfileName_WithTheEmailSmallerUnderIt()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs("owner@example.com") with { DisplayName = "notheartached" } };
        var shell = ShellWith(rig, account);
        var settings = new SettingsPageViewModel(shell);
        var page = OpenAccount(shell);

        Assert.Equal("notheartached", page.DisplayName);
        Assert.Equal("owner@example.com", page.EmailText);
        Assert.True(page.HasEmailText);
        Assert.Equal("NO", page.Initials);
        Assert.Equal("Signed in as notheartached · Studio PC", settings.AccountRowText);

        // No profile name (an older desktop, or none set): the email is the name, shown once.
        account.Account = account.Account! with { DisplayName = null };
        account.RaiseStateChanged();
        Assert.Equal("owner@example.com", page.DisplayName);
        Assert.False(page.HasEmailText);
        Assert.Equal("OW", page.Initials);                                     // the address part: "OC" took the C of ".com"
        settings.OnClosed();
    }

    [AvaloniaFact]
    public void Header_View_ShowsTheNameLarge_AndTheEmailLine_AndTheSendRow()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs("owner@example.com") with { DisplayName = "notheartached" } };
        var shell = ShellWith(rig, account);
        var window = MobileFixtures.Mount(shell, out var view);
        shell.OpenAccountCommand.Execute(null);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AccountPage>(view);
        var name = MobileFixtures.Named<TextBlock>(page, "AccountNameLabel");
        var email = MobileFixtures.Named<TextBlock>(page, "EmailLabel");
        Assert.Equal("notheartached", name.Text);
        Assert.True(email.IsEffectivelyVisible);
        Assert.Equal("owner@example.com", email.Text);
        Assert.True(email.FontSize < name.FontSize);

        var row = MobileFixtures.Named<Button>(page, "SendSongsButton");
        Assert.True(row.IsEffectivelyVisible);
        Assert.True(row.IsEnabled);
        Assert.False(MobileFixtures.Named<Avalonia.Controls.Control>(page, "StopSendingIcon").IsVisible);   // icon only: the send arrow
        Assert.Equal("Send phone songs to computer", Avalonia.Automation.AutomationProperties.GetName(row));
        Assert.False(MobileFixtures.Named<TextBlock>(page, "UploadLabel").IsEffectivelyVisible);   // no status yet

        account.UploadGate = new TaskCompletionSource();
        row.Command!.Execute(null);
        window.UpdateLayout();
        Assert.True(MobileFixtures.Named<Avalonia.Controls.Control>(page, "StopSendingIcon").IsVisible);
        Assert.Equal("Stop sending", Avalonia.Automation.AutomationProperties.GetName(row));
        var status = MobileFixtures.Named<TextBlock>(page, "UploadLabel");
        Assert.True(status.IsEffectivelyVisible);
        Assert.Equal("Sending 3 of 40", status.Text);
        account.UploadGate.SetResult();
        window.Close();
    }

    /// <summary>With nothing downloaded, Remove downloads asked "Remove every downloaded song?"
    /// and then removed nothing: it is off until something is downloaded.</summary>
    [Fact]
    public async Task RemoveDownloads_IsOff_WithNothingDownloaded()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.False(page.CanRemoveDownloads);
        await page.DownloadAllCommand.ExecuteAsync(null);
        Assert.True(page.CanRemoveDownloads);
        page.AskRemoveDownloadsCommand.Execute(null);
        Assert.False(page.CanRemoveDownloads);                             // its question is open
        Assert.StartsWith("Downloads finished", page.DownloadText);
        await page.ConfirmRemoveDownloadsCommand.ExecuteAsync(null);
        Assert.False(page.CanRemoveDownloads);                             // nothing left
        Assert.Equal(string.Empty, page.DownloadText);                     // not "Downloads finished" over none
    }

    /// <summary>Settings' profile card said "Synced 5 min ago" for as long as the sheet stayed
    /// open: the page's minute ticker re-reads it (RefreshSyncTime) against the clock.</summary>
    [Fact]
    public async Task SettingsProfileCard_SyncTime_RefreshesWhileOpen()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        await account.SignInAsync("https://192.168.1.20:4747", "John Doe", "pw", Fingerprint);
        var now = DateTime.UtcNow;
        account.Account = account.Account! with { LastSyncUtc = now.AddMinutes(-5) };
        var clock = now;
        var settings = new SettingsPageViewModel(ShellWith(rig, account)) { UtcNow = () => clock };
        Assert.Equal("Signed in as John Doe · Studio PC, Synced 5 min ago", settings.AccountRowName);

        var raised = false;
        settings.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(SettingsPageViewModel.AccountRowName);
        clock = now.AddMinutes(2);
        settings.RefreshSyncTime();
        Assert.True(raised);
        Assert.Equal("Signed in as John Doe · Studio PC, Synced 7 min ago", settings.AccountRowName);
    }

    /// <summary>The Account page and Settings' profile card say the last sync in the same words
    /// (they read "Synced 3 h ago" and "synced 3 hr ago", "on 10/9/2026 2:05 PM" and "synced 2 Oct").</summary>
    [Fact]
    public async Task LastSync_IsWordedAlike_OnTheAccountPageAndTheProfileCard()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        await account.SignInAsync("https://192.168.1.20:4747", "John Doe", "pw", Fingerprint);
        var shell = ShellWith(rig, account);
        var now = DateTime.UtcNow;
        var settings = new SettingsPageViewModel(shell) { UtcNow = () => now };
        var page = OpenAccount(shell);
        Assert.Equal("Not synced yet", page.LastSyncText);
        Assert.Equal("Studio PC · Not synced yet", settings.AccountSubtitle);

        account.Account = account.Account! with { LastSyncUtc = now.AddMinutes(-5) };
        account.RaiseStateChanged();
        Assert.Equal("Synced 5 min ago", page.LastSyncText);
        Assert.Equal($"Signed in as John Doe · Studio PC, {page.LastSyncText}", settings.AccountRowName);
    }

    [Fact]
    public void LastSync_ReadsSyncedMinutesAgo_AndSyncProgressFillsTheBar()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() with { LastSyncUtc = DateTime.UtcNow.AddMinutes(-5) } };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.Equal("Synced 5 min ago", page.LastSyncText);
        Assert.Equal("Sign out of Studio PC?", page.SignOutQuestion);

        account.RaiseSyncProgress(new NoctisSyncProgress(NoctisSyncStage.Covers, 25, 100));
        Assert.True(page.IsSyncing);
        Assert.True(page.IsSyncCounted);
        Assert.Equal(0.25, page.SyncFraction, 3);
        account.RaiseSyncProgress(new NoctisSyncProgress(NoctisSyncStage.Done, 100, 100));
        Assert.False(page.IsSyncCounted);
    }

    [Fact]
    public async Task RemoveAllDownloads_AsksFirst()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.DownloadedIds.Add(Guid.NewGuid());
        var page = OpenAccount(ShellWith(rig, account));

        page.AskRemoveDownloadsCommand.Execute(null);
        Assert.True(page.IsConfirmingRemoveDownloads);
        page.CancelRemoveDownloadsCommand.Execute(null);
        Assert.Equal(0, account.RemoveAllCalls);

        page.AskRemoveDownloadsCommand.Execute(null);
        await page.ConfirmRemoveDownloadsCommand.ExecuteAsync(null);
        Assert.Equal(1, account.RemoveAllCalls);
        Assert.False(page.IsConfirmingRemoveDownloads);
        Assert.Equal("Downloaded: 0 songs · 0 MB", page.StorageText);
    }

    [Fact]
    public async Task SignOut_AsksFirst_AndCanAlsoRemoveDownloads()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));

        page.AskSignOutCommand.Execute(null);
        Assert.True(page.IsConfirmingSignOut);
        page.CancelSignOutCommand.Execute(null);
        Assert.Empty(account.SignOuts);

        page.AskSignOutCommand.Execute(null);
        Assert.False(page.AlsoRemoveDownloads);                            // off unless ticked
        page.AlsoRemoveDownloads = true;
        await page.ConfirmSignOutCommand.ExecuteAsync(null);

        Assert.Equal(new[] { true }, account.SignOuts);
        Assert.False(page.IsSignedIn);
        Assert.True(page.IsFormVisible);
        Assert.False(page.IsConfirmingSignOut);
    }

    /// <summary>Sign-out's final download report (nothing running) was kept as the page's last
    /// download, so signing back in on the same page showed "Downloads finished" under
    /// Download everything with nothing downloaded.</summary>
    [Fact]
    public async Task SignOutThenIn_ShowsNoStaleDownloadsFinished()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var page = OpenAccount(ShellWith(rig, account));

        page.AskSignOutCommand.Execute(null);
        await page.ConfirmSignOutCommand.ExecuteAsync(null);
        await account.SignInAsync("https://192.168.1.20:4747", "owner", "pw", Fingerprint);

        Assert.True(page.IsSignedIn);
        Assert.Equal(string.Empty, page.DownloadText);
    }

    [Fact]
    public void StateChanged_FromTheService_RefreshesThePage_AndTheSettingsRow()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        var shell = ShellWith(rig, account);
        var settings = new SettingsPageViewModel(shell);
        Assert.Equal("Sign in to your computer", settings.AccountRowText);
        var page = OpenAccount(shell);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        account.SignInAsync("https://192.168.1.20:4747", "owner", "pw", Fingerprint).GetAwaiter().GetResult();

        Assert.True(page.IsSignedIn);
        Assert.Contains(nameof(SettingsPageViewModel.AccountRowText), changed);
        Assert.Equal("Signed in as owner · Studio PC", settings.AccountRowText);

        page.OnClosed();
        settings.OnClosed();
    }

    [Fact]
    public void NoAccountService_HidesTheAccountEntry()
    {
        using var rig = MobileFixtures.MakeRig();
        Assert.False(rig.Shell.HasAccount);
        rig.Shell.OpenAccountCommand.Execute(null);
        Assert.Null(rig.Shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task SettingsAccountRow_OpensTheAccountPage_WithTheSignInForm()
    {
        using var rig = MobileFixtures.MakeRig();
        var shell = ShellWith(rig, new FakeAccount());
        var window = MobileFixtures.Mount(shell, out var view);
        shell.OpenSettingsCommand.Execute(null);
        await ((SettingsPageViewModel)shell.CurrentPage!).Loaded;
        window.UpdateLayout();

        var settings = MobileFixtures.Find<SettingsPage>(view);
        var row = MobileFixtures.Named<Button>(settings, "AccountRow");
        Assert.True(row.IsEffectivelyVisible);
        row.Command!.Execute(row.CommandParameter);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AccountPage>(view);
        Assert.True(MobileFixtures.Named<StackPanel>(page, "SignInForm").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(page, "SignedInPanel").IsEffectivelyVisible);
        Assert.Equal('•', MobileFixtures.Named<TextBox>(page, "PasswordBox").PasswordChar);
        // It only goes back to Settings: the screen reader said "Close", as if it left the sheet.
        Assert.Equal("Back to Settings", Avalonia.Automation.AutomationProperties.GetName(MobileFixtures.Named<Button>(page, "CloseButton")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsWithoutAccountService_HasNoAccountRow()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSettingsCommand.Execute(null);
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).Loaded;
        window.UpdateLayout();

        Assert.False(MobileFixtures.Named<Button>(MobileFixtures.Find<SettingsPage>(view), "AccountRow").IsEffectivelyVisible);
        window.Close();
    }

    // ── The long-press sheet ──

    [Fact]
    public void TrackSheet_OffersDownload_OnlyForADesktopSong()
    {
        var remote = RemoteSong("Remote");
        var local = MobileFixtures.Song("Local");
        using var rig = MobileFixtures.MakeRig(new[] { remote, local });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);

        shell.OpenTrackSheetCommand.Execute(local);
        Assert.False(shell.Sheet!.CanDownload);
        Assert.False(shell.Sheet.CanRemoveDownload);

        shell.OpenTrackSheetCommand.Execute(remote);
        Assert.True(shell.Sheet!.CanDownload);
        Assert.False(shell.Sheet.CanRemoveDownload);
        Assert.Equal("Download", shell.Sheet.DownloadLabel);

        account.DownloadedIds.Add(remote.Id);
        shell.OpenTrackSheetCommand.Execute(remote);
        Assert.False(shell.Sheet!.CanDownload);
        Assert.True(shell.Sheet.CanRemoveDownload);
        Assert.Equal("Remove download", shell.Sheet.RemoveDownloadLabel);

        // Without an account service nothing is offered.
        rig.Shell.OpenTrackSheetCommand.Execute(remote);
        Assert.False(rig.Shell.Sheet!.CanDownload);
        Assert.False(rig.Shell.Sheet.CanRemoveDownload);
    }

    /// <summary>Library → Downloaded (and the Downloaded filter) re-read only on a library refresh,
    /// and a download or Remove Download raises only the account's StateChanged: the removed song
    /// stayed listed (and then streamed) until the page was reopened.</summary>
    [Fact]
    public async Task DownloadedPage_FollowsADownloadAndARemoval()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.DownloadedIds.Add(a.Id);
        var shell = ShellWith(rig, account);
        shell.LibraryRows.ActivateCommand.Execute(shell.LibraryRows.Rows.Single(r => r.Kind == LibraryRowKind.Downloaded));
        var page = Assert.IsType<SongListPageViewModel>(shell.CurrentPage);
        Assert.Equal(new[] { "A" }, page.Songs.Select(t => t.Title));

        await shell.RemoveDownloadsAsync(new[] { a });
        account.RaiseStateChanged();
        Assert.Empty(page.Songs);

        await shell.DownloadTracksAsync(new[] { b });
        account.RaiseStateChanged();
        Assert.Equal(new[] { "B" }, page.Songs.Select(t => t.Title));
    }

    [Fact]
    public void TrackSheet_SignedOut_OffersNoDownload()
    {
        var remote = RemoteSong("Remote");
        using var rig = MobileFixtures.MakeRig(new[] { remote });
        var shell = ShellWith(rig, new FakeAccount());
        shell.OpenTrackSheetCommand.Execute(remote);
        Assert.False(shell.Sheet!.CanDownload);
    }

    [Fact]
    public async Task AlbumSheet_DownloadsOnlyItsMissingDesktopSongs_AndRemovesOnlyDownloadedOnes()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var local = MobileFixtures.Song("Local");
        var album = MobileFixtures.MakeAlbum("Mixed", "Artist", a, b, local);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, local }, new[] { album });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.DownloadedIds.Add(a.Id);
        var shell = ShellWith(rig, account);

        shell.OpenAlbumSheetCommand.Execute(album);
        var sheet = shell.Sheet!;
        Assert.True(sheet.CanDownload);
        Assert.True(sheet.CanRemoveDownload);
        Assert.Equal("Download album", sheet.DownloadLabel);
        Assert.Equal("Remove downloads", sheet.RemoveDownloadLabel);

        await sheet.DownloadCommand.ExecuteAsync(null);
        Assert.False(shell.IsSheetOpen);
        Assert.Equal(new[] { b }, Assert.Single(account.DownloadCalls));

        shell.OpenAlbumSheetCommand.Execute(album);
        Assert.False(shell.Sheet!.CanDownload);                            // all of its desktop songs are on the phone
        await shell.Sheet.RemoveDownloadCommand.ExecuteAsync(null);
        Assert.Equal(new[] { a, b }, Assert.Single(account.RemoveCalls));
    }

    /// <summary>
    /// Remove Downloads on an album whose download is still running: the sheet handed over only
    /// the songs already downloaded, so the batch went on to fetch the rest of the album. It hands
    /// over every desktop song of the album; the service removes the copies and withdraws the rest.
    /// </summary>
    [Fact]
    public async Task AlbumSheet_RemoveDownloadsMidDownload_HandsOverTheSongsStillToCome()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var local = MobileFixtures.Song("Local");
        var album = MobileFixtures.MakeAlbum("Half", "Artist", a, b, local);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, local }, new[] { album });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.DownloadedIds.Add(a.Id);   // b is still on its way
        var shell = ShellWith(rig, account);

        shell.OpenAlbumSheetCommand.Execute(album);
        await shell.Sheet!.RemoveDownloadCommand.ExecuteAsync(null);

        Assert.Equal(new[] { a, b }, Assert.Single(account.RemoveCalls));
    }

    [Fact]
    public void PlaylistSheet_LabelsTheDownload()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var playlist = new Playlist { Name = "Mix", TrackIds = { a.Id } };
        var shell = ShellWith(rig, new FakeAccount { Account = FakeAccount.SignedInAs() });
        shell.OpenPlaylistSheetCommand.Execute(playlist);
        Assert.True(shell.Sheet!.CanDownload);
        Assert.Equal("Download playlist", shell.Sheet.DownloadLabel);
    }

    // ── Plays and startup ──

    [Fact]
    public void PlayRecorded_QueuesTheDesktopPlay_ForDesktopSongsOnly()
    {
        var remote = RemoteSong("Remote");
        var local = MobileFixtures.Song("Local");
        using var rig = MobileFixtures.MakeRig(new[] { remote, local });
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);

        shell.Player.PlayTracks(new[] { local }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        Assert.Empty(account.Plays);

        var before = DateTime.UtcNow;
        shell.Player.PlayTracks(new[] { remote }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        var play = Assert.Single(account.Plays);
        Assert.Same(remote, play.Track);
        Assert.InRange(play.Utc, before, DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, play.Utc.Kind);
    }

    [Fact]
    public async Task Startup_SignedIn_SyncsOnce_AndAFailureIsSwallowed()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount
        {
            Account = FakeAccount.SignedInAs(),
            SyncError = new NoctisServerException(NoctisErrorKind.Unreachable, "off"),
        };
        var shell = ShellWith(rig, account);

        await shell.InitializeAsync();
        await shell.StartupSync;                                           // does not throw

        Assert.Equal(1, account.Syncs);
    }

    [Fact]
    public async Task Startup_SignedOut_DoesNotSync()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        var shell = ShellWith(rig, account);

        await shell.InitializeAsync();
        await shell.StartupSync;

        Assert.Equal(0, account.Syncs);
    }

    // ── Stream key scope (Media3AudioPlayer's resolver) ──

    [Theory]
    [InlineData("https://192.168.1.20:4747/rest/stream?id=tr-0a&c=NoctisAndroid&v=1.16.1", true)]
    [InlineData("https://192.168.1.20:4747/rest/stream.view?id=tr-0a", true)]
    [InlineData("HTTPS://192.168.1.20:4747/rest/stream?id=tr-0a", true)]
    [InlineData("http://192.168.1.20:4747/rest/stream?id=tr-0a", false)]              // cleartext
    [InlineData("https://192.168.1.21:4747/rest/stream?id=tr-0a", false)]             // another host
    [InlineData("https://192.168.1.20:4748/rest/stream?id=tr-0a", false)]             // another port
    [InlineData("https://192.168.1.20:4747/rest/deletePlaylist?id=pl-1", false)]      // another endpoint
    [InlineData("https://192.168.1.20:4747/rest/stream/../deletePlaylist?id=pl-1", false)]
    [InlineData("https://x@192.168.1.20:4747/rest/stream?id=tr-0a", false)]
    [InlineData("https://evil.example/rest/stream?id=tr-0a", false)]
    [InlineData("file:///data/offline/0a.flac", false)]
    [InlineData("content://media/external/audio/1", false)]
    [InlineData(null, false)]
    public void StreamKey_GoesOnlyToTheDesktopsStreamEndpoint(string? request, bool allowed)
    {
        Assert.Equal(allowed, StreamAuthScope.Allows(request, "https://192.168.1.20:4747"));
        Assert.False(StreamAuthScope.Allows(request, null));
    }

    [Theory]
    // Platform clients (lock screen, Bluetooth, SystemUI; Android gates who reaches them): always.
    [InlineData(MediaControllerPolicy.PlatformControllerVersion, 10123, false, false, true)]
    // This app (Media3's notification controller): yes.
    [InlineData(7, 10050, false, false, true)]
    // Trusted (system, MEDIA_CONTENT_CONTROL, enabled notification listener): yes.
    [InlineData(7, 10123, true, false, true)]
    // Android Auto / car controllers Media3 recognizes by verified package: yes.
    [InlineData(7, 10123, false, true, true)]
    // Any other installed app binding to the exported service: no.
    [InlineData(7, 10123, false, false, false)]
    public void MediaSession_ConnectsOnlyTheSystemAndThisApp(int version, int uid, bool trusted, bool knownSystem, bool allowed) =>
        Assert.Equal(allowed, MediaControllerPolicy.Allows(version, uid, ownUid: 10050, trusted, knownSystem));

    // ── Logs ──

    [Theory]
    [InlineData("GET /rest/stream?id=tr-1&apiKey=nk_AbC-123_x&c=NoctisAndroid", "GET /rest/stream?id=tr-1&apiKey=***&c=NoctisAndroid")]
    [InlineData("apikey=abcdef0123 failed", "apikey=*** failed")]
    [InlineData("header X-Noctis-Key: s3cr3t sent", "header X-Noctis-Key: *** sent")]
    [InlineData("{\"X-Noctis-Key\":\"s3cr3t\"}", "{\"X-Noctis-Key\":\"***\"}")]
    [InlineData("key nk_Zz09-_ab issued", "key nk_*** issued")]
    [InlineData("noctisSignIn?u=owner&p=hunter2&f=json", "noctisSignIn?u=owner&p=***&f=json")]
    [InlineData("?p=enc:68756e74657232", "?p=***")]
    [InlineData("password enc:68756E746572 in body", "password enc:*** in body")]
    [InlineData("Playback error: Source error — track: Keep=1&up=2", "Playback error: Source error — track: Keep=1&up=2")]
    public void LogRedactor_MasksKeysAndPasswords(string line, string expected)
    {
        Assert.Equal(expected, LogRedactor.Redact(line));
        Assert.Equal(expected, LogRedactor.Redact(LogRedactor.Redact(line)));   // idempotent
    }

    [Fact]
    public async Task ExportLogs_IsRedacted()
    {
        using var rig = MobileFixtures.MakeRig();
        var exporter = new CapturingExporter();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Logs = exporter };
        shell.OpenSettingsCommand.Execute(null);
        var page = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
        await page.Loaded;
        DebugLog.Write("Test", "redact-marker-91c2 apiKey=nk_leakme123");

        await page.ExportLogsCommand.ExecuteAsync(null);

        Assert.Contains("redact-marker-91c2 apiKey=***", exporter.Text);
        Assert.DoesNotContain("nk_leakme123", exporter.Text);
    }

    // ── The desktop's profile picture: Account header, ProfileButton ──

    /// <summary>A real 64×64 PNG in a fresh temp folder (the caller deletes the folder).</summary>
    private static string MakePicture(out string dir)
    {
        dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noctis-avatar-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "avatar-0123456789abcdef.img");
        using var bmp = new SkiaSharp.SKBitmap(64, 64);
        bmp.Erase(new SkiaSharp.SKColor(40, 120, 200));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var png = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        System.IO.File.WriteAllBytes(path, png.ToArray());
        return path;
    }

    [Theory]
    [InlineData("owner", "OW")]
    [InlineData("John Doe", "JD")]
    [InlineData("jane.smith", "JS")]
    [InlineData("x", "X")]
    [InlineData("élodie", "ÉL")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    // An email shown as the name: its address part only ("tester@example.com" read "TC", the C of ".com").
    [InlineData("tester@example.com", "TE")]
    [InlineData("jane.smith@example.com", "JS")]
    public void Initials_ForTheAvatarFallback(string? name, string expected)
        => Assert.Equal(expected, AccountPageViewModel.InitialsOf(name));

    [Fact]
    public void ShellProfileAvatarPath_FollowsTheAccount()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), AvatarPath = "/data/account/avatar-1.img" };
        var shell = ShellWith(rig, account);
        Assert.Equal("/data/account/avatar-1.img", shell.ProfileAvatarPath);   // from the first frame

        account.AvatarPath = "/data/account/avatar-2.img";                     // a sync brought a new picture
        account.RaiseStateChanged();
        Assert.Equal("/data/account/avatar-2.img", shell.ProfileAvatarPath);

        account.Account = null;                                                // signed out: the glyph again
        account.RaiseStateChanged();
        Assert.Null(shell.ProfileAvatarPath);

        Assert.Null(ShellWith(rig, null).ProfileAvatarPath);                   // no account service at all
    }

    [Fact]
    public async Task AccountPage_Header_ShowsTheAvatar_ElseInitials()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs("owner", "Studio PC") };
        var page = OpenAccount(ShellWith(rig, account));
        Assert.Equal("owner", page.DisplayName);
        Assert.Equal("on Studio PC", page.ServerText);
        Assert.Equal("0 songs · 0 MB", page.StorageValue);
        Assert.False(page.HasAvatar);
        Assert.True(page.ShowInitials);
        Assert.Equal("OW", page.Initials);

        account.AvatarPath = "/data/account/avatar-1.img";
        account.RaiseStateChanged();
        Assert.True(page.HasAvatar);
        Assert.Equal("/data/account/avatar-1.img", page.AvatarPath);
        Assert.False(page.ShowInitials);
        Assert.False(page.ShowPersonGlyph);

        await account.SignOutAsync(removeDownloads: false);
        Assert.False(page.IsSignedIn);
        Assert.Null(page.AvatarPath);
    }

    [AvaloniaFact]
    public void AccountPage_SignedIn_ShowsTheHeaderPicture_AndTheRows()
    {
        var picture = MakePicture(out var dir);
        try
        {
            using var rig = MobileFixtures.MakeRig();
            var account = new FakeAccount { Account = FakeAccount.SignedInAs(), AvatarPath = picture };
            var shell = ShellWith(rig, account);
            var window = MobileFixtures.Mount(shell, out var view);
            shell.OpenAccountCommand.Execute(null);
            window.UpdateLayout();

            var page = MobileFixtures.Find<AccountPage>(view);
            Assert.True(MobileFixtures.Named<StackPanel>(page, "SignedInPanel").IsEffectivelyVisible);
            var image = MobileFixtures.Named<Noctis.Controls.CachedImage>(page, "AvatarImage");
            Assert.True(image.IsEffectivelyVisible);
            Assert.Equal(picture, image.SourcePath);
            Assert.False(MobileFixtures.Named<TextBlock>(page, "AvatarInitials").IsEffectivelyVisible);
            var circle = MobileFixtures.Named<Border>(page, "AvatarCircle");
            Assert.Equal(56, circle.Bounds.Width);
            Assert.True(circle.ClipToBounds);

            // Every action (row or tile) is a touch target of at least 52 dp.
            foreach (var name in new[] { "SyncNowButton", "DownloadAllButton", "SendSongsButton", "RemoveDownloadsButton", "SignOutButton" })
            {
                var row = MobileFixtures.Named<Button>(page, name);
                Assert.True(row.IsEffectivelyVisible, name);
                Assert.True(row.Bounds.Height >= 52, $"{name} is {row.Bounds.Height} dp");
                Assert.NotNull(row.Command);
            }
            Assert.Equal("0 songs · 0 MB", MobileFixtures.Named<TextBlock>(page, "StorageLabel").Text);

            // Remove all downloads still asks first, on the glass prompt over the sheet (owner 10-20).
            MobileFixtures.Named<Button>(page, "RemoveDownloadsButton").Command!.Execute(null);
            window.UpdateLayout();
            var prompt = window.GetVisualDescendants().OfType<Noctis.Mobile.Views.PromptSheet>().Single();
            Assert.True(prompt.IsVisible);
            Assert.Equal("Remove all downloads?", MobileFixtures.Named<TextBlock>(prompt, "PromptTitle").Text);
            MobileFixtures.Named<Button>(prompt, "PromptCancel").Command!.Execute(null);

            // No picture: the initials on the accent circle.
            account.AvatarPath = null;
            account.RaiseStateChanged();
            window.UpdateLayout();
            Assert.False(image.IsEffectivelyVisible);
            Assert.True(MobileFixtures.Named<TextBlock>(page, "AvatarInitials").IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void ProfileButton_ShowsTheAvatar_WhenSet_ElseTheGlyph_AndOpensSettings()
    {
        var picture = MakePicture(out var dir);
        try
        {
            using var rig = MobileFixtures.MakeRig();
            var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
            var shell = ShellWith(rig, account);
            var button = new ProfileButton { DataContext = shell };
            button.Bind(ProfileButton.AvatarPathProperty, new Avalonia.Data.Binding(nameof(ShellViewModel.ProfileAvatarPath)));
            button.Bind(ProfileButton.CommandProperty, new Avalonia.Data.Binding(nameof(ShellViewModel.OpenSettingsCommand)));
            var window = new Window { Width = 200, Height = 200, Content = button };
            window.Show();
            window.UpdateLayout();

            var image = MobileFixtures.Named<Noctis.Controls.CachedImage>(button, "AvatarImage");
            var glyph = MobileFixtures.Named<PathIcon>(button, "AvatarGlyph");
            Assert.False(button.HasAvatar);
            Assert.True(glyph.IsEffectivelyVisible);
            Assert.False(image.IsEffectivelyVisible);

            account.AvatarPath = picture;
            account.RaiseStateChanged();
            window.UpdateLayout();
            Assert.Equal(picture, shell.ProfileAvatarPath);
            Assert.True(button.HasAvatar);
            Assert.True(image.IsEffectivelyVisible);
            Assert.Equal(picture, image.SourcePath);
            Assert.False(glyph.IsEffectivelyVisible);
            var clip = MobileFixtures.Named<Border>(button, "AvatarClip");
            Assert.True(clip.ClipToBounds);
            Assert.Equal(clip.Bounds.Width / 2, clip.CornerRadius.TopLeft);   // a circle

            // A tap opens Settings.
            var root = MobileFixtures.Named<Button>(button, "ProfileButtonRoot");
            root.Command!.Execute(root.CommandParameter);
            Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);

            // Signed out: the glyph again.
            account.Account = null;
            account.RaiseStateChanged();
            window.UpdateLayout();
            Assert.Null(shell.ProfileAvatarPath);
            Assert.True(glyph.IsEffectivelyVisible);
            Assert.False(image.IsEffectivelyVisible);
            Assert.Null(image.SourcePath);
            window.Close();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    private sealed class CapturingExporter : ILogExporter
    {
        public string Text { get; private set; } = string.Empty;
        public Task<bool> ExportAsync(string suggestedFileName, string text)
        {
            Text = text;
            return Task.FromResult(true);
        }
    }

    // ── The glass prompt (owner 10-20: confirm prompts are pop-ups, not cards in the page) ──

    /// <summary>Sign out asks on the shell's glass prompt: the question, one line, Sign Out in red,
    /// and only with downloads on the phone a second red Sign Out and Remove Downloads (the switch
    /// row offered it with nothing to remove). That second action removes them.</summary>
    [Fact]
    public async Task SignOut_AsksOnTheGlassPrompt_AndOffersRemovingDownloadsOnlyWithSome()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        var page = OpenAccount(shell);

        page.AskSignOutCommand.Execute(null);
        var prompt = Assert.IsType<PromptViewModel>(shell.Prompt);
        Assert.Equal("Sign out of Studio PC?", prompt.Title);
        Assert.Equal("Its songs leave this phone's library. You can sign in again any time.", prompt.Message);
        var only = Assert.Single(prompt.Actions);
        Assert.Equal("Sign Out", only.Label);
        Assert.True(only.IsDestructive);
        prompt.CancelCommand.Execute(null);
        Assert.False(shell.IsPromptOpen);
        Assert.False(page.IsConfirmingSignOut);

        account.DownloadedIds.Add(Guid.NewGuid());
        account.RaiseStateChanged();
        page.AskSignOutCommand.Execute(null);
        Assert.Equal(new[] { "Sign Out", "Sign Out and Remove Downloads" }, shell.Prompt!.Actions.Select(a => a.Label));
        await shell.Prompt.Actions[1].RunCommand.ExecuteAsync(null);
        Assert.False(shell.IsPromptOpen);
        Assert.Equal(new[] { true }, account.SignOuts);
    }

    /// <summary>Back, a tap off the card or Cancel: the question goes and the page forgets it
    /// (the Remove downloads tile, greyed while asked, is live again); nothing is removed.</summary>
    [Fact]
    public void RemoveDownloads_BackCancelsTheGlassPrompt()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        account.DownloadedIds.Add(Guid.NewGuid());
        var shell = ShellWith(rig, account);
        var page = OpenAccount(shell);

        page.AskRemoveDownloadsCommand.Execute(null);
        Assert.Equal("Remove all downloads?", shell.Prompt!.Title);
        Assert.Equal("Remove Downloads", Assert.Single(shell.Prompt.Actions).Label);
        Assert.False(page.CanRemoveDownloads);

        Assert.True(shell.TryHandleBack());
        Assert.False(shell.IsPromptOpen);
        Assert.False(page.IsConfirmingRemoveDownloads);
        Assert.True(page.CanRemoveDownloads);
        Assert.Equal(0, account.RemoveAllCalls);
        Assert.IsType<AccountPageViewModel>(shell.CurrentPage);   // Back took the question, not the page
    }

    /// <summary>The sheet dropped another way than Back (DismissSheet closes pages, not prompts):
    /// a question asked by a page it closed goes with it, and nothing is removed.</summary>
    [Fact]
    public async Task DismissingTheSheet_TakesItsPagesQuestionsDown()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        shell.OpenSettingsCommand.Execute(null);
        await ((SettingsPageViewModel)shell.CurrentPage!).Loaded;
        var page = OpenAccount(shell);
        page.AskSignOutCommand.Execute(null);
        Assert.True(shell.IsPromptOpen);

        Assert.True(shell.DismissSheet());
        Assert.False(shell.IsPromptOpen);
        Assert.False(page.IsConfirmingSignOut);
        Assert.Empty(account.SignOuts);
    }
}
