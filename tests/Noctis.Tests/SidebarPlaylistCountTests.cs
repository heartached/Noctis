using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// With a library folder hidden, the sidebar said "4 songs" for a playlist whose page lists 2:
/// the count was the stored ids, the page shows the ids the library resolves.
/// </summary>
public class SidebarPlaylistCountTests
{
    private sealed class OnePlaylist : TestPersistenceService
    {
        public Playlist Playlist { get; } = new() { Id = Guid.NewGuid(), Name = "Gym" };
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist> { Playlist });
        public override Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
    }

    private static Track T(string title) => new() { Id = Guid.NewGuid(), Title = title, Duration = TimeSpan.FromMinutes(1) };

    [AvaloniaFact]
    public async Task Count_IsTheSongsThePageShows_AndFollowsTheLibrary()
    {
        var shown = new[] { T("a"), T("b") };
        var hidden = new[] { T("c"), T("d") };
        var persistence = new OnePlaylist();
        persistence.Playlist.TrackIds.AddRange(shown.Concat(hidden).Select(t => t.Id));
        var library = new FakeLibraryService();
        library.TrackList.AddRange(shown);

        var vm = new SidebarViewModel(persistence, library);
        await vm.LoadPlaylistsAsync();
        var row = vm.PlaylistItems.Single(r => r.Label == "Gym");
        Assert.Equal(2, row.TrackCount);
        Assert.StartsWith("2 tracks", row.MetaText);

        // The hidden folder is shown again: the library publishes, the count follows.
        library.TrackList.AddRange(hidden);
        library.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, row.TrackCount);
        Assert.StartsWith("4 tracks", row.MetaText);
    }

    [AvaloniaFact]
    public async Task BeforeTheLibraryLoads_TheStoredCountStandsIn()
    {
        var persistence = new OnePlaylist();
        persistence.Playlist.TrackIds.AddRange(new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() });

        var vm = new SidebarViewModel(persistence, new FakeLibraryService());
        await vm.LoadPlaylistsAsync();

        Assert.Equal(3, vm.PlaylistItems.Single(r => r.Label == "Gym").TrackCount);
    }
}
