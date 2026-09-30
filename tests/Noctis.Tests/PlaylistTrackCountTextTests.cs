using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>The playlist header read "1 tracks" (09-30, owner screenshot).</summary>
public class PlaylistTrackCountTextTests
{
    private static PlaylistViewModel CreateVm()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var playlist = new Playlist { Name = "Mix" };
        var sidebar = new SidebarViewModel(persistence, lib);
        sidebar.Playlists.Add(playlist);
        return new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
    }

    [Theory]
    [InlineData(0, "0 tracks")]
    [InlineData(1, "1 track")]
    [InlineData(2, "2 tracks")]
    [InlineData(12, "12 tracks")]
    public void TrackCountText_IsSingularForOne(int count, string expected)
    {
        var vm = CreateVm();
        vm.TrackCount = count;
        Assert.Equal(expected, vm.TrackCountText);
    }

    [Fact]
    public void TrackCountText_NotifiesWhenTheCountChanges()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.TrackCount = 7;

        Assert.Contains(nameof(PlaylistViewModel.TrackCountText), raised);
    }
}
