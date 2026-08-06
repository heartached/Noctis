using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Tag-based Autoplay: when the queue is exhausted by a natural track end and the
/// (default-off) setting is on, playback continues with similar library tracks —
/// same genre first, same primary artist as fallback, stop when neither matches.
/// These tests drive the real natural-end entry (TrackEnded → AdvanceQueue), so
/// they also lock the paths that must NOT autoplay: setting off, repeat wrap,
/// and stop-after-current.
/// </summary>
public class AutoplayQueueTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player, FakeLibraryService library) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(
            player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player, library);
    }

    private static Track Trk(string name, string genre = "", string artist = "A") => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = artist,
        Genre = genre,
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    /// <summary>Raises the player's natural end and pumps the dispatcher so the
    /// posted AdvanceQueue(Natural) runs — the exact path VLC's TrackEnded takes.</summary>
    private static void EndTrackNaturally(FakeAudioPlayer player)
    {
        player.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void NaturalDrain_WithAutoplayOn_ContinuesWithSameGenre()
    {
        var (vm, player, library) = CreateVm();
        var seed = Trk("seed", genre: "Rock");
        // Trim + case variants must count as the same genre; Pop must never be picked.
        var rock1 = Trk("rock1", genre: "rock ");
        var rock2 = Trk("rock2", genre: "ROCK");
        var pop = Trk("pop", genre: "Pop");
        library.TrackList.AddRange(new[] { seed, rock1, rock2, pop });

        vm.AutoplayEnabled = true;
        vm.ReplaceQueueAndPlay(new[] { seed }, 0); // queue is empty behind the seed

        EndTrackNaturally(player);

        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.NotNull(vm.CurrentTrack);
        Assert.NotEqual(seed.Id, vm.CurrentTrack!.Id);
        // Everything autoplay queued (playing + upcoming) is a genre match.
        var continued = new List<Track> { vm.CurrentTrack! };
        continued.AddRange(vm.UpNext);
        Assert.Equal(2, continued.Count); // both rock tracks, never the pop one
        Assert.All(continued, t => Assert.Equal("rock", t.Genre.Trim(), ignoreCase: true));
    }

    [AvaloniaFact]
    public void NaturalDrain_WithAutoplayOff_StopsExactlyAsBefore()
    {
        var (vm, player, library) = CreateVm();
        var seed = Trk("seed", genre: "Rock");
        var rock1 = Trk("rock1", genre: "Rock");
        library.TrackList.AddRange(new[] { seed, rock1 });

        // AutoplayEnabled stays at its default (off).
        vm.ReplaceQueueAndPlay(new[] { seed }, 0);

        EndTrackNaturally(player);

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Null(vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
        Assert.Empty(vm.History);
    }

    [AvaloniaFact]
    public void RepeatAll_WrapsTheQueue_AutoplayNeverFires()
    {
        var (vm, player, library) = CreateVm();
        var a = Trk("a", genre: "Rock");
        var b = Trk("b", genre: "Rock");
        var extra = Trk("extra", genre: "Rock");
        library.TrackList.AddRange(new[] { a, b, extra });

        vm.AutoplayEnabled = true;
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        vm.RepeatMode = RepeatMode.All;

        EndTrackNaturally(player); // a → b
        EndTrackNaturally(player); // b ends → repeat-all wraps to a

        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
        Assert.Equal(new[] { b.Id }, vm.UpNext.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(vm.UpNext, t => t.Id == extra.Id);
    }

    [AvaloniaFact]
    public void StopAfterCurrentTrack_HaltsBeforeAutoplay()
    {
        var (vm, player, library) = CreateVm();
        var seed = Trk("seed", genre: "Rock");
        var rock1 = Trk("rock1", genre: "Rock");
        library.TrackList.AddRange(new[] { seed, rock1 });

        vm.AutoplayEnabled = true;
        vm.ReplaceQueueAndPlay(new[] { seed }, 0);
        vm.StopAfterCurrentTrack = true;

        EndTrackNaturally(player);

        // The stop-after guard halts with the track still loaded so Play resumes it.
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(seed.Id, vm.CurrentTrack?.Id);
        Assert.Empty(vm.UpNext);
        Assert.False(vm.StopAfterCurrentTrack); // one-shot flag consumed
    }

    [AvaloniaFact]
    public void NoGenre_FallsBackToSamePrimaryArtist()
    {
        var (vm, player, library) = CreateVm();
        var seed = Trk("seed", genre: "", artist: "Foo feat. Bar");
        var solo = Trk("solo", genre: "", artist: "Foo");
        var duet = Trk("duet", genre: "", artist: "Foo & Baz");
        var prefix = Trk("prefix", genre: "", artist: "Foobar"); // prefix, not the same artist
        var other = Trk("other", genre: "", artist: "Qux");
        library.TrackList.AddRange(new[] { seed, solo, duet, prefix, other });

        vm.AutoplayEnabled = true;
        vm.ReplaceQueueAndPlay(new[] { seed }, 0);

        EndTrackNaturally(player);

        Assert.Equal(PlaybackState.Playing, vm.State);
        var continued = new List<Track> { vm.CurrentTrack! };
        continued.AddRange(vm.UpNext);
        Assert.Equal(2, continued.Count); // solo + duet; never Foobar or Qux
        Assert.All(continued, t =>
            Assert.Equal("Foo", Track.GetPrimaryArtist(t.Artist), ignoreCase: true));
    }

    [AvaloniaFact]
    public void NoCandidates_StopsAsToday()
    {
        var (vm, player, library) = CreateVm();
        var seed = Trk("seed", genre: "Jazz", artist: "Foo");
        var pop1 = Trk("pop1", genre: "Pop", artist: "Bar");
        var pop2 = Trk("pop2", genre: "Pop", artist: "Baz");
        library.TrackList.AddRange(new[] { seed, pop1, pop2 });

        vm.AutoplayEnabled = true;
        vm.ReplaceQueueAndPlay(new[] { seed }, 0);

        EndTrackNaturally(player);

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Null(vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
    }

    [AvaloniaFact]
    public void ExhaustedPool_AllowsReuseInsteadOfStopping()
    {
        var (vm, player, library) = CreateVm();
        var seed = Trk("seed", genre: "Rock");
        var only = Trk("only", genre: "Rock"); // a one-track genre pool
        library.TrackList.AddRange(new[] { seed, only });

        vm.AutoplayEnabled = true;
        vm.ReplaceQueueAndPlay(new[] { seed }, 0);

        EndTrackNaturally(player); // autoplay → "only" (the sole candidate)
        Assert.Equal(only.Id, vm.CurrentTrack?.Id);

        EndTrackNaturally(player); // pool exhausted → reuse allowed, seed comes back

        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(seed.Id, vm.CurrentTrack?.Id);
    }
}
