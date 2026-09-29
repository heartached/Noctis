using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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

        public event EventHandler? StateChanged;
        public event EventHandler<NoctisSyncProgress>? SyncProgress;
        public event EventHandler<NoctisDownloadProgress>? DownloadProgress;

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

        public Task<string> ProbeFingerprintAsync(string serverUrl, CancellationToken ct = default)
        {
            Probes.Add(serverUrl);
            return ProbeError != null ? Task.FromException<string>(ProbeError) : Task.FromResult(Fingerprint);
        }

        public Task SignInAsync(string serverUrl, string userName, string password, string confirmedFingerprint, CancellationToken ct = default)
        {
            SignIns.Add((serverUrl, userName, password, confirmedFingerprint));
            if (SignInError != null) return Task.FromException(SignInError);
            Account = SignedInAs(userName) with { ServerUrl = serverUrl, Fingerprint = confirmedFingerprint };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SignOutAsync(bool removeDownloads, CancellationToken ct = default)
        {
            SignOuts.Add(removeDownloads);
            Account = null;
            if (removeDownloads) DownloadedIds.Clear();
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
    public void Fingerprint_IsShownWhole_EightBytesALine()
    {
        var shown = AccountPageViewModel.FormatFingerprint(Fingerprint);
        var lines = shown.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.Equal(8, l.Split(':').Length));
        Assert.Equal(Fingerprint, string.Join(":", lines));
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
        Assert.Equal(Fingerprint, page.FingerprintDisplay.Replace("\n", ":"));
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
        Assert.StartsWith("Last synced", page.LastSyncText);
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
    [InlineData(NoctisErrorKind.Unreachable, "Can't reach your computer. Check it's on, Noctis is open, Pair a device is on, and both are on the same Wi-Fi.")]
    [InlineData(NoctisErrorKind.CertificateChanged, "Your computer's certificate changed while signing in. Try again and check the fingerprint.")]
    [InlineData(NoctisErrorKind.BadCredentials, "Wrong account name or password.")]
    [InlineData(NoctisErrorKind.LockedOut, "Too many failed sign-ins. Wait a few minutes, then try again.")]
    [InlineData(NoctisErrorKind.SyncDisabled, "Turn on Sync on your computer (Settings → Account & Devices).")]
    [InlineData(NoctisErrorKind.SignedOut, "This phone was removed on your computer. Sign in again.")]
    [InlineData(NoctisErrorKind.InvalidAddress, "Enter your computer's address as shown in Pair a device, like 192.168.1.20 or https://192.168.1.20:4747.")]
    [InlineData(NoctisErrorKind.Server, "Your computer couldn't finish that. Try again in a moment.")]
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

        Assert.Equal("Your computer's certificate changed. Sign out and sign in again to trust the new one.", page.ErrorText);
        Assert.True(page.IsCertificateChanged);
        Assert.False(page.CanUseServer);                                   // Sync and Download hidden
        Assert.True(page.IsSignedIn);                                      // Sign out stays
        Assert.False(page.IsSyncing);
    }

    [Fact]
    public async Task SyncNow_SyncDisabled_IsWorded()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), SyncError = new NoctisServerException(NoctisErrorKind.SyncDisabled, "50") };
        var page = OpenAccount(ShellWith(rig, account));

        await page.SyncNowCommand.ExecuteAsync(null);

        Assert.Equal("Turn on Sync on your computer (Settings → Account & Devices).", page.ErrorText);
        Assert.True(page.CanUseServer);
        Assert.Equal(string.Empty, page.SyncText);
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

    [Fact]
    public void StateChanged_FromTheService_RefreshesThePage_AndTheSettingsRow()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount();
        var shell = ShellWith(rig, account);
        var settings = new SettingsPageViewModel(shell);
        Assert.Equal("Sign in to your Noctis desktop", settings.AccountRowText);
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

    private sealed class CapturingExporter : ILogExporter
    {
        public string Text { get; private set; } = string.Empty;
        public Task<bool> ExportAsync(string suggestedFileName, string text)
        {
            Text = text;
            return Task.FromResult(true);
        }
    }
}
