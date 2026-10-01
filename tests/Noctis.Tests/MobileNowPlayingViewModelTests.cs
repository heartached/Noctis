using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone transport over IAudioPlayer + Core PlaybackQueue, with queue persistence.</summary>
public class MobileNowPlayingViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Track[] Tracks(int n) => Enumerable.Range(0, n)
        .Select(i => new Track { Id = Guid.NewGuid(), Title = $"t{i}", FilePath = $"content://x/t{i}", Duration = TimeSpan.FromSeconds(100 + i) })
        .ToArray();

    private (NowPlayingViewModel Vm, FakeAudioPlayer Player, FakeLibraryService Library, PersistenceService Persistence) Make(Track[] tracks)
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(tracks);
        var persistence = new PersistenceService(_root);
        var vm = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        return (vm, player, library, persistence);
    }

    [Fact]
    public void PlayTracks_StartsTheChosenTrack_AndPreparesTheNextOneForGapless()
    {
        var t = Tracks(3);
        var (vm, player, _, _) = Make(t);

        vm.PlayTracks(t, 1);

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.True(vm.IsPlaying);
        Assert.Equal(new[] { t[1].FilePath }, player.PlayedPaths);
        Assert.Equal(new[] { t[2].FilePath }, player.PreparedPaths);
        Assert.Equal(new[] { t[2] }, vm.UpNext);
        Assert.Equal(t[1].Duration, vm.Duration);
    }

    [Fact]
    public void TrackEnded_AdvancesNaturally_ThenStopsAtTheEndWithRepeatOff()
    {
        var t = Tracks(2);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);

        player.RaiseTrackEnded();
        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(new[] { t[0].FilePath, t[1].FilePath }, player.PlayedPaths);
        Assert.Empty(vm.UpNext);

        player.RaiseTrackEnded();
        Assert.False(vm.IsPlaying);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Same(t[1], vm.CurrentTrack); // stays loaded for the mini bar
    }

    [Fact]
    public void Previous_RestartsAfterThreeSeconds_ElseGoesBack()
    {
        var t = Tracks(2);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);
        vm.NextCommand.Execute(null);
        Assert.Same(t[1], vm.CurrentTrack);

        vm.PreviousCommand.Execute(null);                  // position 0 → back
        Assert.Same(t[0], vm.CurrentTrack);
        Assert.Equal(3, player.PlayedPaths.Count);

        vm.Seek(TimeSpan.FromSeconds(10));
        vm.PreviousCommand.Execute(null);                  // >3 s → restart, no queue change
        Assert.Same(t[0], vm.CurrentTrack);
        Assert.Equal(TimeSpan.Zero, vm.Position);
        Assert.Equal(3, player.PlayedPaths.Count);
    }

    [Fact]
    public async Task SaveAndRestore_ComeBackPausedAtThePosition_AndResumeFromIt()
    {
        var t = Tracks(3);
        var (vm, player, library, persistence) = Make(t);
        vm.PlayTracks(t, 0);
        vm.CycleRepeatCommand.Execute(null);               // Off → All
        vm.Seek(TimeSpan.FromSeconds(42));
        await vm.SaveStateAsync();

        var restored = new NowPlayingViewModel(new FakeAudioPlayer(), library, persistence, marshal: a => a());
        await restored.RestoreStateAsync();

        Assert.Same(t[0], restored.CurrentTrack);
        Assert.False(restored.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(42), restored.Position);
        Assert.Equal(new[] { t[1], t[2] }, restored.UpNext);
        Assert.Equal(RepeatMode.All, restored.RepeatMode);
    }

    [Fact]
    public async Task TogglePlayPause_AfterRestore_PlaysFromTheSavedPosition()
    {
        var t = Tracks(1);
        var (vm, _, library, persistence) = Make(t);
        vm.PlayTracks(t, 0);
        vm.Seek(TimeSpan.FromSeconds(30));
        await vm.SaveStateAsync();

        var player = new FakeAudioPlayer();
        var restored = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        restored.TogglePlayPauseCommand.Execute(null);

        Assert.Equal(30_000, player.PendingSeekMs);
        Assert.Equal(new[] { t[0].FilePath }, player.PlayedPaths);
        Assert.True(restored.IsPlaying);
    }

    [Fact]
    public void QueueEdits_ReprepareTheGaplessNext()
    {
        var t = Tracks(4);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t.Take(2).ToArray(), 0);           // upnext [t1], prepared t1
        vm.PlayNext(t[2]);                                 // upnext [t2, t1]
        Assert.Equal(t[2].FilePath, player.PreparedPaths.Last());
        vm.RemoveFromQueue(0);                             // upnext [t1]
        Assert.Equal(t[1].FilePath, player.PreparedPaths.Last());
        vm.AddToQueue(t[3]);
        Assert.Equal(new[] { t[1], t[3] }, vm.UpNext);
    }

    [Fact]
    public void FiveConsecutiveErrors_StopInsteadOfLooping()
    {
        var t = Tracks(10);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);
        for (int i = 0; i < 5; i++) player.RaisePlaybackError("boom");

        Assert.False(vm.IsPlaying);
        Assert.Equal(5, player.PlayedPaths.Count);         // t0 + four skips, then stop
        Assert.NotEqual(string.Empty, vm.ErrorText);
    }

    [Fact]
    public async Task SaveAndRestore_ShuffleOn_TogglingOffAfterwardsRestoresAlbumOrder()
    {
        var t = Tracks(4);
        var (vm, _, library, persistence) = Make(t);
        vm.PlayTracks(t, 0);
        vm.ToggleShuffleCommand.Execute(null);             // scrambles UpNext, remembers album order
        await vm.SaveStateAsync();

        var restored = new NowPlayingViewModel(new FakeAudioPlayer(), library, persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        Assert.True(restored.IsShuffleEnabled);

        restored.ToggleShuffleCommand.Execute(null);       // off → restores the pre-shuffle order

        Assert.False(restored.IsShuffleEnabled);
        Assert.Equal(new[] { t[1], t[2], t[3] }, restored.UpNext);
    }

    [Fact]
    public void SetGapless_False_CancelsInsteadOfPreparing()
    {
        var t = Tracks(3);
        var (vm, player, _, _) = Make(t);
        vm.SetGapless(false);

        vm.PlayTracks(t, 0);

        Assert.False(player.GaplessEnabled);
        Assert.Empty(player.PreparedPaths);
        Assert.True(player.CancelledCount > 0);
    }

    [Fact]
    public void RepeatOne_PlayTracks_CancelsPreparedNextInsteadOfPreparingIt()
    {
        var t = Tracks(3);
        var (vm, player, _, _) = Make(t);
        vm.CycleRepeatCommand.Execute(null);               // Off → All
        vm.CycleRepeatCommand.Execute(null);               // All → One
        Assert.Equal(RepeatMode.One, vm.RepeatMode);
        // Each CycleRepeat above already ran PrepareUpcoming with no current track, which
        // calls CancelPreparedNext regardless of repeat mode — snapshot after those so the
        // assertion below is actually load-bearing on PlayTracks's own PrepareUpcoming call.
        var cancelledBeforePlay = player.CancelledCount;

        vm.PlayTracks(t, 0);

        Assert.Empty(player.PreparedPaths);
        Assert.True(player.CancelledCount > cancelledBeforePlay);
    }

    [Fact]
    public void FocusLoss_PlayerPausedExternally_SyncsIsPlayingOnTheNextPositionTick()
    {
        var t = Tracks(1);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);
        Assert.True(vm.IsPlaying);

        player.Pause();                                    // audio-focus loss / lock-screen pause, bypassing the VM
        player.RaisePositionChanged(TimeSpan.FromSeconds(1));

        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void SeekDrag_SuspendsThePositionPush_UntilTheDragEnds()
    {
        // The drag itself cannot be simulated headlessly, so this pins the mechanism the
        // pointer handlers drive: while a seek is in progress the 4 Hz position tick must not
        // move Position, because that value is bound into the Slider and would overwrite what
        // the user's finger put there. Everything else the tick does must keep working.
        var t = Tracks(1);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);

        vm.BeginSeek();
        player.RaisePositionChanged(TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.Zero, vm.Position);

        // ...while the rest of the tick still runs: an external pause mid-drag is still caught.
        player.Pause();
        player.RaisePositionChanged(TimeSpan.FromSeconds(13));
        Assert.False(vm.IsPlaying);

        vm.EndSeek();
        player.Resume();
        player.RaisePositionChanged(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(20), vm.Position);
    }

    /// <summary>
    /// Android device run, 2026-09-22: a drag abandoned by pulling the notification shade
    /// down over it delivers no release, no capture-lost and no further pointer event at all,
    /// so nothing called EndSeek and the elapsed label, the thumb and the saved resume
    /// position stayed frozen at 0 for as long as the app was watched. A held seek must
    /// therefore time itself out on the position tick.
    ///
    /// The window is 120 ticks — thirty seconds at the player's 4 Hz poll — and the length is
    /// the point, not an accident. Firing during a gesture the user is still making re-creates
    /// the bug BeginSeek exists to prevent: the same tick that clears _seeking then pushes live
    /// playback position into the Slider's Value, so the thumb jumps out from under the finger.
    /// Android emits no ACTION_MOVE for a stationary finger, so press-and-hold while deciding
    /// produces no keepalive at all — which is why the window has to be longer than any real
    /// gesture rather than merely longer than a drag's tick gaps. What the watchdog owes the
    /// original defect is that the freeze ends unattended, not that it ends quickly.
    /// </summary>
    [Fact]
    public void AbandonedSeek_SelfCancels_ButOnlyLongAfterAnyRealGestureCouldStillBeRunning()
    {
        var t = Tracks(1);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);

        // A long but live drag: the finger keeps moving, so the freeze holds indefinitely.
        vm.BeginSeek();
        for (var i = 0; i < 400; i++)
        {
            if (i % 4 == 0) vm.KeepSeekAlive();
            player.RaisePositionChanged(TimeSpan.FromSeconds(1 + i));
        }
        Assert.Equal(TimeSpan.Zero, vm.Position);

        // Same drag, now held perfectly still — no PointerMoved, so no keepalive. Twenty-five
        // seconds of that is still a gesture as far as this VM is concerned.
        vm.KeepSeekAlive();
        for (var i = 0; i < 100; i++) player.RaisePositionChanged(TimeSpan.FromSeconds(500 + i));
        Assert.Equal(TimeSpan.Zero, vm.Position);

        // Past thirty seconds the watchdog releases the seek, so the display and the saved
        // resume position start tracking playback again without the user touching anything.
        for (var i = 0; i < 21; i++) player.RaisePositionChanged(TimeSpan.FromSeconds(600 + i));
        Assert.Equal(TimeSpan.FromSeconds(620), vm.Position);
    }

    /// <summary>
    /// The watchdog is armed only while the engine is actually advancing. An external pause
    /// (lock-screen, headphone unplug, audio-focus loss) deliberately keeps the 4 Hz tick
    /// running, so without this a careful scrub made while paused-by-lock-screen would be on
    /// the same clock — with nothing playing, so with nothing to un-freeze and no upside to
    /// cutting the gesture short.
    /// </summary>
    [Fact]
    public void SeekWhileExternallyPaused_IsNeverCutShortByTheWatchdog()
    {
        var t = Tracks(1);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);
        vm.Seek(TimeSpan.FromSeconds(5));
        player.Pause();                                    // lock-screen pause, bypassing the VM

        vm.BeginSeek();
        for (var i = 0; i < 400; i++) player.RaisePositionChanged(TimeSpan.FromSeconds(50 + i));

        Assert.Equal(TimeSpan.FromSeconds(5), vm.Position); // the thumb stayed where the drag put it
        Assert.False(vm.IsPlaying);                        // ...and the rest of the tick still ran
    }

    /// <summary>
    /// The periodic tick checkpoints the position only. Tapping one song queues the whole song
    /// list, and PlaybackQueue also records the full repeat cycle and the pre-shuffle order, so
    /// a QueueState is roughly 3N GUIDs — hundreds of KB for a real library, fsync'd, and on the
    /// old code rewritten every five seconds for as long as music played (~260 MB/hour onto
    /// phone flash) even though nothing in it had changed. Every genuine mutator saves the queue
    /// the moment it happens, so between them there is nothing new to write but the position.
    /// </summary>
    [Fact]
    public async Task PeriodicTick_CheckpointsThePositionWithoutRewritingTheUnchangedQueue()
    {
        var t = Tracks(3);
        var (vm, player, library, persistence) = Make(t);
        vm.PlayTracks(t, 0);                               // a real mutation: writes queue.json
        // PlayTracks saves fire-and-forget; this one goes through the same per-file write gate,
        // so awaiting it settles that write too and leaves no queue save in flight to confuse
        // the byte/mtime comparison below.
        await vm.SaveStateAsync();

        var queueFile = Path.Combine(_root, "queue.json");
        var beforeBytes = File.ReadAllBytes(queueFile);
        var beforeWrite = File.GetLastWriteTimeUtc(queueFile);

        // Six seconds of playback, position moving, queue untouched.
        ForcePeriodicSaveDue(vm);
        player.RaisePositionChanged(TimeSpan.FromSeconds(6));
        await WaitForAsync(() => File.Exists(Path.Combine(_root, "queue-position.json")));

        Assert.Equal(beforeBytes, File.ReadAllBytes(queueFile));
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(queueFile));

        // ...and the five-second resume guarantee still holds: the checkpoint is folded back in.
        var restored = new NowPlayingViewModel(new FakeAudioPlayer(), library, persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        Assert.Same(t[0], restored.CurrentTrack);
        Assert.Equal(new[] { t[1], t[2] }, restored.UpNext);
        Assert.Equal(TimeSpan.FromSeconds(6), restored.Position);
    }

    /// <summary>A checkpoint left over from the previous track must not be applied to the new
    /// one — the queue save that changed tracks supersedes it.</summary>
    [Fact]
    public async Task ATrackChange_SupersedesTheStalePositionCheckpoint()
    {
        var t = Tracks(2);
        var (vm, player, library, persistence) = Make(t);
        vm.PlayTracks(t, 0);
        await vm.SaveStateAsync();
        ForcePeriodicSaveDue(vm);
        player.RaisePositionChanged(TimeSpan.FromSeconds(90));
        await WaitForAsync(() => File.Exists(Path.Combine(_root, "queue-position.json")));

        vm.NextCommand.Execute(null);                      // writes queue.json at position 0
        // The checkpoint is dropped synchronously, before the new queue is written, so it is
        // already gone the moment the command returns — no polling needed, and nothing that
        // was already in flight can put it back.
        Assert.False(File.Exists(Path.Combine(_root, "queue-position.json")));
        await vm.SaveStateAsync();                         // settle the queue write read back below

        var restored = new NowPlayingViewModel(new FakeAudioPlayer(), library, persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        Assert.Same(t[1], restored.CurrentTrack);
        Assert.Equal(TimeSpan.Zero, restored.Position);
    }

    /// <summary>
    /// PersistenceService-level cover for the track-id guard in LoadQueueStateAsync. The two
    /// tests above only reach it through the ViewModel's own save, which deletes the checkpoint
    /// first — so the guard never actually runs in either of them. This drives the checkpoint
    /// straight through PersistenceService instead, for a track other than the queue's current
    /// one, and the restored position must stay the queue's own.
    /// </summary>
    [Fact]
    public async Task LoadQueueStateAsync_DiscardsTheCheckpoint_WhenItsTrackDoesNotMatchTheQueue()
    {
        var t = Tracks(2);
        var persistence = new PersistenceService(_root);
        await persistence.SaveQueueStateAsync(new QueueState
        {
            CurrentTrackId = t[0].Id,
            PositionSeconds = 10,
            UpNextIds = { t[1].Id }
        });
        await persistence.SaveQueuePositionAsync(t[1].Id, 999); // checkpoint for a DIFFERENT track

        var loaded = await persistence.LoadQueueStateAsync();

        Assert.NotNull(loaded);
        Assert.Equal(t[0].Id, loaded!.CurrentTrackId);
        Assert.Equal(10, loaded.PositionSeconds);                // the queue's own position, untouched
    }

    /// <summary>The positive half of the guard above: a checkpoint for the SAME track is folded
    /// in, proving the guard discriminates rather than simply ignoring the checkpoint file.</summary>
    [Fact]
    public async Task LoadQueueStateAsync_AppliesTheCheckpoint_WhenItsTrackMatchesTheQueue()
    {
        var t = Tracks(2);
        var persistence = new PersistenceService(_root);
        await persistence.SaveQueueStateAsync(new QueueState
        {
            CurrentTrackId = t[0].Id,
            PositionSeconds = 10,
            UpNextIds = { t[1].Id }
        });
        await persistence.SaveQueuePositionAsync(t[0].Id, 42);  // checkpoint for the SAME track

        var loaded = await persistence.LoadQueueStateAsync();

        Assert.NotNull(loaded);
        Assert.Equal(t[0].Id, loaded!.CurrentTrackId);
        Assert.Equal(42, loaded.PositionSeconds);                // folded in from the checkpoint
    }

    // The save cadence is wall-clock, so drive the clock rather than sleeping five seconds.
    private static void ForcePeriodicSaveDue(NowPlayingViewModel vm) =>
        typeof(NowPlayingViewModel).GetField("_lastSaveUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, DateTime.MinValue);

    // The periodic position checkpoint is fire-and-forget onto the thread pool, with no handle
    // to await, and under the full parallel suite that pool can be busy enough to delay one by
    // seconds — so poll to a generous deadline rather than guess a fixed delay.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(condition());
    }

    [Fact]
    public void QueueAvailability_DrivesTheNotificationTransportButtons()
    {
        // These two feed Media3's HasNext/HasPreviousMediaItem, which decide whether the
        // notification draws Next/Previous enabled. ExoPlayer's own item list is the wrong
        // source, so they are computed from the app queue instead — pinned here.
        var t = Tracks(2);
        var (vm, _, _, _) = Make(t);

        Assert.False(vm.HasNext);                          // stopped, empty queue
        Assert.False(vm.HasPrevious);

        vm.PlayTracks(t, 0);
        Assert.True(vm.HasNext);                           // t[1] is up next
        Assert.True(vm.HasPrevious);                       // Previous restarts the track

        vm.NextCommand.Execute(null);
        Assert.Empty(vm.UpNext);
        Assert.False(vm.HasNext);                          // last track, repeat off
        Assert.True(vm.HasPrevious);                       // history has t[0]

        vm.CycleRepeatCommand.Execute(null);               // Off -> All
        Assert.Equal(RepeatMode.All, vm.RepeatMode);
        // The widening this task added: Repeat All wraps to the recorded cycle even with
        // UpNext empty, so the notification's Next must stay enabled on the last track.
        Assert.True(vm.HasNext);

        vm.CycleRepeatCommand.Execute(null);               // All -> One
        Assert.Equal(RepeatMode.One, vm.RepeatMode);
        Assert.False(vm.HasNext);                          // One does not wrap on a user skip
    }
    [Fact]
    public void APlayIsRecorded_OnlyOnceItsAudioAdvances_SoAFailedTrackIsNot()
    {
        var t = Tracks(2);
        var history = new FakeHistoryLog();
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(t);
        var vm = new NowPlayingViewModel(player, library, new PersistenceService(_root), history, marshal: a => a());

        vm.PlayTracks(t, 0);
        player.RaisePositionChanged(TimeSpan.Zero);          // the poll before any audio: not yet a play
        Assert.Empty(history.Events);

        player.RaisePlaybackError("ERROR_CODE_PARSING_CONTAINER_MALFORMED: broken");   // t0 never played
        Assert.Same(t[1], vm.CurrentTrack);
        player.RaisePositionChanged(TimeSpan.FromSeconds(0.5));
        player.RaisePositionChanged(TimeSpan.FromSeconds(0.75));   // one play, not one per tick

        Assert.Equal(new[] { t[1].Id }, history.Events.Select(e => e.TrackId));
    }

    [Fact]
    public void DropTracks_StopsWhenTheCurrentTrackGoes_AndPrunesUpNext()
    {
        var t = Tracks(4);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);

        vm.DropTracks(x => x == t[0] || x == t[2]);

        Assert.Null(vm.CurrentTrack);
        Assert.False(vm.IsPlaying);
        Assert.Equal(new[] { t[1], t[3] }, vm.UpNext);
    }

    [Fact]
    public void DropTracks_LeavesAKeptCurrentTrackPlaying()
    {
        var t = Tracks(3);
        var (vm, player, _, _) = Make(t);
        vm.PlayTracks(t, 0);

        vm.DropTracks(x => x == t[1]);

        Assert.Same(t[0], vm.CurrentTrack);
        Assert.True(vm.IsPlaying);
        Assert.Equal(new[] { t[2] }, vm.UpNext);
    }

    [Fact]
    public void LibraryUpdate_DropsDesktopSongsThatLeftTheLibrary_FromTheQueue()
    {
        var t = Tracks(4);
        t[0].SourceType = SourceType.NoctisServer;   // playing, then signed out
        t[2].SourceType = SourceType.NoctisServer;   // queued, signed out
        t[3].SourceType = SourceType.NoctisServer;   // queued, still in the library
        var (vm, player, library, _) = Make(t);
        vm.PlayTracks(t, 0);

        library.TrackList.Remove(t[0]);
        library.TrackList.Remove(t[2]);
        library.RaiseLibraryUpdated();

        Assert.Null(vm.CurrentTrack);
        Assert.False(vm.IsPlaying);
        Assert.Equal(new[] { t[1], t[3] }, vm.UpNext);
    }
}
