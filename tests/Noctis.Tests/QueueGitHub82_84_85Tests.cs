using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #84 (dropped non-library files survive a library reconcile), #82 (the island
/// title offers "View album" only when the album exists) and #85 (queue panel: row
/// selection, block remove / move, pinned now-playing row).
/// </summary>
public class QueueGitHub82_84_85Tests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static (PlayerViewModel Vm, FakeAudioPlayer Player, FakeLibraryService Library) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player, library);
    }

    private static Track Trk(string name, bool external = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = $"C:/t/{name}.mp3",
        Duration = TimeSpan.FromMinutes(3),
        IsExternal = external,
    };

    private static void Reconcile(FakeLibraryService library)
    {
        library.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();
    }

    // ── GitHub #84: external tracks survive a full LibraryUpdated ──

    [AvaloniaFact]
    public void LibraryUpdated_KeepsExternalTracks_AndStillPrunesARemovedLibraryTrack()
    {
        var (vm, player, library) = CreateVm();
        var e1 = Trk("e1", external: true);
        var e2 = Trk("e2", external: true);
        var kept = Trk("kept");
        var gone = Trk("gone");
        library.TrackList.AddRange(new[] { kept, gone });
        vm.ReplaceQueueAndPlay(new List<Track> { e1, gone, e2, kept }, 0);
        Assert.Equal(PlaybackState.Playing, player.State);

        // Watcher import / removal: "gone" really left the library.
        library.TrackList.Remove(gone);
        Reconcile(library);

        Assert.Same(e1, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(new[] { "e2", "kept" }, vm.UpNext.Select(t => t.Title));
    }

    [AvaloniaFact]
    public void LibraryUpdated_EmptyLibrary_DoesNotClearAnExternalQueue()
    {
        var (vm, player, library) = CreateVm();
        var e1 = Trk("e1", external: true);
        var e2 = Trk("e2", external: true);
        vm.ReplaceQueueAndPlay(new List<Track> { e1, e2 }, 0);

        Reconcile(library); // library.Tracks.Count == 0

        Assert.Same(e1, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(new[] { "e2" }, vm.UpNext.Select(t => t.Title));
    }

    [AvaloniaFact]
    public void LibraryUpdated_EmptyLibrary_WithOnlyLibraryTracks_StillStopsAndClears()
    {
        var (vm, player, library) = CreateVm();
        var a = Trk("a");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, b });
        vm.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);

        library.TrackList.Clear();
        Reconcile(library);

        Assert.Null(vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [AvaloniaFact]
    public void LibraryUpdated_RemovedCurrentLibraryTrack_AdvancesIntoTheExternalQueue()
    {
        var (vm, _, library) = CreateVm();
        var a = Trk("a");
        var e1 = Trk("e1", external: true);
        library.TrackList.Add(a);
        vm.ReplaceQueueAndPlay(new List<Track> { a, e1 }, 0);

        library.TrackList.Clear();
        Reconcile(library);

        Assert.Same(e1, vm.CurrentTrack);
    }

    [Fact]
    public void IsExternal_IsNotSerialized()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Trk("e", external: true));
        Assert.DoesNotContain(nameof(Track.IsExternal), json);
    }

    // ── GitHub #82: "View album" only when the album is in the library ──

    [AvaloniaFact]
    public void ViewCurrentTrackAlbum_CanExecute_OnlyForALibraryAlbum_AndFollowsAnImport()
    {
        var (vm, _, library) = CreateVm();
        var external = Trk("dropped", external: true);
        external.AlbumId = Guid.NewGuid();
        vm.ReplaceQueueAndPlay(new List<Track> { external }, 0);
        Assert.False(vm.ViewCurrentTrackAlbumCommand.CanExecute(null));

        var raised = 0;
        vm.ViewCurrentTrackAlbumCommand.CanExecuteChanged += (_, _) => raised++;
        ((List<Album>)library.Albums).Add(new Album { Id = external.AlbumId, Name = "Later" });
        Reconcile(library);

        Assert.True(raised > 0, "a reconcile must re-query CanExecute (album imported later)");
        Assert.True(vm.ViewCurrentTrackAlbumCommand.CanExecute(null));
    }

    private static Button TitleButton(PlaybackBarView bar) =>
        bar.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("artist-link-btn")
                        && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "TrackTitleTextBlock"));

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void IslandTitle_IsEnabled_AndShowsItsTooltip_OnlyForALibraryAlbum(bool inLibrary)
    {
        var (vm, _, library) = CreateVm();
        var track = Trk("Volví", external: !inLibrary);
        track.AlbumId = Guid.NewGuid();
        if (inLibrary)
        {
            library.TrackList.Add(track);
            ((List<Album>)library.Albums).Add(new Album { Id = track.AlbumId, Name = "Album" });
        }
        vm.ReplaceQueueAndPlay(new List<Track> { track }, 0);

        var bar = new PlaybackBarView { DataContext = vm, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        win.Show();
        Pump(6);

        var title = TitleButton(bar);
        Assert.Equal(inLibrary, title.IsEffectivelyEnabled);

        // Hover it: a disabled title must neither show "View album" nor take the pointer
        // (the Hand cursor comes from the element under the pointer).
        ToolTip.SetShowDelay(title, 0);
        var centre = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), win)!.Value;
        win.MouseMove(centre, RawInputModifiers.None);
        Pump(4);

        Assert.Equal(inLibrary, title.IsPointerOver);
        Assert.Equal(inLibrary, ToolTip.GetIsOpen(title));
        win.Close();
    }

    // ── GitHub #85: block remove / move by row ──

    [Fact]
    public void RemoveManyFromQueue_RemovesOnlyTheGivenRows_OfADuplicatedTrack()
    {
        var (vm, _, _) = CreateVm();
        var now = Trk("now");
        var a = Trk("a");
        var b = Trk("b");
        var c = Trk("c");
        vm.ReplaceQueueAndPlay(new List<Track> { now, a, b, a, c, a }, 0); // UpNext: a b a c a

        vm.RemoveManyFromQueue(new[] { 4, 2, 2, 99, -1 });

        Assert.Equal(new[] { "a", "b", "c" }, vm.UpNext.Select(t => t.Title));
    }

    [Fact]
    public void MoveBlockInQueue_MovesOnlyTheSelectedCopy_OfADuplicatedTrack()
    {
        var (vm, _, _) = CreateVm();
        var now = Trk("now");
        var a = Trk("a");
        var b = Trk("b");
        var c = Trk("c");
        vm.ReplaceQueueAndPlay(new List<Track> { now, a, b, a, c }, 0); // UpNext: a b a c

        // The first "a" to the end. Keyed by Track (ReorderBlock) both copies would move.
        var landAt = vm.MoveBlockInQueue(new[] { 0 }, 4);

        Assert.Equal(3, landAt);
        Assert.Equal(new[] { "b", "a", "c", "a" }, vm.UpNext.Select(t => t.Title));
    }

    [Theory]
    // (block rows, insertion index) — the same contract as the playlist's ReorderBlock.
    [InlineData(new[] { 1, 3 }, 6)]
    [InlineData(new[] { 1, 3 }, 0)]
    [InlineData(new[] { 4, 0 }, 3)]
    [InlineData(new[] { 2, 3 }, 2)]
    [InlineData(new[] { 2, 3 }, 4)]
    [InlineData(new[] { 0, 5 }, 3)]
    [InlineData(new[] { 5 }, 1)]
    public void MoveBlockInQueue_MatchesPlaylistReorderBlock_OnDistinctTracks(int[] rows, int insertIndex)
    {
        var (vm, _, _) = CreateVm();
        var tracks = Enumerable.Range(0, 7).Select(i => Trk($"t{i}")).ToList();
        vm.ReplaceQueueAndPlay(tracks, 0); // UpNext: t1..t6
        var before = vm.UpNext.ToList();
        var expected = PlaylistViewModel.ReorderBlock(before, rows.Select(i => before[i]).ToList(), insertIndex);

        var landAt = vm.MoveBlockInQueue(rows, insertIndex);

        Assert.Equal(expected.Select(t => t.Title), vm.UpNext.Select(t => t.Title));
        if (landAt >= 0)
        {
            var block = rows.Order().Select(i => before[i]).ToList();
            Assert.Equal(block, vm.UpNext.Skip(landAt).Take(block.Count));
        }
        else
        {
            Assert.Equal(before, vm.UpNext);
        }
    }

    // ── GitHub #85: row selection ──

    [Theory]
    [InlineData(2, 5, new[] { 2, 3, 4, 5 })]
    [InlineData(5, 2, new[] { 2, 3, 4, 5 })]
    [InlineData(3, 3, new[] { 3 })]
    [InlineData(0, 1, new[] { 0, 1 })]
    public void Range_IsInclusive_InEitherDirection(int anchor, int row, int[] expected)
        => Assert.Equal(expected, QueueRowSelection<Track>.Range(anchor, row));

    private static (Noctis.Helpers.BulkObservableCollection<Track> Rows, QueueRowSelection<Track> Selection) Rows(params Track[] tracks)
    {
        var rows = new Noctis.Helpers.BulkObservableCollection<Track>();
        rows.AddRange(tracks);
        var selection = new QueueRowSelection<Track>(rows);
        rows.CollectionChanged += (_, e) => selection.Apply(e);
        return (rows, selection);
    }

    [Fact]
    public void Selection_ClickCtrlShift_AndDuplicatesAreSeparateRows()
    {
        var a = Trk("a");
        var b = Trk("b");
        var (_, sel) = Rows(a, b, a, b, a);

        sel.SelectOnly(2);                 // plain click on the second "a"
        Assert.Equal(new[] { 2 }, sel.Snapshot());

        sel.Toggle(2);                     // Ctrl+Click removes
        sel.Toggle(4);                     // Ctrl+Click adds; the anchor follows it
        Assert.Equal(new[] { 4 }, sel.Snapshot());

        sel.SelectRangeTo(1);              // Shift+Click from the anchor (4)
        Assert.Equal(new[] { 1, 2, 3, 4 }, sel.Snapshot());
        Assert.Equal(4, sel.Anchor);

        sel.SelectOnly(0);
        sel.SelectRangeTo(1, additive: false);
        sel.Toggle(3);                        // anchor moves to 3
        sel.SelectRangeTo(4, additive: true); // Ctrl+Shift keeps the rest
        Assert.Equal(new[] { 0, 1, 3, 4 }, sel.Snapshot());

        sel.Clear();
        sel.SelectAll();
        Assert.Equal(5, sel.Count);
    }

    [Fact]
    public void Selection_FollowsItsRows_AsTheQueueMutates()
    {
        var t = Enumerable.Range(0, 6).Select(i => Trk($"t{i}")).ToArray();
        var (rows, sel) = Rows(t);
        sel.Select(new[] { 2, 4 });

        rows.RemoveAt(0);                  // a track advanced
        Assert.Equal(new[] { 1, 3 }, sel.Snapshot());

        rows.Insert(0, Trk("next"));       // Play Next
        Assert.Equal(new[] { 2, 4 }, sel.Snapshot());

        rows.Move(4, 0);                   // a selected row moved to the top
        Assert.Equal(new[] { 0, 3 }, sel.Snapshot());

        rows.RemoveAt(3);                  // a selected row removed
        Assert.Equal(new[] { 0 }, sel.Snapshot());

        rows.AddRange(new[] { Trk("x") }); // Reset from an append keeps what still matches
        Assert.Equal(new[] { 0 }, sel.Snapshot());

        rows.ReplaceAll(t.Reverse());      // Reset that changed the row drops it
        Assert.Empty(sel.Snapshot());
    }

    [Fact]
    public void Selection_RemapsOnlyTheRowsPastAChange_AndKeepsTheAnchorInStep()
    {
        var t = Enumerable.Range(0, 8).Select(i => Trk($"t{i}")).ToArray();
        var (rows, sel) = Rows(t);
        sel.Select(new[] { 1, 3, 5, 6 });
        sel.Toggle(4);                     // anchor 4
        Assert.Equal(new[] { 1, 3, 4, 5, 6 }, sel.Snapshot());

        rows.RemoveAt(4);                  // a selected row past 1 and 3
        Assert.Equal(new[] { 1, 3, 4, 5 }, sel.Snapshot());
        Assert.Equal(-1, sel.Anchor);

        sel.Toggle(4);                     // off again: the anchor is an unselected row
        rows.RemoveAt(6);                  // past every selected row
        rows.Insert(2, Trk("x"));          // between 1 and 3
        Assert.Equal(new[] { 1, 4, 6 }, sel.Snapshot());
        Assert.Equal(5, sel.Anchor);

        rows.Move(6, 2);                   // the last selected row jumps up past 4
        Assert.Equal(new[] { 1, 2, 5 }, sel.Snapshot());
        Assert.Equal(6, sel.Anchor);
    }

    [Fact]
    public void Selection_BlockRemoveOfAHugeSelection_DoesNotReMapTheWholeSelectionPerRow()
    {
        // Audit U03: Ctrl+A then Delete. RemoveManyFromQueue removes row by row, high to low,
        // and each Remove used to rebuild the whole remaining selection: ~N²/2 re-inserts
        // (5,000 rows: 12.5M tree nodes, ~1 GB of garbage, seconds on the UI thread).
        const int n = 5000;
        var (rows, sel) = Rows(Enumerable.Range(0, n).Select(i => Trk($"t{i}")).ToArray());
        sel.SelectAll();

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var i in sel.Snapshot().Reverse())
            rows.RemoveAt(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(rows);
        Assert.Equal(0, sel.Count);
        Assert.True(allocated < 64L * 1024 * 1024, $"removing {n} selected rows allocated {allocated / (1024 * 1024)} MB");
    }

    // ── GitHub #85: panel wiring (MainWindow is not mountable headlessly) ──

    [Fact]
    public void QueuePanel_NowPlayingRow_SitsOutsideTheList_AndBindsCurrentTrack()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "Views", "MainWindow.axaml"));
        var nowPlaying = xaml.IndexOf("x:Name=\"QueueNowPlaying\"", StringComparison.Ordinal);
        var list = xaml.IndexOf("x:Name=\"QueuePopupListBox\"", StringComparison.Ordinal);
        var listEnd = xaml.IndexOf("</ListBox>", list, StringComparison.Ordinal);
        Assert.True(nowPlaying > 0 && list > 0, "both the now-playing row and the queue list must exist");
        Assert.True(nowPlaying < list, "the now-playing row must not be inside the queue ListBox");
        var section = xaml.Substring(nowPlaying, list - nowPlaying);
        Assert.Contains("Player.CurrentTrack, Converter={x:Static ObjectConverters.IsNotNull}", section);
        Assert.Contains("DataContext=\"{Binding Player.CurrentTrack}\"", section);
        Assert.Contains("{loc:T Queue.NowPlaying}", section);

        var rows = xaml.Substring(list, listEnd - list);
        Assert.Contains("ContextRequested=\"OnQueueRowContextRequested\"", rows);
        Assert.Contains("ListBoxItem.ctrl-selected Border.queue-row", rows);
    }
}
