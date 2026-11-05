using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #134 "Make queue more accessible pt. IV": with Keep Played Songs on (auto-clearing
/// off) the queue reads as one list in play order — played songs, the loaded song in place,
/// Up Next — and the played rows can be selected, removed, cleared, played and dragged.
/// </summary>
public class QueueIssue134Tests
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

    /// <summary>What the rows must be, built from scratch.</summary>
    private static List<Track> Expected(PlayerViewModel vm)
    {
        var rows = new List<Track>();
        if (vm.KeepPlayedInQueue)
        {
            rows.AddRange(vm.History.Reverse());
            if (vm.ShowQueueCurrentTrack) rows.Add(vm.CurrentTrack!); // GitHub #175: not after Clear Queue
        }
        rows.AddRange(vm.UpNext);
        return rows;
    }

    private static void AssertRowsInSync(PlayerViewModel vm)
    {
        var expected = Expected(vm);
        Assert.Equal(expected.Count, vm.QueueRows.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Same(expected[i], vm.QueueRows[i]);
    }

    // ── The rows ──

    [AvaloniaFact]
    public void KeepPlayed_RowsReadPlayedThenCurrentInPlaceThenUpNext()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e"), 0);
        End(player, 2); // a, b played; c playing

        Assert.Equal("a,b,c,d,e", Titles(vm.QueueRows));
        Assert.Equal(2, vm.QueuePlayedRowCount);
        Assert.Equal(2, vm.QueueCurrentRow);
        Assert.Equal(new[] { QueueRowKind.Played, QueueRowKind.Played, QueueRowKind.Current, QueueRowKind.Upcoming, QueueRowKind.Upcoming },
            Enumerable.Range(0, 5).Select(vm.GetQueueRowKind));
        // Up Next keeps counting from the next song; played rows and the loaded one carry a mark.
        Assert.Equal(new[] { "", "", "", "1", "2" }, Enumerable.Range(0, 5).Select(vm.QueueRowNumber));
    }

    [AvaloniaFact]
    public void KeepPlayedOff_RowsAreUpNext_AsBefore()
    {
        var (vm, player) = CreateVm(keepPlayed: false);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player);

        Assert.Equal("c,d", Titles(vm.QueueRows));
        Assert.Equal(-1, vm.QueueCurrentRow);
        Assert.Equal(0, vm.QueuePlayedRowCount);
        Assert.All(Enumerable.Range(0, 2), r => Assert.Equal(QueueRowKind.Upcoming, vm.GetQueueRowKind(r)));
        Assert.Equal(new[] { "1", "2" }, Enumerable.Range(0, 2).Select(vm.QueueRowNumber));
    }

    /// <summary>An advance shifts the loaded-song marker down one row; the rows themselves
    /// stay the same tracks (no reset of the list).</summary>
    [AvaloniaFact]
    public void KeepPlayed_AnAdvance_EditsTheRowsWithoutAReset()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.QueueRows.CollectionChanged += (_, e) => actions.Add(e.Action);

        End(player);

        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal("a,b,c,d", Titles(vm.QueueRows));
        Assert.Equal(1, vm.QueueCurrentRow);
    }

    [AvaloniaFact]
    public void TogglingTheSetting_RebuildsTheRows()
    {
        var (vm, player) = CreateVm(keepPlayed: false);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        End(player);
        Assert.Equal("c", Titles(vm.QueueRows));

        vm.KeepPlayedInQueue = true;
        Assert.Equal("a,b,c", Titles(vm.QueueRows));
        vm.KeepPlayedInQueue = false;
        Assert.Equal("c", Titles(vm.QueueRows));
    }

    /// <summary>Every queue operation, in both modes: the rows always match
    /// History (reversed) + the loaded song + Up Next.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rows_StayInSync_ThroughEveryQueueOperation(bool keepPlayed)
    {
        var (vm, player) = CreateVm(keepPlayed);
        var rng = new Random(134);
        var pool = Enumerable.Range(0, 12).Select(i => Trk("t" + i)).ToArray();
        vm.ReplaceQueueAndPlay(pool.Take(6).ToArray(), 2);
        AssertRowsInSync(vm);

        for (var step = 0; step < 400; step++)
        {
            switch (rng.Next(16))
            {
                case 0: End(player); break;
                case 1: vm.NextCommand.Execute(null); break;
                case 2: vm.PreviousCommand.Execute(null); break;
                case 3: vm.AddToQueue(pool[rng.Next(pool.Length)], announce: false); break;
                case 4: vm.AddNext(pool[rng.Next(pool.Length)], announce: false); break;
                case 5: vm.AddRangeToQueue(pool.Take(rng.Next(1, 4)).ToList(), announce: false); break;
                case 6 when vm.UpNext.Count > 0: vm.RemoveFromQueue(rng.Next(vm.UpNext.Count)); break;
                case 7 when vm.QueueRows.Count > 0: vm.RemoveQueueRows(new[] { rng.Next(vm.QueueRows.Count), rng.Next(vm.QueueRows.Count) }); break;
                case 8 when vm.QueueRows.Count > 1:
                    vm.MoveQueueRow(rng.Next(vm.QueueRows.Count), rng.Next(vm.QueueRows.Count)); break;
                case 9 when vm.QueueRows.Count > 2:
                    vm.MoveQueueRows(new[] { rng.Next(vm.QueueRows.Count), rng.Next(vm.QueueRows.Count) }, rng.Next(vm.QueueRows.Count + 1)); break;
                case 10 when vm.QueueRows.Count > 0: vm.PlayQueueRow(rng.Next(vm.QueueRows.Count)); break;
                case 11: vm.ToggleShuffleCommand.Execute(null); break;
                case 12 when rng.Next(4) == 0: vm.ClearPlayed(); break;
                case 13 when rng.Next(6) == 0: vm.ClearQueue(); break;
                case 14 when rng.Next(8) == 0: vm.ReplaceQueueAndPlay(pool.Skip(rng.Next(6)).Take(5).ToArray(), rng.Next(3)); break;
                case 15 when rng.Next(10) == 0: vm.KeepPlayedInQueue = !vm.KeepPlayedInQueue; break;
            }
            Dispatcher.UIThread.RunJobs();
            AssertRowsInSync(vm);
        }
    }

    [AvaloniaFact]
    public void PlayedRows_AreCappedWithHistory()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Enumerable.Range(0, 70).Select(i => Trk("t" + i)).ToArray(), 0);
        End(player, 60);

        Assert.Equal(50, vm.QueuePlayedRowCount);
        Assert.Equal("t10", vm.QueueRows[0].Title); // the oldest ten dropped off the top
        Assert.Equal("t60", vm.QueueRows[vm.QueueCurrentRow].Title);
        AssertRowsInSync(vm);
    }

    // ── Removing / clearing played rows ──

    [AvaloniaFact]
    public void RemoveQueueRows_RemovesPlayedAndUpcomingRows_ButNeverTheLoadedSong()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e"), 0);
        End(player, 2); // rows a,b | c | d,e

        vm.RemoveQueueRows(new[] { 0, 2, 4 });

        Assert.Equal("b,c,d", Titles(vm.QueueRows));
        Assert.Equal("c", vm.CurrentTrack?.Title);
        Assert.Equal("b", Titles(vm.History));
        Assert.Equal("d", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void ClearPlayed_EmptiesThePlayedRows_AndKeepsTheRest()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player, 2);
        Assert.True(vm.HasPlayedQueueRows);

        vm.ClearPlayed();

        Assert.False(vm.HasPlayedQueueRows);
        Assert.Equal("c,d", Titles(vm.QueueRows));
        Assert.Equal(0, vm.QueueCurrentRow);
        Assert.Empty(vm.History);
        Assert.Empty(vm.PlayedTracks);
    }

    /// <summary>Removing played rows changes the list, not what Repeat All replays.</summary>
    [AvaloniaFact]
    public void ClearPlayed_LeavesRepeatAllsCycleWhole()
    {
        var (vm, player) = CreateVm();
        vm.RepeatMode = RepeatMode.All;
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        End(player, 2);
        vm.ClearPlayed();

        End(player); // c ends → wraps
        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c", Titles(vm.UpNext));
    }

    // ── Playing a row ──

    [AvaloniaFact]
    public void PlayQueueRow_OnAPlayedRow_JumpsBack_AndTheListKeepsItsOrder()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player, 2);

        vm.PlayQueueRow(0);

        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("a,b,c,d", Titles(vm.QueueRows));
        Assert.Equal(0, vm.QueueCurrentRow);
    }

    [AvaloniaFact]
    public void PlayQueueRow_OnAnUpcomingRow_KeepPlayed_TheJumpedOverRowsStayAbove()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e"), 0);

        vm.PlayQueueRow(3); // d

        Assert.Equal("d", vm.CurrentTrack?.Title);
        Assert.Equal("a,b,c,d,e", Titles(vm.QueueRows));
        Assert.Equal(3, vm.QueueCurrentRow);
        Assert.Equal("e", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void PlayQueueRow_OnAnUpcomingRow_KeepPlayedOff_DropsTheJumpedOverRows()
    {
        var (vm, _) = CreateVm(keepPlayed: false);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e"), 0);

        vm.PlayQueueRow(2); // UpNext[2] = d

        Assert.Equal("d", vm.CurrentTrack?.Title);
        Assert.Equal("e", Titles(vm.QueueRows));
        Assert.Equal("a", Titles(vm.History));
    }

    [AvaloniaFact]
    public void PlayQueueRow_OnTheLoadedSong_ResumesItWhenStopped()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b"), 0);
        End(player, 2); // queue ran out: b stays loaded, stopped
        Assert.Equal(PlaybackState.Stopped, vm.State);

        vm.PlayQueueRow(vm.QueueCurrentRow);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b", vm.CurrentTrack?.Title);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    // ── Dragging rows across the loaded song ──

    [AvaloniaFact]
    public void MoveQueueRow_APlayedRowDroppedBelowTheLoadedSong_PlaysNext()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player, 2); // a,b | c | d

        vm.MoveQueueRow(0, 2); // a onto c's slot (dragging down): lands just below c

        Assert.Equal("b,c,a,d", Titles(vm.QueueRows));
        Assert.Equal("a,d", Titles(vm.UpNext));
        Assert.Equal("b", Titles(vm.History));
        Assert.Equal(1, vm.QueueCurrentRow);
        End(player);
        Assert.Equal("a", vm.CurrentTrack?.Title);
    }

    [AvaloniaFact]
    public void MoveQueueRow_AnUpcomingRowDroppedAboveTheLoadedSong_JoinsThePlayed()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e"), 0);
        End(player); // a | b | c,d,e

        vm.MoveQueueRow(3, 1); // d onto b's slot (dragging up): lands just above b

        Assert.Equal("a,d,b,c,e", Titles(vm.QueueRows));
        Assert.Equal("d,a", Titles(vm.History));
        Assert.Equal("c,e", Titles(vm.UpNext));
        // Previous now goes back to it: it sits right before the loaded song.
        vm.PreviousCommand.Execute(null);
        Assert.Equal("d", vm.CurrentTrack?.Title);
    }

    [AvaloniaFact]
    public void MoveQueueRows_ABlockSpanningBothSides_KeepsItsOrder_AndReportsWhereItLanded()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d", "e", "f"), 0);
        End(player, 2); // a,b | c | d,e,f

        var landAt = vm.MoveQueueRows(new[] { 0, 4 }, 6); // a and e to the end

        Assert.Equal("b,c,d,f,a,e", Titles(vm.QueueRows));
        Assert.Equal(4, landAt);
        Assert.Equal("c", vm.CurrentTrack?.Title);
        Assert.Equal(1, vm.QueueCurrentRow);
    }

    [AvaloniaFact]
    public void MoveQueueRows_TheLoadedSongNeverMoves()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        End(player);

        Assert.Equal(-1, vm.MoveQueueRows(new[] { 1 }, 3));
        Assert.Equal("a,b,c", Titles(vm.QueueRows));
    }

    [AvaloniaFact]
    public void MoveQueueRows_KeepPlayedOff_IsTheUpNextMove()
    {
        var (vm, _) = CreateVm(keepPlayed: false);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);

        var landAt = vm.MoveQueueRows(new[] { 0 }, 3);

        Assert.Equal(2, landAt);
        Assert.Equal("c,d,b", Titles(vm.UpNext));
    }

    // ── Empty states ──

    [AvaloniaFact]
    public void QueueEmpty_OnlyWhenThereIsNothingAtAll()
    {
        var (vm, player) = CreateVm(keepPlayed: false);
        Assert.True(vm.IsQueueEmpty);
        Assert.False(vm.ShowQueueNothingUpNext);

        vm.ReplaceQueueAndPlay(Album("a", "b"), 0);
        End(player); // b playing, nothing up next
        Assert.False(vm.IsQueueEmpty);
        Assert.True(vm.ShowQueueNothingUpNext);

        End(player); // the queue ran out (setting off: the player empties)
        Assert.True(vm.IsQueueEmpty);
        Assert.False(vm.ShowQueueNothingUpNext);
    }

    [AvaloniaFact]
    public void QueueEmpty_KeepPlayed_NotWhilePlayedRowsShow()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b"), 0);
        End(player, 2); // b stays loaded, a played
        Assert.False(vm.IsQueueEmpty);
        Assert.False(vm.ShowQueueNothingUpNext); // the list itself shows where it ends

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.StopAndClear("test");
        Assert.True(vm.IsQueueEmpty);
        Assert.Contains(nameof(PlayerViewModel.IsQueueEmpty), changed);
    }

    // ── The panel (MainWindow is not shown headlessly: its drawer is lifted out) ──

    private static string RepoFile(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Noctis.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void QueuePanel_OneListOfRows_AndTheNewActionsAreWired()
    {
        var xaml = RepoFile("src", "Noctis", "Views", "MainWindow.axaml");
        var code = RepoFile("src", "Noctis", "Views", "MainWindow.axaml.cs");

        // The shrunken greyed "Played" block is gone; the list holds every row.
        Assert.DoesNotContain("x:Name=\"QueuePlayed\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding Player.QueueRows}\"", xaml);
        // The pinned Now Playing card only without Keep Played Songs (the row is in the list then).
        var pinned = xaml.IndexOf("x:Name=\"QueueNowPlaying\"", StringComparison.Ordinal);
        var wrapper = xaml.LastIndexOf("<Panel DockPanel.Dock=\"Top\" IsVisible=\"{Binding !Player.KeepPlayedInQueue}\">", pinned, StringComparison.Ordinal);
        Assert.True(wrapper > 0 && pinned - wrapper < 200);

        // Clear Played: the header chip and the played rows' menu.
        Assert.Contains("ToolTip.Tip=\"{loc:T Queue.ClearPlayed}\"", xaml); // icon chip since the History/Repeat chips
        Assert.Contains("IsEnabled=\"{Binding Player.HasPlayedQueueRows}\"", xaml);
        Assert.Contains("Classes=\"queue-menu-clear-played\"", xaml);
        Assert.Contains("vm.Player.ClearPlayed()", code);

        // Rows act by row: remove (played too), play, drag.
        Assert.Contains("vm.Player.RemoveQueueRows(selection.Snapshot())", code);
        Assert.Contains("vm.Player.PlayQueueRow(index)", code);
        Assert.Contains("vm.Player.MoveQueueRows(", code);
        Assert.Contains("new QueueRowSelection<Track>(vm.Player.QueueRows)", code);
        Assert.DoesNotContain("PlayFromUpNextAt(index)", code);

        // Empty states: "empty" only when there is nothing; "nothing up next" under a pinned song.
        Assert.Contains("IsVisible=\"{Binding Player.IsQueueEmpty}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding Player.ShowQueueNothingUpNext}\"", xaml);
        Assert.DoesNotContain("IsVisible=\"{Binding !Player.UpNext.Count}\"", xaml);
    }

    private static (Window window, ListBox list, Border drawer, MainWindow main) MountDrawer(PlayerViewModel vm)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var main = new MainWindow();
        var drawer = main.FindControl<Border>("QueuePopupPanel")!;
        var list = main.FindControl<ListBox>("QueuePopupListBox")!;
        ((Avalonia.Controls.Panel)drawer.Parent!).Children.Remove(drawer);
        drawer.IsVisible = true;
        drawer.Opacity = 1;
        drawer.Height = 420;
        list.ItemsSource = vm.QueueRows;
        list.ContainerPrepared += (_, e) => QueuePanelRows.Stamp(list, e.Container, e.Index, vm);
        var window = new Window { Width = 400, Height = 600, Content = drawer };
        window.Show();
        Pump(window);
        return (window, list, drawer, main);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static ListBoxItem Row(ListBox list, int row) => (ListBoxItem)list.ContainerFromIndex(row)!;

    private static bool MarkVisible(ListBoxItem item, string mark)
        => item.GetVisualDescendants().OfType<Noctis.Controls.LineIcon>().Single(i => i.Classes.Contains(mark)).IsVisible;

    [AvaloniaFact]
    public void QueuePanel_PlayedRowsAreFullSizeAndMarked_ThePlayingRowIsHighlightedInPlace()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);
        End(player, 2); // a,b | c | d
        var (window, list, _, main) = MountDrawer(vm);
        try
        {
            QueuePanelRows.StampRealized(list, vm);
            Pump(window);

            var played = Row(list, 0);
            var current = Row(list, 2);
            var upcoming = Row(list, 3);
            Assert.Contains(QueuePanelRows.PlayedClass, played.Classes);
            Assert.Contains(QueuePanelRows.CurrentClass, current.Classes);
            Assert.DoesNotContain(QueuePanelRows.PlayedClass, upcoming.Classes);
            Assert.DoesNotContain(QueuePanelRows.CurrentClass, upcoming.Classes);

            // Same height for every row: no shrunken block.
            Assert.Equal(upcoming.Bounds.Height, played.Bounds.Height);
            Assert.Equal(upcoming.Bounds.Height, current.Bounds.Height);

            // Played: dimmed content and a check; playing: a speaker on the accent card; Up Next: its number.
            Assert.Equal(0.6, played.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("queue-row-content")).Opacity, 3);
            Assert.True(MarkVisible(played, "queue-played-mark"));
            Assert.False(MarkVisible(played, "queue-current-mark"));
            Assert.True(MarkVisible(current, "queue-current-mark"));
            Assert.False(MarkVisible(upcoming, "queue-played-mark"));
            var card = current.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("queue-row"));
            Assert.NotEqual(Avalonia.Media.Brushes.Transparent, card.Background);
            Assert.Equal("1", upcoming.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "QueueRowNumber").Text);
            Assert.Equal("", played.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "QueueRowNumber").Text);

            // The next song: the marker moves down a row, the played mark follows.
            End(player);
            Pump(window);
            QueuePanelRows.StampRealized(list, vm);
            Pump(window);
            Assert.Contains(QueuePanelRows.PlayedClass, Row(list, 2).Classes);
            Assert.Contains(QueuePanelRows.CurrentClass, Row(list, 3).Classes);
        }
        finally
        {
            window.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public void QueuePanel_ScrollsThePlayingRowNearTheTop()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Enumerable.Range(0, 40).Select(i => Trk("t" + i)).ToArray(), 0);
        End(player, 25);
        var (window, list, _, main) = MountDrawer(vm);
        try
        {
            Assert.True(QueuePanelRows.ScrollRowNearTop(list, vm.QueueCurrentRow));
            Pump(window);

            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var current = Row(list, vm.QueueCurrentRow);
            var top = current.TranslatePoint(new Avalonia.Point(0, 0), scroller)!.Value.Y;
            var pitch = current.Bounds.Height + current.Margin.Top + current.Margin.Bottom;
            Assert.InRange(top, pitch - 2, pitch + 2); // second row: one played row above it
            Assert.True(scroller.Offset.Y > 0);
        }
        finally
        {
            window.Close();
            main.Close();
        }
    }

    /// <summary>The number on the row at the top of the list's viewport.</summary>
    private static string TopRowNumber(ListBox list)
    {
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var top = list.GetRealizedContainers()
            .Select(c => (c, y: c.TranslatePoint(new Avalonia.Point(0, 0), scroller)!.Value.Y))
            .Where(x => x.y + x.c.Bounds.Height / 2 >= 0)
            .OrderBy(x => x.y)
            .First().c;
        return top.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "QueueRowNumber").Text ?? "";
    }

    [AvaloniaFact]
    public void QueuePanel_KeepPlayedOff_ListReturnsToUpNextsFirstRow()
    {
        // Live 10-24: History chip off with 35 played songs above Now Playing — the list
        // kept the offset that held the playing row near the top and opened on Up Next #36.
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(Enumerable.Range(0, 80).Select(i => Trk("t" + i)).ToArray(), 0);
        End(player, 35);
        var (window, list, _, main) = MountDrawer(vm);
        try
        {
            Assert.True(QueuePanelRows.ScrollRowNearTop(list, vm.QueueCurrentRow));
            Pump(window);
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Offset.Y > 0);

            vm.ToggleShowPlayedInQueueCommand.Execute(null); // off: the rows are Up Next only
            Pump(window);
            QueuePanelRows.StampRealized(list, vm);
            Pump(window);
            Assert.Equal(-1, vm.QueueCurrentRow);
            Assert.NotEqual("1", TopRowNumber(list)); // the stale offset (what the panel did)

            Assert.True(QueuePanelRows.ScrollToTop(list));
            Pump(window);
            QueuePanelRows.StampRealized(list, vm);
            Pump(window);
            Assert.Equal(0, scroller.Offset.Y);
            Assert.Equal("1", TopRowNumber(list));
        }
        finally
        {
            window.Close();
            main.Close();
        }
    }

    [Fact]
    public void QueuePanel_KeepPlayedOff_ScrollsToTopFromTheToggleAndOnOpen()
    {
        var code = RepoFile("src", "Noctis", "Views", "MainWindow.axaml.cs");
        // Both the History chip and Settings → Playback land in KeepPlayedInQueue, so one hook covers them.
        Assert.Contains("e.PropertyName == nameof(PlayerViewModel.KeepPlayedInQueue) && !vm.Player.KeepPlayedInQueue", code);
        Assert.Contains("_queueScrollToTopPending = true;", code);
        Assert.Contains("_queueScrollToTopPending && QueuePanelRows.ScrollToTop(listBox)", code);
    }
}
