using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Account &amp; Devices page after the 10-11 reorganisation: one phone list that joins the
/// server's device keys with the sync ledger, a "Last synced" line, the phone card's switch as
/// the real Noctis Server switch, sync no longer forced on by Connect a phone, no API key made
/// behind the user's back, and Delete account asking first.
/// </summary>
public class AccountDevicesPageTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static readonly DateTime Now = new(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PhoneList_JoinsSignInKeysWithTheLedger()
    {
        var keys = new[]
        {
            new ServerDevice("me@example.com", "phone-a-0001", "Pixel 9", Now.AddDays(-3), Now.AddMinutes(-2)),
            new ServerDevice("me@example.com", "phone-b-0002", "Tablet", Now.AddDays(-1), null),
        };
        var ledger = new[]
        {
            new SyncDevice("PHONE-A-0001", "Pixel 9", Now.AddHours(-4), 10), // ids compare case-insensitively
            new SyncDevice("old-phone-9999", "samsung SM-S911U1", Now.AddDays(-2), 5),
        };

        var rows = SettingsViewModel.BuildPhoneDeviceRows(keys, ledger, syncOn: true, Now);

        Assert.Equal(3, rows.Count);
        Assert.Equal(("Pixel 9", "Synced 4 h ago", true), (rows[0].Name, rows[0].StatusText, rows[0].IsSignedIn));
        // Signed in, never synced: when it was last used (here: when it signed in).
        Assert.Equal(("Tablet", "Connected on " + Now.AddDays(-1).ToLocalTime().ToString("g", Noctis.Localization.Loc.Instance.Culture), true),
            (rows[1].Name, rows[1].StatusText, rows[1].IsSignedIn));
        // The ledger knows it but its key is gone: listed as signed out, no Sign out button.
        Assert.Equal(("samsung SM-S911U1", "Signed out", false), (rows[2].Name, rows[2].StatusText, rows[2].IsSignedIn));
    }

    [Fact]
    public void PhoneList_SyncOff_ShowsWhenEachPhoneWasLastUsed()
    {
        var keys = new[] { new ServerDevice("me@example.com", "phone-a-0001", "Pixel 9", Now.AddDays(-3), Now.AddSeconds(-20)) };
        var ledger = new[] { new SyncDevice("phone-a-0001", "Pixel 9", Now.AddHours(-4), 10) };

        var row = Assert.Single(SettingsViewModel.BuildPhoneDeviceRows(keys, ledger, syncOn: false, Now));
        Assert.Equal("Connected just now", row.StatusText);
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600 + 59 * 60, "3 h ago")]
    [InlineData(-90, "just now")] // a phone clock ahead of this PC
    public void Ago_ReadsShort(int secondsAgo, string expected)
        => Assert.Equal(expected, SettingsViewModel.Ago(Now.AddSeconds(-secondsAgo), Now));

    [AvaloniaFact]
    public async Task LastSyncedLine_FollowsTheLedger_AndIsEmptyWhileSyncIsOff()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { SyncEnabled = true, SyncDeviceId = "thispc000001" });
        var settings = await persistence.LoadSettingsAsync();
        var sync = new LibrarySyncService(() => settings, persistence);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService()) { SyncServiceOverride = sync };
        await vm.LoadAsync();

        vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
        Assert.Equal("Not synced yet", vm.SyncStatusText);

        await sync.GetChangesAsync(0, "phone-a-0001", "Pixel 9");
        vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
        vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
        Assert.Equal("Last synced just now", vm.SyncStatusText);
        var row = Assert.Single(vm.PhoneDevices);
        Assert.Equal(("Pixel 9", "Signed out"), (row.Name, row.StatusText)); // no device key in this test

        vm.SyncEnabled = false;
        Assert.Equal(string.Empty, vm.SyncStatusText);
    }

    [AvaloniaFact]
    public async Task ConnectAPhone_NoLongerTurnsSyncOnBehindTheUsersBack()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var port = FreePort();
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { NoctisServerEnabled = true, NoctisServerPort = port, SyncTurnedOffByUser = true });
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        try
        {
            await vm.LoadAsync();
            await ServerSettled(vm);
            Assert.False(vm.SyncEnabled);

            vm.TogglePairingCommand.Execute(null);
            Assert.True(vm.IsPairingVisible);
            Assert.False(vm.SyncEnabled);
        }
        finally { await vm.StopNoctisServerAsync(); }
    }

    [AvaloniaFact]
    public async Task PhoneSwitch_TurnsSyncOnWithIt_UnlessTheUserSwitchedSyncOff()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { NoctisServerPort = FreePort() });
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        try
        {
            await vm.LoadAsync();
            vm.NoctisServerEnabled = true;
            Assert.True(vm.SyncEnabled);

            vm.SyncEnabled = false;            // the user says no
            vm.NoctisServerEnabled = false;
            vm.NoctisServerEnabled = true;
            Assert.False(vm.SyncEnabled);      // and it stays no
            await ServerSettled(vm);
            await vm.SaveAsync();
            Assert.True((await persistence.LoadSettingsAsync()).SyncTurnedOffByUser);
        }
        finally { await vm.StopNoctisServerAsync(); }
    }

    [AvaloniaFact]
    public async Task PhoneSwitchOff_FoldsThePairingDetailsAway()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { NoctisServerEnabled = true, NoctisServerPort = FreePort() });
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        try
        {
            await vm.LoadAsync();
            await ServerSettled(vm);
            Assert.True(vm.IsPhoneCardOpen);
            vm.TogglePairingCommand.Execute(null);
            Assert.True(vm.IsPairingVisible);

            vm.NoctisServerEnabled = false;
            Assert.False(vm.IsPairingVisible);
            Assert.False(vm.IsPhoneCardOpen);
        }
        finally { await vm.StopNoctisServerAsync(); }
    }

    // 11-03 audit. HEAD: "Since November 2026" in every language (hard-coded English, OS month
    // names), and with no account the line's name was bound through a null PrimaryAccount.
    [AvaloniaFact]
    public async Task IdentityLine_IsInTheAppLanguage_AndEmptyWithoutAnAccount()
    {
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
        Assert.Equal(("", ""), (vm.PrimaryAccountName, vm.PrimaryAccountSince));

        new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db")).Create("me@example.com", "correct horse", isAdmin: true);
        try
        {
            Noctis.Localization.Loc.Instance.SetCulture("ru");
            vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
            vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
            Assert.Equal("me@example.com", vm.PrimaryAccountName);
            var ru = System.Globalization.CultureInfo.GetCultureInfo("ru");
            Assert.Equal("Создан: " + DateTime.UtcNow.ToLocalTime().ToString("MMMM yyyy", ru), vm.PrimaryAccountSince);
        }
        finally { Noctis.Localization.Loc.Instance.SetCulture("en"); }
    }

    [AvaloniaFact]
    public void SecurityCode_ShowsEightBytesALine_LikeThePhone()
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        var bytes = Enumerable.Range(0, 32).Select(i => i.ToString("X2")).ToArray();
        vm.NoctisServerFingerprint = string.Join(":", bytes);

        var lines = vm.NoctisServerFingerprintDisplay.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("00:01:02:03:04:05:06:07", lines[0]);
        Assert.Equal("18:19:1A:1B:1C:1D:1E:1F", lines[3]);
    }

    [AvaloniaFact]
    public async Task CreateAccount_DoesNotMakeAnApiKey()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.NewServerUserName = "me@example.com";
        vm.NewServerUserPassword = "correct horse";
        vm.CreatePrimaryAccountCommand.Execute(null);

        Assert.True(vm.HasPrimaryAccount);
        Assert.False(vm.HasIssuedApiKey);
        Assert.False(Assert.Single(new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db")).List()).HasApiKey);
    }

    [AvaloniaFact]
    public async Task DeleteAccount_AsksFirst()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.NewServerUserName = "me@example.com";
        vm.NewServerUserPassword = "correct horse";
        vm.CreatePrimaryAccountCommand.Execute(null);
        var asked = new List<ConfirmationRequest>();
        var answer = false;
        vm.ShowPluginDialog = r => { asked.Add(r); return Task.FromResult(new ConfirmationResult(answer, false)); };

        await vm.DeleteServerUserCommand.ExecuteAsync(vm.PrimaryAccount);
        Assert.Single(asked);
        Assert.Equal("Delete your account?", asked[0].Title);
        Assert.True(vm.HasPrimaryAccount);

        answer = true;
        await vm.DeleteServerUserCommand.ExecuteAsync(vm.PrimaryAccount);
        Assert.False(vm.HasPrimaryAccount);
    }

    [AvaloniaFact]
    public async Task PhoneSwitch_OnOffOnQuickly_EndsListening_AndOffQuicklyEndsStopped()
    {
        var port = FreePort();
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { NoctisServerPort = port });
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        try
        {
            await vm.LoadAsync();
            vm.NoctisServerEnabled = true;
            vm.NoctisServerEnabled = false;
            await ServerSettled(vm, expectRunning: false);
            // The switch reads off at once, but the first start may still be finishing on a slow
            // runner before the queued stop runs: wait for the port to close, then make sure no
            // start escaped the stop and opened it again.
            for (var waited = 0; Listening(port) && waited < 5000; waited += 100) await Task.Delay(100);
            await Task.Delay(500);
            Assert.False(Listening(port));

            vm.NoctisServerEnabled = true;
            vm.NoctisServerEnabled = false;
            vm.NoctisServerEnabled = true;
            await ServerSettled(vm);
            Assert.True(Listening(port));
        }
        finally { await vm.StopNoctisServerAsync(); }
        Assert.False(Listening(port));
    }

    /// <summary>Lets the fire-and-forget start/stop passes run (they queue on the gate).</summary>
    private static async Task ServerSettled(SettingsViewModel vm, bool expectRunning = true)
    {
        for (var i = 0; i < 200; i++)
        {
            if (vm.NoctisServerStartFailed) throw new Xunit.Sdk.XunitException("server failed: " + vm.NoctisServerError);
            if (expectRunning == vm.IsPhoneCardOpen) { await Task.Delay(50); if (expectRunning == vm.IsPhoneCardOpen) return; }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("server never settled");
    }

    private static bool Listening(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(IPAddress.Loopback, port).Wait(1000) && c.Connected;
        }
        catch { return false; }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
