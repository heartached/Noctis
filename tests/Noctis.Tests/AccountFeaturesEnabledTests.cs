using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The account and sync cards are live (<see cref="AccountFeatures"/> on, owner 10-11, to test
/// desktop-to-phone sync): the stored SyncEnabled flag is honoured, the toggle turns sync on and
/// saves it, and Create account makes the primary account in the server's user store.
/// </summary>
public class AccountFeaturesEnabledTests : IDisposable
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

    [AvaloniaFact]
    public async Task StoredSyncFlag_IsHonoured_AndTheToggleSavesIt()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { SyncEnabled = true });

        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        Assert.True(vm.AccountFeaturesEnabled);
        Assert.True(vm.SyncEnabled);

        vm.SyncEnabled = false;
        await vm.SaveAsync();
        Assert.False((await persistence.LoadSettingsAsync()).SyncEnabled);

        vm.SyncEnabled = true;
        await vm.SaveAsync();
        Assert.True((await persistence.LoadSettingsAsync()).SyncEnabled);
    }

    [AvaloniaFact]
    public async Task CreateAccount_MakesThePrimaryAccount()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();

        Assert.True(vm.CreatePrimaryAccountCommand.CanExecute(null));
        vm.NewServerUserName = "me@example.com";
        vm.NewServerUserPassword = "correct horse";
        vm.CreatePrimaryAccountCommand.Execute(null);

        Assert.True(vm.HasPrimaryAccount);
        var user = Assert.Single(new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db")).List());
        Assert.Equal("me@example.com", user.Name);
    }

    [AvaloniaFact]
    public async Task IssuedApiKey_StartsHidden_TheEyeShowsIt_AndANewKeyHidesItAgain()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.NewServerUserName = "me@example.com";
        vm.NewServerUserPassword = "correct horse";
        vm.CreatePrimaryAccountCommand.Execute(null);

        vm.RegenerateServerApiKeyCommand.Execute(vm.PrimaryAccount);
        var key = vm.IssuedApiKey;
        Assert.True(key.Length > 10);
        Assert.False(vm.IsIssuedApiKeyRevealed);
        Assert.DoesNotContain(key[3..], vm.DisplayedApiKey);           // only the prefix shows
        Assert.StartsWith(key[..3], vm.DisplayedApiKey);

        vm.ToggleIssuedApiKeyRevealCommand.Execute(null);
        Assert.Equal(key, vm.DisplayedApiKey);

        vm.RegenerateServerApiKeyCommand.Execute(vm.PrimaryAccount);
        Assert.NotEqual(key, vm.IssuedApiKey);
        Assert.False(vm.IsIssuedApiKeyRevealed);

        // Done folds the panel with the key still in it, and clears the key once it has folded.
        var hide = vm.HideIssuedApiKeyCommand.ExecuteAsync(null);
        Assert.False(vm.IsIssuedApiKeyOpen);
        Assert.True(vm.HasIssuedApiKey);
        await hide;
        Assert.False(vm.HasIssuedApiKey);
    }

    [AvaloniaFact]
    public async Task ChangePassword_TakesTheShownApiKeyAway_ItNoLongerWorks()
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.NewServerUserName = "me@example.com";
        vm.NewServerUserPassword = "correct horse";
        vm.CreatePrimaryAccountCommand.Execute(null);
        vm.RegenerateServerApiKeyCommand.Execute(vm.PrimaryAccount);
        var key = vm.IssuedApiKey;
        var users = new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db"));
        Assert.NotNull(users.ByApiKey(key));

        vm.ToggleChangePasswordCommand.Execute(null);
        vm.ChangePasswordValue = "battery staple";
        vm.ChangePrimaryPasswordCommand.Execute(null);

        Assert.Null(users.ByApiKey(key));         // ServerUserStore.ChangePassword dropped it
        Assert.False(vm.HasIssuedApiKey);
        Assert.False(vm.IsIssuedApiKeyOpen);
    }

    [AvaloniaFact]
    public async Task PasswordEye_TogglesAndClosingChangePasswordHidesItAgain()
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.TogglePasswordRevealCommand.Execute(null);
        Assert.True(vm.IsPasswordRevealed);
        vm.ToggleChangePasswordCommand.Execute(null);
        Assert.False(vm.IsPasswordRevealed);
    }

    /// <summary>
    /// Bug: the desktop's sync device id was made at load straight onto the settings object, and
    /// the first save re-based those settings on the file (which had none), so it went back to
    /// empty: the ledger stamped this computer's changes with a new random id each launch, then
    /// with the "desktop-&lt;machine&gt;" fallback (three ids for one PC in the owner's ledger, 10-11).
    /// </summary>
    [AvaloniaFact]
    public async Task SyncDeviceId_IsMadeOnce_AndSurvivesASave()
    {
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        var id = vm.GetSettings().SyncDeviceId;
        Assert.False(string.IsNullOrWhiteSpace(id));

        await vm.SaveAsync();

        Assert.Equal(id, vm.GetSettings().SyncDeviceId);                        // what the ledger stamps with
        Assert.Equal(id, (await persistence.LoadSettingsAsync()).SyncDeviceId); // what the next launch reads
    }

    /// <summary>
    /// Bug: the sync ledger keeps every phone that ever synced, and nothing ever removed one, so a
    /// phone whose key is gone (signed out on the phone, here, or by a password change) stayed
    /// listed for good: the owner's dev data kept "samsung SM-S911U1" (10-11), and a reinstall
    /// (a new device id) adds another. Its row had no way off the list.
    /// </summary>
    [AvaloniaFact]
    public async Task ASignedOutPhone_CanBeTakenOffTheList()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { SyncEnabled = true, SyncDeviceId = "thispc000001" });
        var settings = await persistence.LoadSettingsAsync();
        var sync = new Noctis.Services.Sync.LibrarySyncService(() => settings, persistence);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService()) { SyncServiceOverride = sync };
        await vm.LoadAsync();
        await sync.GetChangesAsync(0, "phone-a-0001", "samsung SM-S911U1"); // it synced; it has no key now
        vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
        var row = Assert.Single(vm.PhoneDevices);
        Assert.False(row.IsSignedIn);

        vm.RemoveSignedInDeviceCommand.Execute(row);

        Assert.Empty(vm.PhoneDevices);
        Assert.Empty(sync.Devices());
    }

    [Fact]
    public void CreateAccount_ShortPasswordOrNoEmail_SaysWhatToFix()
    {
        var store = new ServerUserStore(Path.Combine(_root, "users.db"));
        Assert.Equal("Use a password of at least 8 characters.",
            Assert.Throws<ArgumentException>(() => store.Create("me@example.com", "short")).Message);
        Assert.Equal("Enter an email address.",
            Assert.Throws<ArgumentException>(() => store.Create("  ", "long enough")).Message);
    }
}
