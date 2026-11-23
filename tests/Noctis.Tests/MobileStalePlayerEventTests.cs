using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Player events reach the phone's ViewModel through the UI thread's queue (the marshal posts),
/// so one raised for a song can arrive after that song was replaced or dropped. A late
/// song-ended or error then started an Up Next song nobody asked for, and a late position tick
/// put a stopped song back at its old place, so Play resumed it mid-song.
/// </summary>
public class MobileStalePlayerEventTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Track[] Tracks(int n) => Enumerable.Range(0, n)
        .Select(i => new Track { Id = Guid.NewGuid(), Title = $"t{i}", FilePath = $"content://x/t{i}", Duration = TimeSpan.FromSeconds(100 + i) })
        .ToArray();

    /// <summary>The phone's marshal posts; this one holds the work until <see cref="Flush"/>.</summary>
    private sealed class PostedMarshal
    {
        private readonly Queue<Action> _posted = new();
        public bool Hold { get; set; }
        public void Post(Action work)
        {
            if (Hold) _posted.Enqueue(work);
            else work();
        }
        public void Flush()
        {
            Hold = false;
            while (_posted.Count > 0) _posted.Dequeue()();
        }
    }

    private (NowPlayingViewModel Vm, FakeAudioPlayer Player, PostedMarshal Marshal) Make(Track[] tracks)
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(tracks);
        var marshal = new PostedMarshal();
        var vm = new NowPlayingViewModel(player, library, new PersistenceService(_root), marshal: marshal.Post);
        return (vm, player, marshal);
    }

    // ── Bug: a late song-ended / error after the current song was dropped ──

    [Fact]
    public void TrackEnded_AfterTheCurrentSongWasDropped_StartsNothing()
    {
        var t = Tracks(3);
        var (vm, player, _) = Make(t);
        vm.PlayTracks(t, 0);

        vm.DropTracks(x => x == t[0]);           // signed out: the playing song left
        player.RaiseTrackEnded();                // its end, raised before the stop, arrives now

        Assert.Equal(new[] { t[0].FilePath }, player.PlayedPaths);
        Assert.Null(vm.CurrentTrack);
        Assert.False(vm.IsPlaying);
        Assert.Equal(new[] { t[1], t[2] }, vm.UpNext);
    }

    [Fact]
    public void PlaybackError_AfterTheCurrentSongWasDropped_StartsNothing()
    {
        var t = Tracks(3);
        var (vm, player, _) = Make(t);
        vm.PlayTracks(t, 0);

        vm.DropTracks(x => x == t[0]);
        player.RaisePlaybackError("ERROR_CODE_IO_UNSPECIFIED: late");

        Assert.Equal(new[] { t[0].FilePath }, player.PlayedPaths);
        Assert.Null(vm.CurrentTrack);
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void TrackEnded_PostedBeforeTheDrop_StartsNothing()
    {
        var t = Tracks(3);
        var (vm, player, marshal) = Make(t);
        vm.PlayTracks(t, 0);

        marshal.Hold = true;
        player.RaiseTrackEnded();                // posted, not run yet
        vm.DropTracks(x => x == t[0]);
        marshal.Flush();

        Assert.Equal(new[] { t[0].FilePath }, player.PlayedPaths);
        Assert.Null(vm.CurrentTrack);
    }

    [Fact]
    public void TrackEnded_PostedBeforeNext_DoesNotSkipTheSongNextStarted()
    {
        var t = Tracks(3);
        var (vm, player, marshal) = Make(t);
        vm.PlayTracks(t, 0);

        marshal.Hold = true;
        player.RaiseTrackEnded();                // t0's end, posted
        vm.NextCommand.Execute(null);            // the user skipped to t1 meanwhile
        marshal.Flush();

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(new[] { t[0].FilePath, t[1].FilePath }, player.PlayedPaths);
    }

    // ── Bug: a late position tick puts a stopped song back mid-song ──

    [Fact]
    public void PositionTick_AfterNextStoppedTheQueue_DoesNotMoveTheStoppedSong()
    {
        var t = Tracks(1);
        var (vm, player, _) = Make(t);
        vm.PlayTracks(t, 0);
        player.RaisePositionChanged(TimeSpan.FromSeconds(50));

        vm.NextCommand.Execute(null);            // end of the queue: stops, back to 00:00
        player.RaisePositionChanged(TimeSpan.FromSeconds(50));   // late tick of the old song

        Assert.Equal(TimeSpan.Zero, vm.Position);
        vm.TogglePlayPauseCommand.Execute(null);
        Assert.Equal(-1, player.PendingSeekMs);  // from the start, not 00:50
    }

    [Fact]
    public void PositionTick_PostedBeforeNext_DoesNotLandOnTheNewSong()
    {
        var t = Tracks(2);
        var (vm, player, marshal) = Make(t);
        vm.PlayTracks(t, 0);

        marshal.Hold = true;
        player.RaisePositionChanged(TimeSpan.FromSeconds(50));   // t0's tick, posted
        vm.NextCommand.Execute(null);
        marshal.Flush();

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(TimeSpan.Zero, vm.Position);
    }

    // ── Bug: Play Next / Add to Queue after the queue ran out ──

    private (NowPlayingViewModel Vm, FakeAudioPlayer Player, Track[] Tracks) RanOut()
    {
        var t = Tracks(4);
        var (vm, player, _) = Make(t);
        vm.PlayTracks(t.Take(2).ToArray(), 0);
        player.RaiseTrackEnded();
        player.RaiseTrackEnded();                // last song ended, Repeat off
        Assert.False(vm.IsPlaying);
        Assert.Same(t[1], vm.CurrentTrack);
        return (vm, player, t);
    }

    [Fact]
    public void PlayNext_AfterTheQueueRanOut_DoesNotStart_ThenPlayStartsTheAddedSong()
    {
        var (vm, player, t) = RanOut();

        vm.PlayNext(new[] { t[2], t[3] });

        Assert.False(vm.IsPlaying);              // no playback without a tap
        Assert.Equal(2, player.PlayedPaths.Count);
        Assert.Equal(new[] { t[2], t[3] }, vm.UpNext);

        vm.TogglePlayPauseCommand.Execute(null);

        Assert.Same(t[2], vm.CurrentTrack);
        Assert.Equal(t[2].FilePath, player.PlayedPaths.Last());
        Assert.Equal(-1, player.PendingSeekMs);
        Assert.Equal(new[] { t[3] }, vm.UpNext);
    }

    [Fact]
    public void AddToQueue_AfterTheQueueRanOut_PlayStartsTheAddedSong()
    {
        var (vm, player, t) = RanOut();

        vm.AddToQueue(t[3]);
        vm.TogglePlayPauseCommand.Execute(null);

        Assert.Same(t[3], vm.CurrentTrack);
        Assert.Equal(t[3].FilePath, player.PlayedPaths.Last());
        Assert.Empty(vm.UpNext);
    }

    [Fact]
    public void Play_AfterTheQueueRanOut_WithNothingAdded_StillReplaysTheLastSong()
    {
        var (vm, player, t) = RanOut();

        vm.TogglePlayPauseCommand.Execute(null);

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(t[1].FilePath, player.PlayedPaths.Last());
    }
}
