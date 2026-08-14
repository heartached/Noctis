using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Flipping "Merge Featured Artists From Titles" or "Collapse Album Editions" inside
/// the Settings modal used to rebuild the covered Albums/Artists/Favorites grids on
/// the UI thread mid-click — the modal fully covers those views, so the work was
/// invisible yet janked the very toggle animation that triggered it. Hidden VMs now
/// mark dirty and catch up on activation, and the Artists rebuild runs off-thread
/// (the same generation-guarded pattern Albums and Songs already use).
/// </summary>
public class SettingsToggleRebuildGatingTests
{
    private sealed class NoOpPlayHistoryService : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Pumps the headless dispatcher until the condition holds (or the budget runs out).</summary>
    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps for a fixed window — for asserting that nothing happens.</summary>
    private static async Task PumpFor(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Artists_LibraryUpdateWhileHidden_DefersRebuildUntilActivation()
    {
        var lib = new FakeLibraryService();
        lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Alpha" });
        lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Beta" });
        var vm = new LibraryArtistsViewModel(lib);

        // Hidden (e.g. beneath the Settings modal): the event only marks dirty.
        lib.RaiseLibraryUpdated();
        await PumpFor(150);
        Assert.Empty(vm.ArtistRows);

        // Activation catches up; the rebuild lands asynchronously.
        vm.IsActive = true;
        await PumpUntil(() => vm.ArtistRows.Count > 0);

        var row = Assert.Single(vm.ArtistRows);
        Assert.Equal(new[] { "Alpha", "Beta" }, row.Artists.Select(a => a.Name));
    }

    [AvaloniaFact]
    public async Task Favorites_LibraryUpdateWhileHidden_DefersRebuildUntilActivation()
    {
        var lib = new FakeLibraryService();
        lib.TrackList.Add(new Track
        {
            Id = Guid.NewGuid(),
            Title = "Kept Song",
            Artist = "Artist",
            FilePath = TestPaths.Primary("fav", "kept.mp3"),
            Duration = TimeSpan.FromMinutes(3),
            IsFavorite = true,
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService());
        var vm = new FavoritesViewModel(player, lib, persistence, sidebar, settings);

        lib.RaiseLibraryUpdated();
        await PumpFor(100);
        Assert.Empty(vm.FavoriteItems);

        // Activation runs the (synchronous) catch-up rebuild.
        vm.IsActive = true;
        var item = Assert.Single(vm.FavoriteItems);
        Assert.Equal("Kept Song", item.Track?.Title);
    }
}
