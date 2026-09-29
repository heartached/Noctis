using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #74 (DanielKNDZ): moving a multi-selection as one block, and Previous
/// walking back through the playlist the user started from the middle of.
/// </summary>
public class PlaylistCustomizationIssue74Tests
{
    private static Track T(string title) => new() { Id = Guid.NewGuid(), Title = title, FilePath = title + ".flac" };

    private static PlaylistViewModel CreateVm(Playlist playlist, FakeLibraryService lib, out PlayerViewModel player)
    {
        var persistence = new TestPersistenceService();
        player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        sidebar.Playlists.Add(playlist);
        return new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
    }

    // ── Block move (issue item 4) ──

    [Theory]
    [InlineData("A,B,C,D,E", "B,D", 0, "B,D,A,C,E")]  // up to the top
    [InlineData("A,B,C,D,E", "A,B", 5, "C,D,E,A,B")]  // down to the end
    [InlineData("A,B,C,D,E", "A,C", 4, "B,D,A,C,E")]  // between D and E
    [InlineData("A,B,C,D,E", "B,C", 1, "A,B,C,D,E")]  // dropped where it already is
    [InlineData("A,B,C,D,E", "E,B", 2, "A,B,E,C,D")]  // selection order is the list order, not click order
    public void ReorderBlock_MovesTheSelectionTogether_KeepingItsListOrder(string list, string moved, int insertIndex, string expected)
    {
        var tracks = list.Split(',').Select(T).ToList();
        var byTitle = tracks.ToDictionary(t => t.Title);
        var selection = moved.Split(',').Select(t => byTitle[t]).ToList();

        var result = PlaylistViewModel.ReorderBlock(tracks, selection, insertIndex);

        Assert.Equal(expected, string.Join(",", result.Select(t => t.Title)));
    }

    [Fact]
    public async Task MoveTracks_PersistsTheNewOrder_ToThePlaylist()
    {
        var tracks = "A,B,C,D,E".Split(',').Select(T).ToList();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var playlist = new Playlist { Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        var vm = CreateVm(playlist, lib, out _);
        // LoadTracks resolves the ids on a worker and fills Tracks afterwards.
        for (var i = 0; i < 100 && vm.Tracks.Count < 5; i++) await Task.Delay(20);
        Assert.Equal(5, vm.Tracks.Count);

        await vm.MoveTracks(new[] { vm.Tracks[1], vm.Tracks[3] }, 0);

        Assert.Equal("B,D,A,C,E", string.Join(",", vm.Tracks.Select(t => t.Title)));
        Assert.Equal(vm.Tracks.Select(t => t.Id), playlist.TrackIds);
    }

    // ── Previous inside a playlist started from the middle (issue item 5) ──

    [Fact]
    public void Previous_FromAPlaylistStartedAtTrackThree_GoesToTrackTwo_NotToTheOldQueue()
    {
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var old = T("Old");
        player.ReplaceQueueAndPlay(new List<Track> { old }, 0);

        var p = "P1,P2,P3,P4".Split(',').Select(T).ToList();
        player.ReplaceQueueAndPlay(p, 2);
        Assert.Equal("P3", player.CurrentTrack?.Title);

        player.PreviousCommand.Execute(null);
        Assert.Equal("P2", player.CurrentTrack?.Title);
        Assert.Equal("P3", player.UpNext[0].Title);

        player.PreviousCommand.Execute(null);
        Assert.Equal("P1", player.CurrentTrack?.Title);

        // Past the playlist's first track, Previous falls back to what played before it.
        player.PreviousCommand.Execute(null);
        Assert.Equal("Old", player.CurrentTrack?.Title);
    }

    // Discord (aaron, 2026-09-23): start at track 3, Next to track 4, Previous landed on
    // track 2 — the pre-start tracks were consulted before the track just left.
    [Fact]
    public void Previous_AfterNextInsideAPlaylistStartedFromTheMiddle_ReturnsToTheTrackJustLeft()
    {
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var p = "P1,P2,P3,P4".Split(',').Select(T).ToList();
        player.ReplaceQueueAndPlay(p, 2);
        player.NextCommand.Execute(null);
        Assert.Equal("P4", player.CurrentTrack?.Title);

        player.PreviousCommand.Execute(null);
        Assert.Equal("P3", player.CurrentTrack?.Title);
        Assert.Equal("P4", player.UpNext[0].Title);

        // Only once the started-from track is left does Previous step into the pre-start part.
        player.PreviousCommand.Execute(null);
        Assert.Equal("P2", player.CurrentTrack?.Title);
        Assert.Equal("P3", player.UpNext[0].Title);

        player.PreviousCommand.Execute(null);
        Assert.Equal("P1", player.CurrentTrack?.Title);
    }

    [Fact]
    public void Previous_AfterNextThenBackToTheStartTrack_NextAgainThenPrevious_StillUndoesTheLatestNext()
    {
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var p = "P1,P2,P3,P4".Split(',').Select(T).ToList();
        player.ReplaceQueueAndPlay(p, 2);
        player.NextCommand.Execute(null);      // P4
        player.PreviousCommand.Execute(null);  // P3
        player.PreviousCommand.Execute(null);  // P2 (pre-start)
        player.NextCommand.Execute(null);      // P3 again
        Assert.Equal("P3", player.CurrentTrack?.Title);

        player.PreviousCommand.Execute(null);
        Assert.Equal("P2", player.CurrentTrack?.Title);

        player.PreviousCommand.Execute(null);
        Assert.Equal("P1", player.CurrentTrack?.Title);
    }

    [Fact]
    public void Previous_PrecedingTracks_NeverShowUpInHistory()
    {
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var p = "P1,P2,P3".Split(',').Select(T).ToList();
        player.ReplaceQueueAndPlay(p, 2);

        // History is what the Queue panel and Cover Flow show as played: P1/P2 were not.
        Assert.DoesNotContain(player.History, t => t.Title is "P1" or "P2");
    }
}
