using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #256 "Deleted songs off queue re-appear when Repeat All is enabled". With Keep Played
/// Songs on the queue is one list (played, playing, up next): removing a played row took it out
/// of the list but not out of Repeat All's cycle, so the wrap brought it back (and a song first
/// in the cycle could never be removed for good). Clearing the whole queue, then playing newly
/// queued music, put the cleared song back in the played rows like a ghost.
/// </summary>
public class QueueRemovedPlayedIssue256Tests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm(bool keepPlayed = true)
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

    private static int RowOf(PlayerViewModel vm, string title, QueueRowKind kind, bool last = false)
    {
        var rows = Enumerable.Range(0, vm.QueueRows.Count)
            .Where(r => vm.QueueRows[r].Title == title && vm.GetQueueRowKind(r) == kind).ToList();
        Assert.NotEmpty(rows);
        return last ? rows[^1] : rows[0];
    }

    /// <summary>The reporter's steps: an album, a single song put first and played, then the
    /// album. Returns with the album's first song playing and the single a played row.</summary>
    private static Track[] SingleThenAlbum(PlayerViewModel vm, FakeAudioPlayer player)
    {
        var album = Album("a", "b", "c");
        vm.ReplaceQueueAndPlay(album, 0);
        vm.RepeatMode = RepeatMode.All;
        var single = Trk("s");
        vm.AddNext(single, announce: false);
        vm.PlayFromUpNextAt(0); // play it
        End(player); // b
        return album;
    }

    // ── A: a removed played song stays removed through the wrap ──

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedPlayedSong_DoesNotComeBack_WhenRepeatAllWraps(bool shuffle)
    {
        var (vm, player) = CreateVm();
        var album = Album("a", "b", "c");
        var single = Trk("s");
        vm.ReplaceQueueAndPlay(new[] { single }.Concat(album).ToList(), 0);
        vm.RepeatMode = RepeatMode.All;
        if (shuffle) vm.ToggleShuffleCommand.Execute(null);
        End(player); // s is played now

        vm.RemoveQueueRows(new[] { RowOf(vm, "s", QueueRowKind.Played) });
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == "s");

        var seen = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }

        Assert.DoesNotContain("s", seen);
        Assert.Equal("a,b,c", string.Join(",", seen.Distinct().Order()));
        Assert.DoesNotContain(vm.UpNext, t => t.Title == "s");
    }

    [AvaloniaFact]
    public void RemovedPlayedSong_PlayedNextFromMenu_DoesNotComeBack()
    {
        // The reporter's order: album queued, the single added to play next and played.
        var (vm, player) = CreateVm();
        SingleThenAlbum(vm, player);
        Assert.Equal("b", vm.CurrentTrack!.Title);

        vm.RemoveQueueRows(new[] { RowOf(vm, "s", QueueRowKind.Played) });

        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }
        Assert.DoesNotContain("s", seen);
    }

    // ── B: the first song of the cycle can be removed after a wrap too ──

    [AvaloniaFact]
    public void FirstSongOfTheCycle_RemovedAfterAWrap_StaysRemoved()
    {
        var (vm, player) = CreateVm();
        var single = Trk("s");
        vm.ReplaceQueueAndPlay(new[] { single }.Concat(Album("a", "b")).ToList(), 0);
        vm.RepeatMode = RepeatMode.All;
        End(player, 3); // wrapped: s plays again, straight away (never an Up Next row)
        Assert.Equal("s", vm.CurrentTrack!.Title);
        End(player); // s is a played row again (two of them now)

        vm.RemoveQueueRows(new[] { RowOf(vm, "s", QueueRowKind.Played, last: true) });

        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }
        Assert.DoesNotContain("s", seen);
    }

    [AvaloniaFact]
    public async Task RemovedPlayedSong_StaysOutOfTheCycle_AfterRestart()
    {
        var lib = new FakeLibraryService();
        var single = Trk("s");
        var album = Album("a", "b", "c");
        lib.TrackList.Add(single);
        lib.TrackList.AddRange(album);
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, lib, persistence, new FakeAnimatedCoverService()) { KeepPlayedInQueue = true };
        vm.ReplaceQueueAndPlay(new[] { single }.Concat(album).ToList(), 0);
        vm.RepeatMode = RepeatMode.All;
        End(player);
        vm.RemoveQueueRows(new[] { RowOf(vm, "s", QueueRowKind.Played) });
        await Task.Delay(300); // let the track-change background snapshot land first
        await vm.SaveQueueStateAsync();

        var player2 = new FakeAudioPlayer();
        var restored = new PlayerViewModel(player2, lib, persistence, new FakeAnimatedCoverService()) { KeepPlayedInQueue = true };
        await restored.RestoreQueueStateAsync();
        Dispatcher.UIThread.RunJobs();
        restored.PlayPauseCommand.Execute(null);
        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            End(player2);
            seen.Add(restored.CurrentTrack!.Title);
        }
        Assert.DoesNotContain("s", seen);
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    // ── Neighbours ──

    [AvaloniaFact]
    public void RemovingOneOfTwoPlayedCopies_KeepsTheOtherInTheCycle()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b"), 0);
        var a2 = vm.CurrentTrack!;
        vm.AddToQueue(a2, announce: false); // a, b, a
        vm.RepeatMode = RepeatMode.All;
        End(player, 2); // the second a playing; a, b played

        vm.RemoveQueueRows(new[] { RowOf(vm, "a", QueueRowKind.Played) });

        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }
        Assert.Equal("b,a,b,a", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void RemovingSeveralSelectedPlayedAndUpcomingRows_AllStayRemoved()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e"), 0);
        vm.RepeatMode = RepeatMode.All;
        End(player, 2); // c playing; a, b played; d, e up next

        vm.RemoveQueueRows(new[] { RowOf(vm, "a", QueueRowKind.Played), RowOf(vm, "e", QueueRowKind.Upcoming) });

        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }
        Assert.Equal("d,b,c,d,b,c", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void RemovingAPlayedRowFromBeforeThisQueue_LeavesTheCycleAlone()
    {
        // A song that played in an earlier queue is a played row too; it was never in this
        // queue's cycle, so removing it must not take this queue's copy of the same song.
        var (vm, player) = CreateVm();
        var x = Trk("x");
        vm.ReplaceQueueAndPlay(new[] { x }, 0);
        vm.ReplaceQueueAndPlay(new[] { x, Trk("y") }, 1); // x (old queue) is a played row
        vm.RepeatMode = RepeatMode.All;

        vm.RemoveQueueRows(new[] { RowOf(vm, "x", QueueRowKind.Played) });

        End(player);
        Assert.Equal("x", vm.CurrentTrack!.Title);
    }

    // ── C: a cleared queue stays cleared ──

    public static TheoryData<bool, RepeatMode, bool> ClearCases()
    {
        var data = new TheoryData<bool, RepeatMode, bool>();
        foreach (var dropped in new[] { false, true })
        foreach (var repeat in new[] { RepeatMode.Off, RepeatMode.All })
        foreach (var shuffle in new[] { false, true })
            data.Add(dropped, repeat, shuffle);
        return data;
    }

    /// <summary>Plays an album, pauses on b, clears played and up next, then queues x, y
    /// (dropped onto the list, or Add to Queue from a menu).</summary>
    private static void PauseClearAllAndRequeue(PlayerViewModel vm, FakeAudioPlayer player,
        bool dropped, RepeatMode repeat, bool shuffle)
    {
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        vm.RepeatMode = repeat;
        if (shuffle) vm.ToggleShuffleCommand.Execute(null);
        End(player);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(PlaybackState.Paused, vm.State);
        var ghost = vm.CurrentTrack!.Title;

        vm.ClearQueue();
        vm.ClearPlayed();
        Assert.True(vm.IsQueueEmpty);

        if (dropped) vm.InsertIntoQueue(vm.QueueInsertRowToUpNextIndex(0), Album("x", "y"), announce: false);
        else vm.AddRangeToQueue(Album("x", "y"), announce: false);
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == ghost);
    }

    [AvaloniaTheory]
    [MemberData(nameof(ClearCases))]
    public void ClearedEverything_DoubleClickTheNewSong_ClearedSongDoesNotComeBack(bool dropped, RepeatMode repeat, bool shuffle)
    {
        var (vm, player) = CreateVm();
        PauseClearAllAndRequeue(vm, player, dropped, repeat, shuffle);
        var ghost = vm.CurrentTrack!.Title;

        vm.PlayQueueRow(0); // double-click x

        Assert.Equal("x", vm.CurrentTrack!.Title);
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == ghost);
        Assert.DoesNotContain(vm.History, t => t.Title == ghost);
        End(player, 4);
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == ghost);
    }

    [AvaloniaTheory]
    [MemberData(nameof(ClearCases))]
    public void ClearedEverything_PlayOn_ClearedSongDoesNotComeBack(bool dropped, RepeatMode repeat, bool shuffle)
    {
        var (vm, player) = CreateVm();
        PauseClearAllAndRequeue(vm, player, dropped, repeat, shuffle);
        var ghost = vm.CurrentTrack!.Title;

        vm.PlayPauseCommand.Execute(null); // resumes the cleared song (#175: it plays on)
        End(player); // it ends: the queued songs follow

        Assert.Equal("x", vm.CurrentTrack!.Title);
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == ghost);
        End(player, 4);
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == ghost);
    }

    [AvaloniaFact]
    public void ClearedEverything_NextButton_ClearedSongDoesNotComeBack()
    {
        var (vm, player) = CreateVm();
        PauseClearAllAndRequeue(vm, player, dropped: false, RepeatMode.All, shuffle: false);
        var ghost = vm.CurrentTrack!.Title;

        vm.NextCommand.Execute(null);

        Assert.Equal("x", vm.CurrentTrack!.Title);
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == ghost);
    }

    [AvaloniaFact]
    public void ClearedUpNext_DoubleClickAPlayedSong_ClearedSongDoesNotComeBack()
    {
        // Clear Queue keeps the played rows: playing one again put the cleared song in Up Next.
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        End(player); // b playing, a played
        vm.ClearQueue();

        vm.PlayQueueRow(RowOf(vm, "a", QueueRowKind.Played));

        Assert.Equal("a", vm.CurrentTrack!.Title);
        Assert.DoesNotContain(vm.QueueRows, t => t.Title == "b");
    }

    [AvaloniaFact]
    public void ClearedEverything_PlayAnAlbum_ClearedSongDoesNotComeBack()
    {
        var (vm, player) = CreateVm();
        PauseClearAllAndRequeue(vm, player, dropped: false, RepeatMode.All, shuffle: false);
        var ghost = vm.CurrentTrack!.Title;

        vm.ReplaceQueueAndPlay(Album("p", "q"), 0);

        Assert.DoesNotContain(vm.QueueRows, t => t.Title == ghost);
    }
}
