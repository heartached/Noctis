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
}
