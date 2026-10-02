using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>The playlist header read "1 tracks" (09-30, owner screenshot).</summary>
public class PlaylistTrackCountTextTests
{
    private static async Task<PlaylistViewModel> CreateVm()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var playlist = new Playlist { Name = "Mix" };
        var sidebar = new SidebarViewModel(persistence, lib);
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        // The constructor's background load ends by writing TrackCount = 0 (empty playlist);
        // wait for it (TotalDuration is set in the same pass) so it can't overwrite the test's value.
        var deadline = Environment.TickCount64 + 5000;
        while (vm.TotalDuration.Length == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        return vm;
    }

    [Theory]
    [InlineData(0, "0 tracks")]
    [InlineData(1, "1 track")]
    [InlineData(2, "2 tracks")]
    [InlineData(12, "12 tracks")]
    public async Task TrackCountText_IsSingularForOne(int count, string expected)
    {
        var vm = await CreateVm();
        vm.TrackCount = count;
        Assert.Equal(expected, vm.TrackCountText);
    }

    [Fact]
    public async Task TrackCountText_NotifiesWhenTheCountChanges()
    {
        var vm = await CreateVm();
        var raised = new List<string?>();
        // The VM's background load can raise PropertyChanged on another thread.
        vm.PropertyChanged += (_, e) => { lock (raised) raised.Add(e.PropertyName); };

        vm.TrackCount = 7;

        string?[] snapshot;
        lock (raised) snapshot = raised.ToArray();
        Assert.Contains(nameof(PlaylistViewModel.TrackCountText), snapshot);
    }
}
