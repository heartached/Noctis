using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The playlist page view-model is rebuilt on every navigation, so the chosen sort
/// only survives if it round-trips through the persisted Playlist — otherwise it
/// snaps back to Manual (Discord: "sorting always reverts back to manual selection").
/// </summary>
public class PlaylistSortPersistenceTests
{
    private static PlaylistViewModel CreateVm(Playlist playlist)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        return new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
    }

    [Fact]
    public void Ctor_RestoresTheSavedSortMode()
    {
        var playlist = new Playlist { Name = "P", SortMode = "Title" };
        var vm = CreateVm(playlist);
        Assert.Equal(PlaylistSortMode.Title, vm.SortMode);
    }

    [Fact]
    public void Ctor_UnknownSavedSortMode_FallsBackToManual()
    {
        var playlist = new Playlist { Name = "P", SortMode = "garbage" };
        var vm = CreateVm(playlist);
        Assert.Equal(PlaylistSortMode.Manual, vm.SortMode);
    }

    [Fact]
    public void SetSort_WritesTheChoiceBackToThePlaylist()
    {
        var playlist = new Playlist { Name = "P" };
        var vm = CreateVm(playlist);

        vm.SetSortCommand.Execute("RecentlyAdded");

        Assert.Equal(PlaylistSortMode.RecentlyAdded, vm.SortMode);
        Assert.Equal("RecentlyAdded", playlist.SortMode);
    }
}
