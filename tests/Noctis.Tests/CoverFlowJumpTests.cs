using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Cover Flow carousel jumps: clicking a side card (or wheel / arrows) plays that track.
/// Forward reuses PlayFromUpNextAt; backward is PlayFromHistoryAt, which must re-queue the
/// current track and everything skipped over so the order survives the jump.
/// </summary>
public class CoverFlowJumpTests
{
    private static (PlayerViewModel player, Track a, Track b, Track c, Track d) Seed()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var a = new Track { Title = "a", FilePath = "a.mp3" };
        var b = new Track { Title = "b", FilePath = "b.mp3" };
        var c = new Track { Title = "c", FilePath = "c.mp3" };
        var d = new Track { Title = "d", FilePath = "d.mp3" };
        // Each replacement pushes the previous current into history → history [b, a],
        // current c, up next [d].
        player.ReplaceQueueAndPlay(new[] { a }, 0);
        player.ReplaceQueueAndPlay(new[] { b }, 0);
        player.ReplaceQueueAndPlay(new[] { c, d }, 0);
        Assert.Same(c, player.CurrentTrack);
        Assert.Equal(new[] { b, a }, player.History);
        Assert.Equal(new[] { d }, player.UpNext);
        return (player, a, b, c, d);
    }

    [Fact]
    public void PlayFromHistoryAt_TwoBack_RequeuesTheSkippedTracksInOrder()
    {
        var (player, a, b, c, d) = Seed();

        player.PlayFromHistoryAt(1);

        Assert.Same(a, player.CurrentTrack);
        Assert.Empty(player.History);
        Assert.Equal(new[] { b, c, d }, player.UpNext);
    }

    /// <summary>Three back skips two played songs: they replay oldest first, as three
    /// Previous presses leave them (the newest one came back first, out of order).</summary>
    [Fact]
    public void PlayFromHistoryAt_ThreeBack_RequeuesTheSkippedTracksOldestFirst()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = new Track { Title = "a", FilePath = "a.mp3" };
        var b = new Track { Title = "b", FilePath = "b.mp3" };
        var c = new Track { Title = "c", FilePath = "c.mp3" };
        var d = new Track { Title = "d", FilePath = "d.mp3" };
        player.ReplaceQueueAndPlay(new[] { a }, 0);
        player.ReplaceQueueAndPlay(new[] { b }, 0);
        player.ReplaceQueueAndPlay(new[] { c }, 0);
        player.ReplaceQueueAndPlay(new[] { d }, 0);
        Assert.Equal(new[] { c, b, a }, player.History);

        player.PlayFromHistoryAt(2);

        Assert.Same(a, player.CurrentTrack);
        Assert.Equal(new[] { b, c, d }, player.UpNext);
    }

    [Fact]
    public void PlayFromHistoryAt_OneBack_MatchesPrevious()
    {
        var (player, a, b, c, d) = Seed();

        player.PlayFromHistoryAt(0);

        Assert.Same(b, player.CurrentTrack);
        Assert.Equal(new[] { a }, player.History);
        Assert.Equal(new[] { c, d }, player.UpNext);
    }

    [Fact]
    public void PlayFromHistoryAt_OutOfRange_IsANoOp()
    {
        var (player, _, _, c, _) = Seed();
        player.PlayFromHistoryAt(2);
        player.PlayFromHistoryAt(-1);
        Assert.Same(c, player.CurrentTrack);
        Assert.Equal(2, player.History.Count);
    }

    [AvaloniaFact]
    public void JumpTo_RoutesForwardAndBack_AndReportsTheStep()
    {
        var (player, a, b, c, d) = Seed();
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        var steps = new List<int>();
        vm.CarouselShifted += (_, s) => steps.Add(s);

        vm.JumpToCommand.Execute(-2);           // a: the −2 card
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(a, player.CurrentTrack);
        Assert.Same(a, vm.CenterTrack);
        Assert.Contains(-2, steps);

        vm.JumpToCommand.Execute(2);            // from a: up next [b, c, d] → c
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(c, player.CurrentTrack);
        Assert.Contains(2, steps);

        vm.JumpToCommand.Execute(0);            // no-op
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(c, player.CurrentTrack);
    }
}
