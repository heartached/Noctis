using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// SaveAsync re-bases the in-memory AppSettings on the on-disk file
/// (MergeExternalSettingChangesAsync) and then relies on SyncToSettings to re-apply
/// every field this view-model owns. A VM-owned field missing from SyncToSettings is
/// therefore silently reverted to its stored value on every save: the About tab's
/// "Include pre-release updates" and "Developer Mode" toggles turned back off on the
/// next launch, and the volume pushed at shutdown never survived a restart. These
/// tests run the real PersistenceService against a temp root and simulate a restart
/// with a second view-model over the same data.
/// </summary>
public class SettingsViewModelPersistenceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private SettingsViewModel CreateViewModel() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    public void Dispose()
    {
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
    public async Task AboutTabToggles_SurviveSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        vm.IncludePrereleaseUpdates = true;
        vm.DeveloperMode = true;
        await vm.SaveAsync();

        // "Restart": a fresh view-model loading from the same data root.
        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.True(reloaded.IncludePrereleaseUpdates);
        Assert.True(reloaded.DeveloperMode);
    }

    [AvaloniaFact]
    public async Task ShutdownVolume_SurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        // MainWindowViewModel.ShutdownAsync pushes the player volume, then saves.
        vm.SetVolume(37);
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.Equal(37, reloaded.GetSettings().Volume);
    }
}
