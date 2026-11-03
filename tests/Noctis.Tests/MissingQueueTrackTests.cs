using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #154: a queue holding songs whose files were moved or deleted stopped on the
/// first missing one (its accent still showing) instead of going on to the next song that
/// can play. Missing songs are now passed over in the direction of travel, with a notice,
/// and either kept (shown as missing) or taken off the queue.
/// </summary>
public class MissingQueueTrackTests
{
    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = $"C:/t/{name}.mp3",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static (PlayerViewModel Vm, FakeAudioPlayer Player, FakeLibraryService Library) Create()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player, library);
    }

    private static void Run() => Dispatcher.UIThread.RunJobs();

    private static void EndTrack(FakeAudioPlayer player)
    {
        player.RaisePositionChanged(TimeSpan.FromSeconds(1)); // it really played
        Run();
        player.RaiseTrackEnded();
        Run();
    }

    [AvaloniaFact]
    public void NaturalEnd_PassesOverAWholeMovedAlbum_ToTheNextPlayableSong()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var missing = Enumerable.Range(0, 6).Select(i => Trk("m" + i)).ToList();
        var b = Trk("b");
        var queue = new List<Track> { a };
        queue.AddRange(missing);
        queue.Add(b);
        library.TrackList.AddRange(queue);
        foreach (var m in missing) player.MissingPaths.Add(m.FilePath);

        vm.ReplaceQueueAndPlay(queue, 0);
        EndTrack(player);

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(new[] { a.FilePath, b.FilePath }, player.PlayedPaths);
    }

    [AvaloniaFact]
    public void MissingLastSong_WithRepeatAll_WrapsToTheFirst()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var b = Trk("b");
        var m = Trk("m");
        library.TrackList.AddRange(new[] { a, b, m });
        player.MissingPaths.Add(m.FilePath);
        vm.RepeatMode = RepeatMode.All;

        vm.ReplaceQueueAndPlay(new List<Track> { a, b, m }, 0);
        EndTrack(player);
        Assert.Same(b, vm.CurrentTrack);
        EndTrack(player);

        Assert.Same(a, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(new[] { a.FilePath, b.FilePath, a.FilePath }, player.PlayedPaths);
    }

    [AvaloniaFact]
    public void EverySongMissing_StopsOnTheFirst_WithTheQueueKept_AndANotice()
    {
        var (vm, player, library) = Create();
        var m = Enumerable.Range(0, 4).Select(i => Trk("m" + i)).ToList();
        library.TrackList.AddRange(m);
        foreach (var t in m) player.MissingPaths.Add(t.FilePath);
        vm.RepeatMode = RepeatMode.All; // the wrap must not loop forever
        var notices = new List<string>();
        vm.MissingTracksNotice += (_, text) => notices.Add(text);

        vm.ReplaceQueueAndPlay(m, 0);
        Run();

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Same(m[0], vm.CurrentTrack);
        Assert.Equal(m.Skip(1), vm.UpNext);
        Assert.Empty(player.PlayedPaths);
        Assert.Empty(player.MissingPlayAttempts); // never handed to the engine
        Assert.Equal(new[] { PlayerViewModel.DescribeMissingRun(4, "m0", removed: false, stopped: true) }, notices);
        Assert.All(m, t => Assert.True(t.IsFileMissing));

        // The drive comes back: Play starts where it stopped.
        player.MissingPaths.Clear();
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(new[] { m[0].FilePath }, player.PlayedPaths);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.False(m[0].IsFileMissing);
    }

    [AvaloniaFact]
    public void RepeatAll_PlayingSongDeletedToo_StopsAfterOneWrap()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var m = Trk("m");
        library.TrackList.AddRange(new[] { a, m });
        player.MissingPaths.Add(m.FilePath);
        vm.RepeatMode = RepeatMode.All;
        vm.ReplaceQueueAndPlay(new List<Track> { a, m }, 0);

        player.MissingPaths.Add(a.FilePath); // deleted while it plays
        EndTrack(player);

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(new[] { a.FilePath }, player.PlayedPaths);
        Assert.Same(m, vm.CurrentTrack);
    }

    [AvaloniaFact]
    public void MarkAsMissing_KeepsTheSongInThePlayOrder_AndNamesIt()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var m = Trk("m");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, m, b });
        player.MissingPaths.Add(m.FilePath);
        var notices = new List<string>();
        vm.MissingTracksNotice += (_, text) => notices.Add(text);
        vm.ReplaceQueueAndPlay(new List<Track> { a, m, b }, 0);

        EndTrack(player);

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(new[] { m, a }, vm.History); // passed over like a played song
        Assert.True(m.IsFileMissing);
        Assert.Equal(new[] { PlayerViewModel.DescribeMissingRun(1, "m", removed: false, stopped: false) }, notices);
        Assert.Contains("m", notices[0]);
    }

    [AvaloniaFact]
    public void Previous_StepsBackOverAMissingSong()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var m = Trk("m");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, m, b });
        vm.ReplaceQueueAndPlay(new List<Track> { a, m, b }, 0);
        EndTrack(player);              // m still there: plays
        EndTrack(player);              // b
        Assert.Same(b, vm.CurrentTrack);

        player.MissingPaths.Add(m.FilePath);
        vm.PreviousCommand.Execute(null);

        Assert.Same(a, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(new[] { m, b }, vm.UpNext);
        Assert.Equal(new[] { a.FilePath, m.FilePath, b.FilePath, a.FilePath }, player.PlayedPaths);
    }

    [AvaloniaFact]
    public void Previous_WithNothingPlayableBehind_TurnsRoundToTheSongItWasPressedOn()
    {
        var (vm, player, library) = Create();
        var m = Trk("m");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { m, b });
        vm.ReplaceQueueAndPlay(new List<Track> { m, b }, 1); // m above the start
        player.MissingPaths.Add(m.FilePath);

        vm.PreviousCommand.Execute(null);

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(new[] { b.FilePath, b.FilePath }, player.PlayedPaths);
    }

    [AvaloniaFact]
    public void StartingOnAMissingSong_PlaysTheNextOne()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var m = Trk("m");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, m, b });
        player.MissingPaths.Add(m.FilePath);

        vm.ReplaceQueueAndPlay(new List<Track> { a, m, b }, 1); // double-click on m

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(new[] { b.FilePath }, player.PlayedPaths);
    }

    [AvaloniaFact]
    public void JumpingToAMissingQueueRow_PlaysTheRowAfterIt()
    {
        var (vm, player, library) = Create();
        var t = Enumerable.Range(0, 4).Select(i => Trk("t" + i)).ToList();
        library.TrackList.AddRange(t);
        vm.ReplaceQueueAndPlay(t, 0);
        player.MissingPaths.Add(t[2].FilePath);

        vm.PlayFromUpNextAt(1); // t2

        Assert.Same(t[3], vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
    }

    [AvaloniaFact]
    public void NextPress_PassesOverMissingSongs()
    {
        var (vm, player, library) = Create();
        var t = Enumerable.Range(0, 4).Select(i => Trk("t" + i)).ToList();
        library.TrackList.AddRange(t);
        player.MissingPaths.Add(t[1].FilePath);
        player.MissingPaths.Add(t[2].FilePath);
        vm.ReplaceQueueAndPlay(t, 0);

        vm.NextCommand.Execute(null);

        Assert.Same(t[3], vm.CurrentTrack);
        Assert.Equal(new[] { t[0].FilePath, t[3].FilePath }, player.PlayedPaths);
    }

    [AvaloniaFact]
    public void RepeatOne_OnASongDeletedWhilePlaying_MovesOn()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, b });
        vm.RepeatMode = RepeatMode.One;
        vm.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);
        player.MissingPaths.Add(a.FilePath);

        EndTrack(player);

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    [AvaloniaFact]
    public void RemoveFromQueue_TakesTheMissingSongOffTheQueueAndTheRepeatCycle()
    {
        var (vm, player, library) = Create();
        var a = Trk("a");
        var m = Trk("m");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, m, b });
        player.MissingPaths.Add(m.FilePath);
        vm.RemoveMissingFromQueue = true;
        vm.RepeatMode = RepeatMode.All;
        var notices = new List<string>();
        vm.MissingTracksNotice += (_, text) => notices.Add(text);
        vm.ReplaceQueueAndPlay(new List<Track> { a, m, b }, 0);

        EndTrack(player);
        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(new[] { a }, vm.History);
        Assert.Equal(new[] { PlayerViewModel.DescribeMissingRun(1, "m", removed: true, stopped: false) }, notices);

        EndTrack(player); // wrap: the next pass has no m in it
        Assert.Same(a, vm.CurrentTrack);
        Assert.Equal(new[] { b }, vm.UpNext);
        Assert.Single(notices);
    }

    [AvaloniaFact]
    public void RemoveFromQueue_KeepsSongsWhoseDriveIsNotThere()
    {
        var root = "ZYXWVUTSRQPONMLKJIHGFED".Select(c => c + ":" + Path.DirectorySeparatorChar)
            .FirstOrDefault(r => !Directory.Exists(r));
        Assert.NotNull(root);
        var (vm, player, library) = Create();
        var a = Trk("a");
        var gone = new Track { Id = Guid.NewGuid(), Title = "gone", FilePath = root + "gone.mp3", Duration = TimeSpan.FromMinutes(3) };
        library.TrackList.AddRange(new[] { a, gone });
        player.MissingPaths.Add(gone.FilePath);
        vm.RemoveMissingFromQueue = true;
        vm.ReplaceQueueAndPlay(new List<Track> { a, gone }, 0);

        EndTrack(player);

        // Nothing left to play: stopped on it, still queued, for when the drive is back.
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Same(gone, vm.CurrentTrack);
    }

    [AvaloniaFact]
    public void RemoveFromQueue_EverySongGone_EmptiesThePlayer()
    {
        var (vm, player, library) = Create();
        var m = Enumerable.Range(0, 3).Select(i => Trk("m" + i)).ToList();
        library.TrackList.AddRange(m);
        foreach (var t in m) player.MissingPaths.Add(t.FilePath);
        vm.RemoveMissingFromQueue = true;

        vm.ReplaceQueueAndPlay(m, 0);

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Null(vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
    }

    [AvaloniaFact]
    public async Task QueueMarks_FollowTheFiles_BothWays()
    {
        var (vm, player, library) = Create();
        var t = Enumerable.Range(0, 3).Select(i => Trk("t" + i)).ToList();
        library.TrackList.AddRange(t);
        vm.ReplaceQueueAndPlay(t, 0);
        player.MissingPaths.Add(t[2].FilePath);

        await vm.RefreshMissingMarksAsync();
        Run();
        Assert.True(t[2].IsFileMissing);
        Assert.False(t[1].IsFileMissing);

        player.MissingPaths.Clear(); // drive reconnected
        await vm.RefreshMissingMarksAsync();
        Run();
        Assert.False(t[2].IsFileMissing);
    }
}
