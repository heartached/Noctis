using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Queue/shuffle/repeat behavior through the real PlayerViewModel — the exact
/// interactions the 2026-07-23 audit found broken (Repeat One trapping Next,
/// un-shuffle resurrecting played tracks) now have regression coverage.
/// </summary>
public class PlayerQueueTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    private static Track Trk(string name, bool skipWhenShuffling = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = $"C:/t/{name}.mp3",
        Duration = TimeSpan.FromMinutes(3),
        SkipWhenShuffling = skipWhenShuffling
    };

    [Fact]
    public void RepeatOne_ExplicitNext_StillAdvances()
    {
        var (vm, _) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        vm.RepeatMode = RepeatMode.One;

        vm.NextCommand.Execute(null);

        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
    }

    [Fact]
    public void ShuffleOff_DoesNotResurrectPlayedTracks()
    {
        var (vm, _) = CreateVm();
        var tracks = Enumerable.Range(0, 6).Select(i => Trk($"t{i}")).ToList();
        vm.ReplaceQueueAndPlay(tracks, 0); // playing t0, queue = t1..t5

        vm.ToggleShuffleCommand.Execute(null); // on
        var played = vm.UpNext[0]; // simulate the first shuffled track playing
        vm.UpNext.RemoveAt(0);

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.DoesNotContain(vm.UpNext, t => t.Id == played.Id);
        // Remaining tracks come back in original order.
        var expected = tracks.Skip(1).Where(t => t.Id != played.Id).Select(t => t.Id).ToList();
        Assert.Equal(expected, vm.UpNext.Select(t => t.Id).ToList());
    }

    [Fact]
    public void ShuffleOff_RestoresSkipWhenShufflingTracks()
    {
        var (vm, _) = CreateVm();
        var normal = Enumerable.Range(0, 4).Select(i => Trk($"n{i}")).ToList();
        var excluded = Trk("excluded", skipWhenShuffling: true);
        var queue = new List<Track>(normal) { excluded };
        vm.ReplaceQueueAndPlay(queue, 0); // playing n0

        vm.ToggleShuffleCommand.Execute(null); // on
        // The flagged track is filtered out of the shuffled queue...
        Assert.DoesNotContain(vm.UpNext, t => t.Id == excluded.Id);

        vm.ToggleShuffleCommand.Execute(null); // off
        // ...but it never played, so un-shuffle must bring it back.
        Assert.Contains(vm.UpNext, t => t.Id == excluded.Id);
    }

    [Fact]
    public void Shuffle_KeepsSameTrackSet()
    {
        var (vm, _) = CreateVm();
        var tracks = Enumerable.Range(0, 8).Select(i => Trk($"t{i}")).ToList();
        vm.ReplaceQueueAndPlay(tracks, 0);
        var before = vm.UpNext.Select(t => t.Id).OrderBy(g => g).ToList();

        vm.ToggleShuffleCommand.Execute(null); // on
        var after = vm.UpNext.Select(t => t.Id).OrderBy(g => g).ToList();

        Assert.Equal(before, after);
    }

    /// <summary>Skips to the last track, then once more so Repeat All wraps; returns the new pass.</summary>
    private static List<Guid> WrapRepeatAll(PlayerViewModel vm)
    {
        while (vm.UpNext.Count > 0) vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        var pass = new List<Guid> { vm.CurrentTrack!.Id };
        pass.AddRange(vm.UpNext.Select(t => t.Id));
        return pass;
    }

    [Fact]
    public void RepeatAll_WithShuffleOn_WrapStartsAReshuffledPass()
    {
        var (vm, _) = CreateVm();
        var tracks = Enumerable.Range(0, 20).Select(i => Trk($"t{i}")).ToList();
        vm.ReplaceQueueAndPlay(tracks, 0);
        vm.RepeatMode = RepeatMode.All;
        vm.ToggleShuffleCommand.Execute(null); // on

        var pass = WrapRepeatAll(vm);

        Assert.True(vm.IsShuffleEnabled);
        Assert.Equal(tracks.Select(t => t.Id).OrderBy(g => g), pass.OrderBy(g => g));
        // The wrap replayed the album order with Shuffle lit (1 in 20! by chance).
        Assert.NotEqual(tracks.Select(t => t.Id), pass);

        vm.ToggleShuffleCommand.Execute(null); // off: the rest of the pass in queue order
        var current = vm.CurrentTrack!.Id;
        Assert.Equal(tracks.Select(t => t.Id).Where(id => id != current), vm.UpNext.Select(t => t.Id));
    }

    [Fact]
    public void RepeatAll_WrapReplaysTheQueueAsEdited()
    {
        var (vm, _) = CreateVm();
        var (a, b, c, d, x, y) = (Trk("a"), Trk("b"), Trk("c"), Trk("d"), Trk("x"), Trk("y"));
        vm.ReplaceQueueAndPlay(new[] { a, b, c, d }, 0);
        vm.RepeatMode = RepeatMode.All;

        vm.AddNext(y);         // y b c d
        vm.AddToQueue(x);      // y b c d x
        vm.RemoveFromQueue(2); // y b d x

        Assert.Equal(new[] { a.Id, y.Id, b.Id, d.Id, x.Id }, WrapRepeatAll(vm));
    }

    [Fact]
    public void RepeatAll_WrapKeepsRangeAddsAndMultiRemoves()
    {
        var (vm, _) = CreateVm();
        var (a, b, c, x, y) = (Trk("a"), Trk("b"), Trk("c"), Trk("x"), Trk("y"));
        vm.ReplaceQueueAndPlay(new[] { a, b, c }, 0);
        vm.RepeatMode = RepeatMode.All;

        vm.AddRangeToQueue(new[] { x, y });      // b c x y
        vm.RemoveManyFromQueue(new[] { 0, 2 }); // c y

        Assert.Equal(new[] { a.Id, c.Id, y.Id }, WrapRepeatAll(vm));
    }

    [Fact]
    public void RepeatAll_AfterClearQueue_WrapReplaysOnlyWhatIsLeft()
    {
        var (vm, _) = CreateVm();
        var (a, b, c) = (Trk("a"), Trk("b"), Trk("c"));
        vm.ReplaceQueueAndPlay(new[] { a, b, c }, 0);
        vm.RepeatMode = RepeatMode.All;

        vm.ClearQueue();

        Assert.Equal(new[] { a.Id }, WrapRepeatAll(vm));
    }

    [Fact]
    public void RepeatAll_AfterStopAndClear_DoesNotWrapIntoTheOldQueue()
    {
        var (vm, _) = CreateVm();
        var (a, b, c, d) = (Trk("a"), Trk("b"), Trk("c"), Trk("d"));
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        vm.RepeatMode = RepeatMode.All;

        vm.StopAndClear("test");
        vm.AddToQueue(c);
        vm.AddToQueue(d);
        vm.PlayPauseCommand.Execute(null); // plays c from the refilled queue

        Assert.Equal(new[] { c.Id, d.Id }, WrapRepeatAll(vm));
    }
}
