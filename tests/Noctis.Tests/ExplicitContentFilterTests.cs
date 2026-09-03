using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Explicit Content (default on). Off is a PLAYBACK filter: explicit tracks are
/// skipped when the queue advances, pruned from the queue when the switch flips, and left
/// out of shuffle — but a track the user plays directly still plays.
/// </summary>
public class ExplicitContentFilterTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player, FakeLibraryService library) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(
            player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player, library);
    }

    private static Track Trk(string name, bool explicitTrack = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        IsExplicit = explicitTrack,
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    private static void EndTrackNaturally(FakeAudioPlayer player)
    {
        player.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DefaultOn_ExplicitTracksPlayLikeAnyOther()
    {
        var (vm, player, _) = CreateVm();
        var clean = Trk("clean");
        var dirty = Trk("dirty", explicitTrack: true);

        vm.ReplaceQueueAndPlay(new[] { clean, dirty }, 0);
        EndTrackNaturally(player);

        Assert.True(vm.AllowExplicitContent);
        Assert.Equal(dirty.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void Off_NaturalAdvance_SkipsOverExplicitTracks()
    {
        var (vm, player, _) = CreateVm();
        var a = Trk("a");
        var x1 = Trk("x1", explicitTrack: true);
        var x2 = Trk("x2", explicitTrack: true);
        var b = Trk("b");

        vm.ReplaceQueueAndPlay(new[] { a, x1, x2, b }, 0);
        vm.AllowExplicitContent = false;
        EndTrackNaturally(player);

        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
        Assert.Empty(vm.UpNext);
        // Skipped tracks were never played, so they must not appear in History either.
        Assert.Equal(new[] { a.Id }, vm.History.Select(t => t.Id).ToArray());
    }

    [AvaloniaFact]
    public void Off_OnlyExplicitLeft_StopsInsteadOfPlayingOne()
    {
        var (vm, player, _) = CreateVm();
        var a = Trk("a");
        var x = Trk("x", explicitTrack: true);

        vm.ReplaceQueueAndPlay(new[] { a, x }, 0);
        vm.AllowExplicitContent = false;
        EndTrackNaturally(player);

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Null(vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
    }

    [AvaloniaFact]
    public void TurningOff_PrunesExplicitTracksFromTheLiveQueue()
    {
        var (vm, _, _) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        var x = Trk("x", explicitTrack: true);
        var c = Trk("c");

        vm.ReplaceQueueAndPlay(new[] { a, b, x, c }, 0);
        Assert.Equal(3, vm.UpNext.Count);

        vm.AllowExplicitContent = false;

        Assert.Equal(new[] { b.Id, c.Id }, vm.UpNext.Select(t => t.Id).ToArray());
        // The playing track is untouched even if it were explicit — the filter never stops
        // what is already playing.
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void Off_DirectPlay_StillPlaysAnExplicitTrack()
    {
        var (vm, _, _) = CreateVm();
        var x = Trk("x", explicitTrack: true);

        vm.AllowExplicitContent = false;
        vm.ReplaceQueueAndPlay(new[] { x }, 0);

        Assert.Equal(x.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void Off_RepeatAll_WrapsWithoutTheExplicitTracks()
    {
        var (vm, player, _) = CreateVm();
        var a = Trk("a");
        var x = Trk("x", explicitTrack: true);
        var b = Trk("b");

        vm.ReplaceQueueAndPlay(new[] { a, x, b }, 0);
        vm.RepeatMode = RepeatMode.All;
        vm.AllowExplicitContent = false;

        EndTrackNaturally(player); // a → b (x pruned)
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
        EndTrackNaturally(player); // b ends → wraps to a, x must not come back

        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
        Assert.DoesNotContain(vm.UpNext, t => t.IsExplicit);
    }

    [AvaloniaFact]
    public void Off_ThenOn_RestoresExplicitTracksToTheirPlaces()
    {
        // The user's repro: shuffle a library, flip the toggle off and back on — the
        // explicit tracks must come back, each in front of the track that followed it.
        var (vm, _, _) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        var x = Trk("x", explicitTrack: true);
        var c = Trk("c");
        var y = Trk("y", explicitTrack: true);
        var d = Trk("d");

        vm.ReplaceQueueAndPlay(new[] { a, b, x, c, y, d }, 0);
        vm.AllowExplicitContent = false;
        Assert.Equal(new[] { b.Id, c.Id, d.Id }, vm.UpNext.Select(t => t.Id).ToArray());

        vm.AllowExplicitContent = true;

        Assert.Equal(new[] { b.Id, x.Id, c.Id, y.Id, d.Id }, vm.UpNext.Select(t => t.Id).ToArray());
    }

    [AvaloniaFact]
    public void Off_ThenOn_AfterTheNeighbourPlayed_AppendsInstead()
    {
        var (vm, player, _) = CreateVm();
        var a = Trk("a");
        var x = Trk("x", explicitTrack: true);
        var b = Trk("b");
        var c = Trk("c");

        vm.ReplaceQueueAndPlay(new[] { a, x, b, c }, 0);
        vm.AllowExplicitContent = false;
        EndTrackNaturally(player); // a → b; x's neighbour is now playing
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);

        vm.AllowExplicitContent = true;

        Assert.Equal(new[] { c.Id, x.Id }, vm.UpNext.Select(t => t.Id).ToArray());
    }

    [AvaloniaFact]
    public void Off_ThenOn_TrailingExplicitTracks_ComeBackAtTheEnd()
    {
        var (vm, _, _) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        var x = Trk("x", explicitTrack: true);
        var y = Trk("y", explicitTrack: true);

        vm.ReplaceQueueAndPlay(new[] { a, b, x, y }, 0);
        vm.AllowExplicitContent = false;
        vm.AllowExplicitContent = true;

        Assert.Equal(new[] { b.Id, x.Id, y.Id }, vm.UpNext.Select(t => t.Id).ToArray());
    }

    [AvaloniaFact]
    public void ReplacingTheQueue_ForgetsParkedTracks()
    {
        var (vm, _, _) = CreateVm();
        var a = Trk("a");
        var x = Trk("x", explicitTrack: true);
        var c = Trk("c");
        var d = Trk("d");

        vm.ReplaceQueueAndPlay(new[] { a, x }, 0);
        vm.AllowExplicitContent = false;
        vm.ReplaceQueueAndPlay(new[] { c, d }, 0); // a new queue: the parked x belongs to the old one
        vm.AllowExplicitContent = true;

        Assert.Equal(new[] { d.Id }, vm.UpNext.Select(t => t.Id).ToArray());
    }

    [AvaloniaFact]
    public void ShuffleRoundTrip_WhileOff_NeverResurrectsAndNeverDuplicates()
    {
        var (vm, _, _) = CreateVm();
        var a = Trk("a");
        var x = Trk("x", explicitTrack: true);
        var b = Trk("b");
        var c = Trk("c");

        vm.ReplaceQueueAndPlay(new[] { a, x, b, c }, 0);
        vm.AllowExplicitContent = false;
        Assert.DoesNotContain(vm.UpNext, t => t.IsExplicit);

        vm.ToggleShuffleCommand.Execute(null); // shuffle on
        Assert.DoesNotContain(vm.UpNext, t => t.IsExplicit);
        vm.ToggleShuffleCommand.Execute(null); // shuffle off → original order restored
        Assert.DoesNotContain(vm.UpNext, t => t.IsExplicit);

        vm.AllowExplicitContent = true;

        Assert.Single(vm.UpNext, t => t.Id == x.Id);
        Assert.Equal(3, vm.UpNext.Count);
    }

    [AvaloniaFact]
    public void ShufflingWhileOff_ParksTheExplicitTracks_SoOnBringsThemBack()
    {
        var (vm, _, _) = CreateVm();
        var a = Trk("a");
        var x = Trk("x", explicitTrack: true);
        var b = Trk("b");

        vm.ReplaceQueueAndPlay(new[] { a, x, b }, 0);
        vm.AllowExplicitContent = false;
        vm.ToggleShuffleCommand.Execute(null); // shuffle on while off
        Assert.DoesNotContain(vm.UpNext, t => t.IsExplicit);

        vm.AllowExplicitContent = true;

        Assert.Contains(vm.UpNext, t => t.Id == x.Id);
        Assert.Equal(2, vm.UpNext.Count);
    }

    [Fact]
    public void WeightedShuffle_ExcludesExplicit_OnlyWhenAsked()
    {
        var tracks = new[] { Trk("a"), Trk("x", explicitTrack: true), Trk("b") };

        var allowed = ShuffleHelper.WeightedShuffle(tracks, new Random(1));
        var filtered = ShuffleHelper.WeightedShuffle(tracks, new Random(1), allowExplicit: false);

        Assert.Equal(3, allowed.Count);
        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, t => t.IsExplicit);
    }
}
