using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #189 "Drop files at a place in the queue": files and folders dragged from Explorer
/// onto the queue list land where the insertion line between the rows shows, not at the end.
/// The pointer → row → Up Next index math, the insert itself (Repeat All cycle and the #188
/// cleared song included) and the line on a real list.
/// </summary>
public class QueueDropIssue189Tests
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

    // ── Pointer → row ──

    /// <summary>Rows 0..4, 40 px apart (margins included), starting at the list's top.</summary>
    private static readonly (int Index, double Top, double Height)[] FiveRows =
        Enumerable.Range(0, 5).Select(i => (i, i * 40.0, 40.0)).ToArray();

    [Theory]
    [InlineData(0, 0)]     // top edge of the first row: above it
    [InlineData(19, 0)]    // upper half of row 0
    [InlineData(21, 1)]    // lower half of row 0: between 0 and 1
    [InlineData(85, 2)]    // upper half of row 2
    [InlineData(119, 3)]   // lower half of row 2
    [InlineData(181, 5)]   // lower half of the last row: the end
    [InlineData(300, 5)]   // empty space below the rows: the end
    public void InsertRowAt_PicksTheGapNearestThePointer(double y, int expected)
    {
        Assert.Equal(expected, QueueDropTarget.InsertRowAt(FiveRows, y, 400, 5));
    }

    [Fact]
    public void InsertRowAt_ScrolledList_CountsFromTheFirstRealizedRow()
    {
        // Rows 30..34 on screen, the first half scrolled out above the top.
        var rows = Enumerable.Range(30, 5).Select(i => (i, (i - 30) * 40.0 - 20, 40.0)).ToArray();
        Assert.Equal(30, QueueDropTarget.InsertRowAt(rows, -5, 160, 100));
        Assert.Equal(31, QueueDropTarget.InsertRowAt(rows, 5, 160, 100));
        Assert.Equal(32, QueueDropTarget.InsertRowAt(rows, 70, 160, 100)); // row 32: 60..100
    }

    [Fact]
    public void InsertRowAt_IgnoresAContainerRealizedFarOffScreen()
    {
        // The focused row (index 90) stays realized thousands of px below the viewport.
        var rows = FiveRows.Append((90, 9000.0, 40.0)).ToArray();
        Assert.Equal(5, QueueDropTarget.InsertRowAt(rows, 190, 400, 100));
    }

    [Fact]
    public void InsertRowAt_EmptyList_IsTheTop()
    {
        Assert.Equal(0, QueueDropTarget.InsertRowAt(Array.Empty<(int, double, double)>(), 50, 400, 0));
    }

    [Fact]
    public void LineY_SitsOnTheGapBetweenRows()
    {
        Assert.Equal(80, QueueDropTarget.LineY(FiveRows, 2, 400, 5));
        Assert.Equal(0, QueueDropTarget.LineY(FiveRows, 0, 400, 5));
        Assert.Equal(200, QueueDropTarget.LineY(FiveRows, 5, 400, 5));  // below the last
        Assert.Equal(0, QueueDropTarget.LineY(Array.Empty<(int, double, double)>(), 0, 400, 0));
        Assert.Null(QueueDropTarget.LineY(FiveRows, 40, 400, 50));      // not on screen
    }

    // ── Row → Up Next index ──

    [AvaloniaFact]
    public void KeepPlayedOff_RowsAreUpNext()
    {
        var (vm, _) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0); // up next b, c, d

        Assert.Equal(0, vm.QueueInsertRowToUpNextIndex(0));
        Assert.Equal(2, vm.QueueInsertRowToUpNextIndex(2));
        Assert.Equal(3, vm.QueueInsertRowToUpNextIndex(3));
        Assert.Equal(3, vm.QueueInsertRowToUpNextIndex(9));
        Assert.Equal(2, vm.UpNextIndexToQueueRow(2));
    }

    [AvaloniaFact]
    public void KeepPlayedOn_AbovePlayedRowsOrThePlayingSong_IsPlayNext()
    {
        var (vm, player) = CreateVm(keepPlayed: true);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e"), 0);
        End(player, 2); // rows: a, b (played), c (playing), d, e
        Assert.Equal("a,b,c,d,e", Titles(vm.QueueRows));

        Assert.Equal(0, vm.QueueInsertRowToUpNextIndex(0)); // among the played rows
        Assert.Equal(0, vm.QueueInsertRowToUpNextIndex(2)); // above the playing song
        Assert.Equal(0, vm.QueueInsertRowToUpNextIndex(3)); // right below it
        Assert.Equal(1, vm.QueueInsertRowToUpNextIndex(4));
        Assert.Equal(2, vm.QueueInsertRowToUpNextIndex(5)); // the end
        // The line for Play Next is drawn under the playing song's row.
        Assert.Equal(3, vm.UpNextIndexToQueueRow(0));
        Assert.Equal(5, vm.UpNextIndexToQueueRow(2));
    }

    [AvaloniaFact]
    public void KeepPlayedOn_AfterClear_TheClearedSongHasNoRowToSkip()
    {
        var (vm, player) = CreateVm(keepPlayed: true);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        End(player); // a played, b playing
        vm.ClearQueue();
        vm.AddRangeToQueue(Album("x", "y"), announce: false);
        Assert.Equal("a,x,y", Titles(vm.QueueRows));

        Assert.Equal(0, vm.QueueInsertRowToUpNextIndex(1));
        Assert.Equal(1, vm.QueueInsertRowToUpNextIndex(2));
        Assert.Equal(1, vm.UpNextIndexToQueueRow(0));
    }

    // ── The insert ──

    [AvaloniaFact]
    public void InsertIntoQueue_Middle_KeepsTheDropsOrder()
    {
        var (vm, _) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);

        vm.InsertIntoQueue(1, Album("x", "y"), announce: false);

        Assert.Equal("b,x,y,c,d", Titles(vm.UpNext));
        Assert.Equal("b,x,y,c,d", Titles(vm.QueueRows));
    }

    [AvaloniaTheory]
    [InlineData(0, "x,y,b,c")]
    [InlineData(-3, "x,y,b,c")]
    [InlineData(2, "b,c,x,y")]
    [InlineData(99, "b,c,x,y")]
    public void InsertIntoQueue_TopIsPlayNext_PastTheEndAppends(int index, string expected)
    {
        var (vm, _) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);

        vm.InsertIntoQueue(index, Album("x", "y"), announce: false);

        Assert.Equal(expected, Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void InsertIntoQueue_AnnouncesOnce_UnlessToldNot()
    {
        var (vm, _) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        var raised = 0;
        vm.TracksQueued += (_, _) => raised++;

        vm.InsertIntoQueue(1, Album("x", "y"));
        vm.InsertIntoQueue(1, Album("z"), announce: false);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void InsertIntoQueue_RepeatAll_WrapReplaysTheQueueAsEdited()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        vm.RepeatMode = RepeatMode.All;
        vm.InsertIntoQueue(1, Album("x"), announce: false); // a | b, x, c

        var seen = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }

        Assert.Equal("b,x,c,a,b,x,c,a", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void InsertIntoQueue_AfterClear_ClearedSongStaysOff_CycleIsTheVisibleQueue()
    {
        var (vm, player) = CreateVm();
        var cleared = Album("a", "b");
        vm.ReplaceQueueAndPlay(cleared, 0);
        vm.RepeatMode = RepeatMode.All;
        vm.ClearQueue();
        vm.AddRangeToQueue(Album("x", "y"), announce: false);

        vm.InsertIntoQueue(1, Album("m"), announce: false);

        Assert.False(vm.ShowQueueCurrentTrack);
        Assert.Equal("x,m,y", Titles(vm.QueueRows));
        Assert.Same(cleared[0], vm.CurrentTrack);
        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            End(player);
            seen.Add(vm.CurrentTrack!.Title);
        }
        Assert.Equal("x,m,y,x,m,y", string.Join(",", seen));
    }

    [AvaloniaFact]
    public void InsertIntoQueue_KeepPlayed_LandsAmongTheUpcomingRows()
    {
        var (vm, player) = CreateVm(keepPlayed: true);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player); // a played, b playing, c, d

        vm.InsertIntoQueue(vm.QueueInsertRowToUpNextIndex(3), Album("x"), announce: false); // above d

        Assert.Equal("a,b,c,x,d", Titles(vm.QueueRows));
        Assert.Equal(QueueRowKind.Upcoming, vm.GetQueueRowKind(3));
    }

    // ── The files are read before they go in ──

    [Fact]
    public void QueueDropIndex_FollowsTheSongTheDropWasAbove()
    {
        var up = Album("b", "c", "d").ToList();
        var c = up[1];
        Assert.Equal(1, MainWindowViewModel.QueueDropIndex(up, c, 1));
        up.RemoveAt(0); // b started playing while the files were read
        Assert.Equal(0, MainWindowViewModel.QueueDropIndex(up, c, 1));
    }

    [Fact]
    public void QueueDropIndex_SongGone_FallsBackToTheRequestedRow_Clamped()
    {
        var up = Album("b", "c").ToList();
        var gone = Trk("z");
        Assert.Equal(1, MainWindowViewModel.QueueDropIndex(up, gone, 1));
        Assert.Equal(2, MainWindowViewModel.QueueDropIndex(up, gone, 7));
    }

    [Fact]
    public void QueueDropIndex_EndAndTop()
    {
        var up = Album("b", "c").ToList();
        Assert.Equal(2, MainWindowViewModel.QueueDropIndex(up, null, 2));
        Assert.Equal(0, MainWindowViewModel.QueueDropIndex(up, null, 0));
        up.Add(Trk("d")); // a song queued meanwhile: the end is still the end
        Assert.Equal(3, MainWindowViewModel.QueueDropIndex(up, null, 2));
    }

    [Fact]
    public void QueueDropIndex_SongQueuedTwice_TakesTheCopyNearestTheRow()
    {
        var b = Trk("b");
        var up = new List<Track> { b, Trk("c"), Trk("d"), b };
        Assert.Equal(3, MainWindowViewModel.QueueDropIndex(up, b, 3));
        Assert.Equal(0, MainWindowViewModel.QueueDropIndex(up, b, 0));
    }

    // ── The line on a real list ──

    [AvaloniaFact]
    public void Track_PlacesTheLineOnTheGap_AndReturnsTheUpNextIndex()
    {
        var (list, line, window) = BuildList(6);
        try
        {
            var target = new QueueDropTarget(list, line, row => row, i => i);
            var row2 = (Control)list.ContainerFromIndex(2)!;
            var row2Top = row2.TranslatePoint(new Point(0, 0), list)!.Value.Y;

            // Upper part of row 2: the drop lands above it.
            var index = target.Track(At(list, row2Top + 2));
            window.UpdateLayout();

            Assert.Equal(2, index);
            Assert.True(line.IsVisible);
            var lineMid = line.TranslatePoint(new Point(0, line.Bounds.Height / 2), list)!.Value.Y;
            Assert.InRange(lineMid, row2Top - row2.Margin.Top - 1.5, row2Top + 0.5);

            // Lower part of row 2: below it.
            Assert.Equal(3, target.Track(At(list, row2Top + row2.Bounds.Height - 2)));

            target.Hide();
            Assert.False(line.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void At_PrefersTheHigherPriority_AndSkipsInactiveLists()
    {
        var (list, line, window) = BuildList(3);
        var active = true;
        var page = new QueueDropTarget(list, line, r => r, i => i);
        var panel = new QueueDropTarget(list, line, r => r, i => i, () => active) { Priority = 1 };
        QueueDropTarget.Register(page);
        QueueDropTarget.Register(panel);
        try
        {
            Assert.Same(panel, QueueDropTarget.At(At(list, 5)));
            active = false; // the queue panel closed
            Assert.Same(page, QueueDropTarget.At(At(list, 5)));
            Assert.Null(QueueDropTarget.At(At(list, list.Bounds.Height + 50))); // below the list
        }
        finally
        {
            QueueDropTarget.Unregister(page);
            QueueDropTarget.Unregister(panel);
            window.Close();
        }
    }

    /// <summary>Maps a point at <paramref name="y"/> in <paramref name="list"/> into any visual,
    /// as DragEventArgs.GetPosition does.</summary>
    private static Func<Visual, Point> At(Visual list, double y)
        => v => list.TranslatePoint(new Point(10, y), v) ?? default;

    private static (ListBox list, Border line, Window window) BuildList(int rows)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, rows).Select(i => $"Row {i}").ToList(),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) =>
                new TextBlock { Text = s, Height = 30 }),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var line = new Border
        {
            Height = 2,
            IsVisible = false,
            IsHitTestVisible = false,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var grid = new Grid { Children = { list, line } };
        var window = new Window { Width = 400, Height = 600, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (list, line, window);
    }
}
