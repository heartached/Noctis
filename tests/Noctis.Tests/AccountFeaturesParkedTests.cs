using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// v1.5.8 ships the account and sync cards as Coming soon (<see cref="AccountFeatures"/> off):
/// a SyncEnabled flag or an account left by an earlier test build activates nothing, and no
/// command can create an account or turn sync on.
/// </summary>
public class AccountFeaturesParkedTests : IDisposable
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

    [Fact]
    public void Switch_IsOffForThisRelease() => Assert.False(AccountFeatures.Enabled);

    [AvaloniaFact]
    public async Task StoredSyncFlag_StaysOff_AndNothingTurnsItOn()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { SyncEnabled = true });

        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        Assert.False(vm.AccountFeaturesEnabled);
        Assert.False(vm.SyncEnabled);

        vm.SyncEnabled = true; // the toggle, or TogglePairing's "sync on"
        Assert.False(vm.SyncEnabled);
        Assert.False(vm.TogglePairingCommand.CanExecute(null));

        // The stored flag is left for a build that ships the feature; it activates nothing here.
        await vm.SaveAsync();
        Assert.True((await persistence.LoadSettingsAsync()).SyncEnabled);
    }

    [AvaloniaFact]
    public async Task CreateAccount_CannotExecute_AndCreatesNothing()
    {
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();

        Assert.False(vm.CreatePrimaryAccountCommand.CanExecute(null));
        Assert.False(vm.AddServerUserCommand.CanExecute(null));

        vm.NewServerUserName = "me@example.com";
        vm.NewServerUserPassword = "correct horse";
        vm.CreatePrimaryAccountCommand.Execute(null); // Execute does not consult CanExecute
        Assert.False(vm.HasPrimaryAccount);
        Assert.Empty(new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db")).List());
    }
}
