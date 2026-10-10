using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #124: "Skip back never working". Previous walked back through played songs, except
/// in the two places the played list was wiped: the Repeat All wrap (History.Clear, so
/// Previous on the first song of a new pass did nothing) and the end of the queue
/// (StopAndClear, so nothing was left to go back to once the last song ended).
/// </summary>
public class QueueHistoryIssue124Tests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    private static void End(FakeAudioPlayer player, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            player.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Previous(PlayerViewModel vm)
    {
        vm.PreviousCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Titles(IEnumerable<Track> tracks) => string.Join(",", tracks.Select(t => t.Title));

    [AvaloniaFact]
    public void RepeatAll_AfterTheWrap_PreviousGoesBackIntoTheLastPass()
    {
        var (vm, player) = CreateVm();
        vm.RepeatMode = RepeatMode.All;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);

        End(player, 3); // a, b, c played → wraps to a
        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c", Titles(vm.UpNext));
        Assert.Equal("c,b,a", Titles(vm.History));

        Previous(vm);
        Assert.Equal("c", vm.CurrentTrack?.Title);
        Previous(vm);
        Assert.Equal("b", vm.CurrentTrack?.Title);
        Assert.Equal("c,a,b,c", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void RepeatAll_WrapsAgainAndAgain_WithTheSameCycle()
    {
        var (vm, player) = CreateVm();
        vm.RepeatMode = RepeatMode.All;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);

        End(player, 6); // two full passes
        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c", Titles(vm.UpNext));
    }

    /// <summary>No recorded cycle (songs queued onto the emptied player, GitHub #92): the wrap
    /// replays what this queue played — never the queue that ended before it — and keeping
    /// History across the wrap must not double the next pass.</summary>
    [AvaloniaFact]
    public void RepeatAll_WithoutARecordedCycle_ReplaysOnlyThisQueue_WithoutDoubling()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        End(player, 2); // queue ended, player emptied
        Assert.Null(vm.CurrentTrack);

        vm.AddToQueue(Trk("x"), announce: false);
        vm.AddToQueue(Trk("y"), announce: false);
        vm.PlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("x", vm.CurrentTrack?.Title);
        vm.RepeatMode = RepeatMode.All;

        End(player, 2);
        Assert.Equal("x", vm.CurrentTrack?.Title);
        Assert.Equal("y", Titles(vm.UpNext));

        End(player, 2);
        Assert.Equal("x", vm.CurrentTrack?.Title);
        Assert.Equal("y", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void QueueEnd_KeepsWhatPlayed_SoPreviousPlaysTheLastSongsAgain()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b"), Trk("c") }, 0);

        End(player, 3);
        Assert.Null(vm.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Empty(vm.UpNext);
        Assert.Equal("c,b,a", Titles(vm.History));

        Previous(vm);
        Assert.Equal("c", vm.CurrentTrack?.Title);
        Previous(vm);
        Assert.Equal("b", vm.CurrentTrack?.Title);
        Assert.Equal("c", Titles(vm.UpNext));
    }

    /// <summary>The kept songs belong to the queue that ended: turning Repeat All on afterwards
    /// must not make Next "wrap" an empty player (that path wiped everything).</summary>
    [AvaloniaFact]
    public void QueueEnd_ThenRepeatAll_NextStaysUnavailable()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        End(player, 2);

        vm.RepeatMode = RepeatMode.All;

        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.Equal("b,a", Titles(vm.History));
    }
}
