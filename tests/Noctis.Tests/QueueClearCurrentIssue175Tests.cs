using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #175 "Queue: Also clear currently playing song": Clear Queue takes the playing
/// song out of the queue too. It plays on, the panel shows the queue empty, and unless
/// something is queued after it, Repeat replays that song alone.
/// </summary>
public class QueueClearCurrentIssue175Tests
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

    private static Track Trk(string name, bool skipWhenShuffling = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
        SkipWhenShuffling = skipWhenShuffling
    };

    private static Track[] Album(params string[] names) => names.Select(n => Trk(n)).ToArray();

    private static void End(FakeAudioPlayer player, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            player.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Titles(IEnumerable<Track> tracks) => string.Join(",", tracks.Select(t => t.Title));

    // ── The clear itself ──

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearQueue_PlayingSongPlaysOn_ButTheQueueShowsEmpty(bool keepPlayed)
    {
        var (vm, player) = CreateVm(keepPlayed);
        var album = Album("a", "b", "c", "d");
        vm.ReplaceQueueAndPlay(album, 1);
        var plays = player.PlayedPaths.Count;
        var seeks = player.Seeks.Count;

        vm.ClearQueue();
        Dispatcher.UIThread.RunJobs();

        // Playback untouched: same song, still playing, nothing re-opened or seeked.
        Assert.Same(album[1], vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(plays, player.PlayedPaths.Count);
        Assert.Equal(seeks, player.Seeks.Count);
        // The queue: no Up Next, no Now Playing card or row.
        Assert.Empty(vm.UpNext);
        Assert.False(vm.ShowQueueCurrentTrack);
        Assert.Equal(-1, vm.QueueCurrentRow);
        Assert.DoesNotContain(album[1], vm.QueueRows);
        Assert.False(vm.ShowQueueNothingUpNext);
        Assert.False(vm.CanClearQueue);
        // Nothing played yet in this queue: Keep Played Songs has no played rows either.
        Assert.True(vm.IsQueueEmpty);
        Assert.Empty(vm.QueueRows);
    }

    [AvaloniaFact]
    public void ClearQueue_KeepPlayed_PlayedRowsStay_PlayingRowGoes()
    {
        var (vm, player) = CreateVm(keepPlayed: true);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player); // a played, b playing

        vm.ClearQueue();

        Assert.Equal("a", Titles(vm.QueueRows)); // Clear Played clears these
        Assert.Equal(QueueRowKind.Played, vm.GetQueueRowKind(0));
        Assert.Equal("b", vm.CurrentTrack?.Title);
    }

    [AvaloniaFact]
    public void ClearButton_IsEnabled_ForTheLastSongAlone()
    {
        var (vm, _) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a"), 0);

        Assert.Empty(vm.UpNext);
        Assert.True(vm.CanClearQueue); // was bound to Up Next's count: dimmed here
        vm.ClearQueue();
        Assert.False(vm.CanClearQueue);
    }

    /// <summary>The Queue page's Clear followed Up Next's count, so with the last song alone
    /// playing it was hidden there while the queue popup's Clear could clear it.</summary>
    [AvaloniaFact]
    public void QueuePage_ClearButton_ShowsForTheLastSongAlone()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var (vm, _) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a"), 0);
        var page = new Noctis.Views.QueueView { DataContext = new QueueViewModel(vm) };
        var window = new Avalonia.Controls.Window { Width = 1280, Height = 900, Content = page };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var clear = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(page)
                .OfType<Avalonia.Controls.Button>()
                .Single(b => b.Command == ((QueueViewModel)page.DataContext!).ClearQueueCommand);

            Assert.Empty(vm.UpNext);
            Assert.True(clear.IsVisible);

            clear.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(clear.IsVisible);
        }
        finally { window.Close(); }
    }

    // ── The song ending ──

    [AvaloniaFact]
    public void RepeatOff_SongEnds_QueueEndsAsUsual()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        vm.ClearQueue();

        End(player);

        Assert.Null(vm.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Empty(vm.UpNext);
    }

    [AvaloniaFact]
    public void RepeatOff_KeepPlayed_SongEnds_StaysLoadedStopped_StillOutOfTheQueue()
    {
        var (vm, player) = CreateVm(keepPlayed: true);
        var album = Album("a", "b");
        vm.ReplaceQueueAndPlay(album, 0);
        vm.ClearQueue();

        End(player);

        Assert.Same(album[0], vm.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Empty(vm.QueueRows);
    }

    [AvaloniaFact]
    public void RepeatAll_ClearedMidAlbum_ReplaysOnlyThePlayingSong()
    {
        var (vm, player) = CreateVm();
        var album = Album("a", "b", "c", "d");
        vm.ReplaceQueueAndPlay(album, 0);
        vm.RepeatMode = RepeatMode.All;
        End(player, 2); // a, b played; c playing

        vm.ClearQueue();
        var plays = player.PlayedPaths.Count;
        End(player, 3);

        // The cycle kept a and b (played before the clear): the wrap went back to a.
        Assert.Same(album[2], vm.CurrentTrack);
        Assert.Equal(plays + 3, player.PlayedPaths.Count);
        Assert.All(player.PlayedPaths.Skip(plays), p => Assert.Equal(album[2].FilePath, p));
        Assert.Empty(vm.UpNext);
        Assert.False(vm.ShowQueueCurrentTrack); // still visibly empty while it repeats
    }

    [AvaloniaFact]
    public void RepeatOne_SongEnds_Replays()
    {
        var (vm, player) = CreateVm();
        var album = Album("a", "b");
        vm.ReplaceQueueAndPlay(album, 0);
        vm.RepeatMode = RepeatMode.One;
        vm.ClearQueue();
        var plays = player.PlayedPaths.Count;

        End(player);

        Assert.Same(album[0], vm.CurrentTrack);
        Assert.Equal(plays + 1, player.PlayedPaths.Count);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    // ── Next / Previous ──

    [AvaloniaTheory]
    [InlineData(RepeatMode.All)]
    [InlineData(RepeatMode.One)]
    public void NextWithRepeat_RestartsThePlayingSong(RepeatMode repeat)
    {
        var (vm, player) = CreateVm();
        var album = Album("a", "b", "c");
        vm.ReplaceQueueAndPlay(album, 1);
        vm.RepeatMode = repeat;
        vm.ClearQueue();
        var plays = player.PlayedPaths.Count;

        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);

        Assert.Same(album[1], vm.CurrentTrack);
        Assert.Equal(plays + 1, player.PlayedPaths.Count);
        Assert.Equal(album[1].FilePath, player.PlayedPaths[^1]);
        Assert.Empty(vm.UpNext);
    }

    [AvaloniaFact]
    public void NextWithoutRepeat_IsDimmed_AsAtTheEndOfAQueue()
    {
        var (vm, _) = CreateVm();
        var album = Album("a", "b");
        vm.ReplaceQueueAndPlay(album, 0);
        vm.ClearQueue();

        Assert.False(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null); // hotkeys and media keys bypass CanExecute
        Assert.Same(album[0], vm.CurrentTrack);
    }

    [AvaloniaFact]
    public void Previous_RestartsThePlayingSong_NotTheSongBeforeTheClear()
    {
        var (vm, player) = CreateVm();
        var album = Album("a", "b", "c");
        vm.ReplaceQueueAndPlay(album, 0);
        End(player); // b playing, a played
        vm.ClearQueue();
        var plays = player.PlayedPaths.Count;

        vm.PreviousCommand.Execute(null);

        Assert.Same(album[1], vm.CurrentTrack);
        Assert.Equal(plays, player.PlayedPaths.Count); // a seek to 0, not a reload
        Assert.Equal(TimeSpan.Zero, vm.Position);
    }

    [AvaloniaFact]
    public void Previous_StartedMidAlbum_DoesNotStepBackIntoTheClearedAlbum()
    {
        var (vm, _) = CreateVm();
        var album = Album("a", "b", "c");
        vm.ReplaceQueueAndPlay(album, 2); // a, b are "preceding" (GitHub #74)
        vm.ClearQueue();

        vm.PreviousCommand.Execute(null);

        Assert.Same(album[2], vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
    }

    // ── Filling the queue again ──

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddToQueue_AfterClear_PlayingSongIsBackAboveIt_AndPlaysNext(bool keepPlayed)
    {
        var (vm, player) = CreateVm(keepPlayed);
        var album = Album("a", "b", "c");
        var (x, y) = (Trk("x"), Trk("y"));
        vm.ReplaceQueueAndPlay(album, 0);
        vm.ClearQueue();

        vm.AddToQueue(x, announce: false);
        vm.AddNext(y, announce: false);

        Assert.True(vm.ShowQueueCurrentTrack);
        Assert.Equal(keepPlayed ? "a,y,x" : "y,x", Titles(vm.QueueRows));
        if (keepPlayed) Assert.Equal(0, vm.QueueCurrentRow);

        End(player);
        Assert.Same(y, vm.CurrentTrack);
        End(player);
        Assert.Same(x, vm.CurrentTrack);
        End(player);
        // Nothing of the cleared album comes back: the queue ends (Keep Played Songs keeps
        // the last song loaded, stopped).
        if (keepPlayed) Assert.Same(x, vm.CurrentTrack);
        else Assert.Null(vm.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Empty(vm.UpNext);
    }

    [AvaloniaFact]
    public void RepeatAll_RefilledAfterClear_CyclesThePlayingSongAndTheNewOnes()
    {
        var (vm, player) = CreateVm();
        var album = Album("a", "b", "c");
        var x = Trk("x");
        vm.ReplaceQueueAndPlay(album, 1);
        vm.RepeatMode = RepeatMode.All;
        vm.ClearQueue();
        vm.AddToQueue(x, announce: false);

        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }

        Assert.Equal("x,b,x,b", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void PlayingTheSameSongAgain_FromTheLibrary_ShowsItInTheQueue()
    {
        var (vm, _) = CreateVm();
        var a = Trk("a");
        vm.ReplaceQueueAndPlay(new[] { a }, 0);
        vm.ClearQueue();

        vm.ReplaceQueueAndPlay(new[] { a }, 0); // the same Track: CurrentTrack does not change

        Assert.True(vm.ShowQueueCurrentTrack);
        Assert.True(vm.ShowQueueNothingUpNext);
    }

    [AvaloniaFact]
    public void MoveQueueRows_PlayedBlockDroppedBelow_AfterClear_KeepsItsOrder()
    {
        var (vm, player) = CreateVm(keepPlayed: true);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player, 2); // a, b played; c playing
        vm.ClearQueue();
        Assert.Equal("a,b", Titles(vm.QueueRows));

        // The top row dragged below the last: it lands there, among the played rows, and the
        // cleared song does not reappear between them.
        Assert.Equal(1, vm.MoveQueueRows(new[] { 0 }, 2));

        Assert.Equal("b,a", Titles(vm.QueueRows));
        Assert.Empty(vm.UpNext);
        Assert.Equal(-1, vm.QueueCurrentRow);
        Assert.Equal("c", vm.CurrentTrack?.Title);
    }

    [AvaloniaFact]
    public void AnotherSongStarting_BringsTheNowPlayingCardBack()
    {
        var (vm, _) = CreateVm();
        var album = Album("a", "b");
        vm.ReplaceQueueAndPlay(album, 0);
        vm.ClearQueue();

        vm.ReplaceQueueAndPlay(Album("p", "q"), 0);

        Assert.Equal("p", vm.CurrentTrack?.Title);
        Assert.True(vm.ShowQueueCurrentTrack);
        Assert.Equal("q", Titles(vm.QueueRows));
    }

    // ── Shuffle ──

    [AvaloniaFact]
    public void ShuffleOff_AfterClear_DoesNotBringClearedTracksBack()
    {
        var (vm, _) = CreateVm();
        var album = new[] { Trk("a"), Trk("b"), Trk("skip", skipWhenShuffling: true), Trk("c") };
        vm.ReplaceQueueAndPlay(album, 0);
        vm.ToggleShuffleCommand.Execute(null); // on: "skip" left out of the shuffled queue
        vm.ClearQueue();

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.Empty(vm.UpNext);
    }

    // ── Restart ──

    [AvaloniaFact]
    public async Task Restore_AfterClear_KeepsTheSong_AndRepeatStillReplaysOnlyIt()
    {
        var lib = new FakeLibraryService();
        var album = Album("a", "b", "c");
        lib.TrackList.AddRange(album);
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, lib, persistence, new FakeAnimatedCoverService());
        vm.ReplaceQueueAndPlay(album, 0);
        End(player); // b playing
        vm.RepeatMode = RepeatMode.All;
        vm.ClearQueue();
        await Task.Delay(300); // let the track-change background snapshot land first
        await vm.SaveQueueStateAsync();

        var player2 = new FakeAudioPlayer();
        var restored = new PlayerViewModel(player2, lib, persistence, new FakeAnimatedCoverService());
        await restored.RestoreQueueStateAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(album[1], restored.CurrentTrack);
        Assert.Empty(restored.UpNext);
        restored.PlayPauseCommand.Execute(null);
        End(player2, 2);
        Assert.Same(album[1], restored.CurrentTrack);
        Assert.All(player2.PlayedPaths, p => Assert.Equal(album[1].FilePath, p));
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
