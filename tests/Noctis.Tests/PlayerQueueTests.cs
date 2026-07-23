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
}
