using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #188 "Cleared song re-appears in queue": after Clear Queue (#175) the playing song
/// is off the queue. Songs queued while it plays must not bring it back — not as the Now
/// Playing card or row, and not into the Repeat All cycle: when it ends playback carries on
/// with what was queued, and Repeat All cycles only that.
/// </summary>
public class QueueClearedSongIssue188Tests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm(bool keepPlayed = false)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            KeepPlayedInQueue = keepPlayed
        };
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

    private static Track[] Album(params string[] names) => names.Select(Trk).ToArray();

    private static void End(FakeAudioPlayer player, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            player.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Titles(IEnumerable<Track> tracks) => string.Join(",", tracks.Select(t => t.Title));

    /// <summary>Plays <paramref name="names"/> from the first, then clears the queue.</summary>
    private static Track[] PlayAndClear(PlayerViewModel vm, params string[] names)
    {
        var album = Album(names);
        vm.ReplaceQueueAndPlay(album, 0);
        vm.ClearQueue();
        Dispatcher.UIThread.RunJobs();
        return album;
    }

    // ── The cleared song stays off the queue ──

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingAnAlbum_AfterClear_DoesNotBringThePlayingSongBack(bool keepPlayed)
    {
        var (vm, player) = CreateVm(keepPlayed);
        var cleared = PlayAndClear(vm, "a", "b", "c");
        var plays = player.PlayedPaths.Count;

        vm.AddRangeToQueue(Album("x", "y"), announce: false);
        Dispatcher.UIThread.RunJobs();

        // Still playing, untouched, and still nowhere in the queue.
        Assert.Same(cleared[0], vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(plays, player.PlayedPaths.Count);
        Assert.False(vm.ShowQueueCurrentTrack);
        Assert.Equal(-1, vm.QueueCurrentRow);
        Assert.Equal("x,y", Titles(vm.QueueRows));
        Assert.Equal(0, vm.QueueRowToUpNextIndex(0)); // row 0 is Up Next's first song
        Assert.Equal(QueueRowKind.Upcoming, vm.GetQueueRowKind(0));
        Assert.True(vm.CanClearQueue);
    }

    [AvaloniaFact]
    public void AddToQueue_And_PlayNext_AfterClear_KeepItOff_InQueueOrder()
    {
        var (vm, _) = CreateVm();
        PlayAndClear(vm, "a", "b");

        vm.AddToQueue(Trk("x"), announce: false);
        vm.AddNext(Trk("y"), announce: false);
        vm.AddNextRange(Album("p", "q"), announce: false);

        Assert.False(vm.ShowQueueCurrentTrack);
        Assert.Equal("p,q,y,x", Titles(vm.QueueRows));
    }

    [AvaloniaFact]
    public void ClearedSongEnds_PlaybackCarriesOnWithTheFirstQueuedSong()
    {
        var (vm, player) = CreateVm();
        PlayAndClear(vm, "a", "b");
        var added = Album("x", "y");
        vm.AddRangeToQueue(added, announce: false);

        End(player);

        Assert.Same(added[0], vm.CurrentTrack);
        Assert.True(vm.ShowQueueCurrentTrack); // the queue's own song: Now Playing again
        Assert.Equal("y", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void Next_AfterRefill_GoesToTheFirstQueuedSong()
    {
        var (vm, _) = CreateVm();
        PlayAndClear(vm, "a", "b");
        var x = Trk("x");
        vm.AddToQueue(x, announce: false);

        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);

        Assert.Same(x, vm.CurrentTrack);
    }

    // ── Repeat All: only the visible queue ──

    [AvaloniaFact]
    public void RepeatAll_AfterRefill_CyclesOnlyTheQueuedSongs()
    {
        var (vm, player) = CreateVm();
        PlayAndClear(vm, "a", "b", "c");
        vm.RepeatMode = RepeatMode.All;
        vm.AddRangeToQueue(Album("x", "y"), announce: false);

        var seen = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }

        Assert.Equal("x,y,x,y,x", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void RepeatAll_PlayNextAfterClear_CyclesInQueueOrder()
    {
        var (vm, player) = CreateVm();
        PlayAndClear(vm, "a", "b");
        vm.RepeatMode = RepeatMode.All;
        vm.AddToQueue(Trk("x"), announce: false);
        vm.AddNext(Trk("y"), announce: false);

        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }

        Assert.Equal("y,x,y,x", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void RepeatAll_QueuedThenRemovedAgain_ReplaysTheClearedSongAlone()
    {
        // Nothing queued after the clear in the end: #175's "Repeat replays it" holds.
        var (vm, player) = CreateVm();
        var cleared = PlayAndClear(vm, "a", "b");
        vm.RepeatMode = RepeatMode.All;
        vm.AddToQueue(Trk("x"), announce: false);
        vm.RemoveFromQueue(0);
        var plays = player.PlayedPaths.Count;

        End(player, 2);

        Assert.Same(cleared[0], vm.CurrentTrack);
        Assert.Equal(plays + 2, player.PlayedPaths.Count);
        Assert.All(player.PlayedPaths.Skip(plays), p => Assert.Equal(cleared[0].FilePath, p));
        Assert.False(vm.ShowQueueCurrentTrack);
    }

    [AvaloniaFact]
    public void RepeatAll_ClearedSongReplaying_DoesNotPileUpPlayedRows()
    {
        var (vm, player) = CreateVm(keepPlayed: true);
        PlayAndClear(vm, "a", "b");
        vm.RepeatMode = RepeatMode.All;

        End(player, 3);

        Assert.Empty(vm.QueueRows);
        Assert.True(vm.IsQueueEmpty);
    }

    // ── Repeat One: the playing song, on or off the queue ──

    [AvaloniaFact]
    public void RepeatOne_AfterRefill_ReplaysTheClearedSong_NextMovesOn()
    {
        var (vm, player) = CreateVm();
        var cleared = PlayAndClear(vm, "a", "b");
        vm.RepeatMode = RepeatMode.One;
        var x = Trk("x");
        vm.AddToQueue(x, announce: false);

        End(player);
        Assert.Same(cleared[0], vm.CurrentTrack); // Repeat One repeats what is playing
        Assert.False(vm.ShowQueueCurrentTrack);

        vm.NextCommand.Execute(null);
        Assert.Same(x, vm.CurrentTrack);
    }

    // ── Other ways of filling Up Next ──

    [AvaloniaFact]
    public void ReplaceUpNext_AfterClear_CycleLeavesTheClearedSongOut()
    {
        var (vm, player) = CreateVm();
        PlayAndClear(vm, "a", "b");
        vm.RepeatMode = RepeatMode.All;

        vm.ReplaceUpNext(Album("x", "y"));

        Assert.False(vm.ShowQueueCurrentTrack);
        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }
        Assert.Equal("x,y,x,y", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void PlayAlbum_AfterClear_StartsTheAlbum()
    {
        var (vm, _) = CreateVm();
        PlayAndClear(vm, "a", "b");
        vm.AddToQueue(Trk("x"), announce: false);

        vm.ReplaceQueueAndPlay(Album("p", "q"), 0);

        Assert.Equal("p", vm.CurrentTrack?.Title);
        Assert.True(vm.ShowQueueCurrentTrack);
        Assert.Equal("q", Titles(vm.QueueRows));
    }

    // ── Restart ──

    [AvaloniaFact]
    public async Task Restore_AfterClearAndRefill_ClearedSongStaysOff_AndOutOfTheCycle()
    {
        var lib = new FakeLibraryService();
        var album = Album("a", "b");
        var added = Album("x", "y");
        lib.TrackList.AddRange(album);
        lib.TrackList.AddRange(added);
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, lib, persistence, new FakeAnimatedCoverService());
        vm.ReplaceQueueAndPlay(album, 0);
        vm.RepeatMode = RepeatMode.All;
        vm.ClearQueue();
        vm.AddRangeToQueue(added, announce: false);
        await Task.Delay(300); // let the track-change background snapshot land first
        await vm.SaveQueueStateAsync();

        var player2 = new FakeAudioPlayer();
        var restored = new PlayerViewModel(player2, lib, persistence, new FakeAnimatedCoverService());
        await restored.RestoreQueueStateAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(album[0], restored.CurrentTrack);
        Assert.False(restored.ShowQueueCurrentTrack);
        Assert.Equal("x,y", Titles(restored.QueueRows));
        restored.PlayPauseCommand.Execute(null);
        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            End(player2);
            seen.Add(restored.CurrentTrack!.Title);
        }
        Assert.Equal("x,y,x,y", string.Join(",", seen));
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
