using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #151: the Songs list (and the Artists list view) columns can be resized, moved,
/// shown or hidden from the header's right-click menu, and the sorted column carries an
/// arrow. Header and rows read one shared <see cref="ColumnLayout"/>; the layout persists.
/// </summary>
public class ListColumnsIssue151Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "columns151-" + Guid.NewGuid().ToString("N"));

    public ListColumnsIssue151Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── ColumnLayout ──────────────────────────────────────────────────────

    private static double W(ColumnLayout l, double avail, string key)
    {
        var (w, _) = l.Compute(avail);
        return w[l.Visible.ToList().FindIndex(s => s.Key == key)];
    }

    private static double X(ColumnLayout l, double avail, string key)
    {
        var (_, x) = l.Compute(avail);
        return x[l.Visible.ToList().FindIndex(s => s.Key == key)];
    }

    [Fact]
    public void Defaults_AreTheOldHeaderGrid_StarsShareTheRest_DotsPinnedRight()
    {
        var l = LibraryColumns.CreateSongs();
        Assert.Equal(new[] { "Title", "Time", "Artist", "Album", "Genre", "Favorite", "Rating", "Plays", "More" },
            l.Visible.Select(s => s.Key));
        // Fixed: Time 80 + Genre 110 + Favorites 80 + Rating 84 + Plays 55 + dots 42 = 451.
        var star = (1000 - 451) / 3.0;
        Assert.Equal(star, W(l, 1000, "Title"), 3);
        Assert.Equal(star, W(l, 1000, "Artist"), 3);
        Assert.Equal(star, W(l, 1000, "Album"), 3);
        Assert.Equal(80, W(l, 1000, "Time"));
        Assert.Equal(958, X(l, 1000, "More"), 3);
        Assert.Equal(1000, X(l, 1000, "More") + W(l, 1000, "More"), 3);
        Assert.Equal(string.Empty, l.SerializeOrder());
        Assert.Equal(string.Empty, l.SerializeWidths());
    }

    [Fact]
    public void ResizedColumn_KeepsItsWidth_OthersFlex_MinimumsHold_ResetGoesBackToAuto()
    {
        var l = LibraryColumns.CreateSongs();
        l.SetWidth("Title", 300.4);
        Assert.Equal(300, W(l, 1000, "Title"));
        Assert.Equal((1000 - 451 - 300) / 2.0, W(l, 1000, "Artist"), 3);
        Assert.Equal(300, W(l, 1400, "Title")); // a wider window goes to the flexing columns

        l.SetWidth("Time", 5);
        Assert.Equal(48, W(l, 1000, "Time")); // Time's minimum

        // Narrow window: flexing columns stop at their minimum; the row overflows (clips).
        Assert.Equal(80, W(l, 500, "Artist"));
        Assert.Equal(80, W(l, 500, "Album"));

        l.ResetWidth("Title");
        Assert.Null(l.UserWidth("Title"));
        Assert.Equal("Time=48", l.SerializeWidths());

        // Every flexing column resized: the slack opens before the pinned dots column.
        l.SetWidth("Title", 200); l.SetWidth("Artist", 100); l.SetWidth("Album", 100);
        Assert.Equal(1000 - 42, X(l, 1000, "More"), 3);
    }

    [Fact]
    public void Move_Hide_AndLoad_SurviveUnknownKeysAndNewColumns()
    {
        var l = LibraryColumns.CreateSongs();
        l.Move("Genre", 0);
        Assert.Equal("Genre", l.Visible[0].Key);
        l.Move("Title", 99); // past the end: last movable, still before the pinned dots
        Assert.Equal(new[] { "Plays", "Title", "More" }, l.Visible.Skip(l.Visible.Count - 3).Select(s => s.Key));
        l.SetVisible("Title", false); // cannot hide the title
        Assert.True(l.IsVisible("Title"));
        l.SetVisible("More", false);
        Assert.True(l.IsVisible("More"));

        // An older save: no Year/Composer keys, a key that no longer exists, a bad width.
        var loaded = LibraryColumns.CreateSongs();
        loaded.Load("Genre,Bogus,Title,Time,Artist,Album,Favorite,Rating,Plays,Bpm,Bitrate,SampleRate",
            "Title=300,Bogus=5,Time=abc,Artist=1");
        var order = loaded.Ordered.Select(s => s.Key).ToList();
        Assert.DoesNotContain("Bogus", order);
        Assert.Equal("Genre", order[0]);
        Assert.Equal("More", order[^1]);
        Assert.Equal(order.IndexOf("SampleRate") + 1, order.IndexOf("Year")); // after its default neighbour
        Assert.Equal(LibraryColumns.CreateSongs().Specs.Count, order.Count);
        Assert.Equal(300, loaded.UserWidth("Title"));
        Assert.Null(loaded.UserWidth("Time"));
        Assert.Equal(80, loaded.UserWidth("Artist")); // clamped to the minimum

        loaded.ResetLayout();
        Assert.Equal(string.Empty, loaded.SerializeOrder());
        Assert.Equal(string.Empty, loaded.SerializeWidths());
    }

    // ── Settings ──────────────────────────────────────────────────────────

    private SettingsViewModel NewSettings() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [AvaloniaFact]
    public async Task SongsLayout_FollowsTheShowColumnFlags_AndPersistsOrderAndWidths()
    {
        var settings = NewSettings();
        await settings.LoadAsync();
        var layout = LibraryColumns.CreateSongs();
        using (LibraryColumns.BindSongs(layout, settings))
        {
            // Header menu → the Settings flag View Options shows too.
            layout.RequestVisibility("Year", true);
            Assert.True(settings.ShowYearColumn);
            Assert.True(layout.IsVisible("Year"));
            // View Options → the layout.
            settings.ShowBpmColumn = true;
            Assert.True(layout.IsVisible("Bpm"));
            settings.ShowGenreColumn = false;
            Assert.False(layout.IsVisible("Genre"));

            layout.SetWidth("Title", 320);
            layout.Move("Year", 0);
            layout.Commit();
            Assert.Equal("Title=320", settings.SongsColumnWidths);
            Assert.StartsWith("Year,Title", settings.SongsColumnOrder);
            await settings.SaveAsync();
        }

        var reloaded = NewSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.ShowYearColumn);
        var again = LibraryColumns.CreateSongs();
        using (LibraryColumns.BindSongs(again, reloaded))
        {
            Assert.Equal(320, again.UserWidth("Title"));
            Assert.Equal("Year", again.Visible[0].Key);
            Assert.False(again.IsVisible("Genre"));

            // Settings ▸ Reset puts the columns back too.
            reloaded.ResetSettingsToDefaults(new AppSettings());
            Assert.Null(again.UserWidth("Title"));
            Assert.Equal("Title", again.Visible[0].Key);
            Assert.False(again.IsVisible("Year"));
            Assert.True(again.IsVisible("Genre"));
        }
    }

    [AvaloniaFact]
    public async Task ArtistsLayout_OwnsItsHiddenColumns_AndPersistsThem()
    {
        var settings = NewSettings();
        await settings.LoadAsync();
        var layout = LibraryColumns.CreateArtists();
        using (LibraryColumns.BindArtists(layout, settings))
        {
            layout.RequestVisibility("Genre", false);
            layout.SetWidth("Songs", 120);
            layout.Commit();
            Assert.Equal("Genre", settings.ArtistListHiddenColumns);
            Assert.Equal("Songs=120", settings.ArtistListColumnWidths);
            await settings.SaveAsync();
        }
        var reloaded = NewSettings();
        await reloaded.LoadAsync();
        var again = LibraryColumns.CreateArtists();
        using (LibraryColumns.BindArtists(again, reloaded))
        {
            Assert.False(again.IsVisible("Genre"));
            Assert.Equal(120, again.UserWidth("Songs"));
        }
    }

    // ── Songs view (headless) ─────────────────────────────────────────────

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Settle(Window window, int passes = 6)
    {
        for (var i = 0; i < passes; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static (LibrarySongsViewModel Vm, LibrarySongsView View, Window Win) MountSongs(int count)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < count; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i:D5}", Artist = "Artist", AlbumArtist = "Artist", Album = "Album",
                AlbumId = Guid.NewGuid(), Genre = "Rock", Year = 1999, Composer = "Writer", Codec = "FLAC",
                Duration = TimeSpan.FromSeconds(180 + i % 60), FilePath = $"C:/m/{i}.flac",
                DateAdded = new DateTime(2024, 1, 1).AddMinutes(i),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        vm.Refresh();
        // The sort runs off the UI thread; a loaded machine (full suite) can take a while on
        // 50k tracks. Bounded wait, so a broken load fails instead of hanging.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (vm.FilteredTracks.Count != count && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Settle(win, 8);
        return (vm, view, win);
    }

    private static ColumnHeaderPanel Header(LibrarySongsView view) => view.FindControl<ColumnHeaderPanel>("ColumnHeader")!;

    private static List<ColumnsPanel> RowPanels(LibrarySongsView view) =>
        view.TrackList.GetRealizedContainers().OfType<ListBoxItem>()
            .Select(i => i.GetVisualDescendants().OfType<ColumnsPanel>().First()).ToList();

    private static Control Cell(Panel panel, string key) => panel.Children.First(c => ColumnsPanel.GetColumn(c) == key);

    private static Rect InWindow(Control c, Window win)
    {
        var p = c.TranslatePoint(default, win)!.Value;
        return new Rect(p, c.Bounds.Size);
    }

    /// <summary>
    /// Header and every realized row are the same width at the same x, so the shared layout
    /// gives both the same columns; each row cell fills exactly its column's slot.
    /// </summary>
    private static void AssertAligned(LibrarySongsView view, Window win)
    {
        var header = Header(view);
        var rows = RowPanels(view);
        Assert.NotEmpty(rows);
        var hx = header.TranslatePoint(default, win)!.Value.X;
        var (widths, offsets) = view.Columns.Compute(header.Bounds.Width);
        foreach (var row in rows)
        {
            Assert.Equal(hx, row.TranslatePoint(default, win)!.Value.X, 0.5);
            Assert.Equal(header.Bounds.Width, row.Bounds.Width, 0.5);
            for (var i = 0; i < view.Columns.Visible.Count; i++)
            {
                var spec = view.Columns.Visible[i];
                var c = Cell(row, spec.Key);
                Assert.True(c.IsVisible, spec.Key);
                if (c is Border)
                {
                    Assert.Equal(offsets[i], c.Bounds.X, 0.5);
                    Assert.Equal(widths[i], c.Bounds.Width, 0.5);
                }
                // The header cell sits inside the same slot.
                if (!spec.Pinned)
                {
                    var h = Cell(header, spec.Key);
                    Assert.InRange(h.Bounds.X, offsets[i] - 0.5, offsets[i] + widths[i]);
                    Assert.True(h.Bounds.Right <= offsets[i] + widths[i] + 0.5, spec.Key);
                }
            }
        }
    }

    [AvaloniaFact]
    public void SongsView_SortedColumn_ShowsItsArrowBesideTheLabel()
    {
        var (vm, view, win) = MountSongs(40);
        try
        {
            vm.SortColumn = "Time";
            vm.SortAscending = true;
            Settle(win);
            var header = Header(view);
            var arrow = header.SortArrow;
            var cell = Cell(header, "Time");
            var probe = $"vis={arrow.IsVisible} eff={arrow.IsEffectivelyVisible} bounds={arrow.Bounds} cell={cell.Bounds} fill={arrow.Fill} data={arrow.Data != null} key={header.SortKey}";
            Assert.True(arrow.IsEffectivelyVisible, probe);
            Assert.NotNull(arrow.Fill);
            Assert.True(arrow.Bounds.Width > 0 && arrow.Bounds.Height > 0, probe);
            Assert.True(arrow.Bounds.Left >= cell.Bounds.Right, probe); // after the label, not on it
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongsView_HeaderAndRowsShareTheLayout_HiddenColumnsHaveNoCells_ColumnsComeAndGo()
    {
        var (_, view, win) = MountSongs(40);
        try
        {
            AssertAligned(view, win);
            var row = RowPanels(view)[0];
            Assert.False(Cell(row, "Bpm").IsVisible);
            Assert.False(Cell(Header(view), "Year").IsVisible);

            view.Columns.SetVisible("Year", true);
            view.Columns.SetVisible("Composer", true);
            view.Columns.SetVisible("Format", true);
            view.Columns.SetVisible("DateAdded", true);
            view.Columns.SetVisible("Album", false);
            Settle(win);
            AssertAligned(view, win);
            row = RowPanels(view)[0];
            Assert.False(Cell(row, "Album").IsVisible);
            var texts = row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("1999", texts);
            Assert.Contains("Writer", texts);
            Assert.Contains("FLAC", texts);
            Assert.Contains(new DateTime(2024, 1, 1).AddMinutes(39).ToLocalTime().ToString("d"), texts);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongsView_LargeLibrary_RealizesAFewRows_AllReadingOneLayout()
    {
        var (vm, view, win) = MountSongs(50_000);
        try
        {
            Assert.Equal(50_000, vm.FilteredTracks.Count);
            var rows = RowPanels(view);
            Assert.InRange(rows.Count, 5, 80);
            Assert.All(rows, r => Assert.Same(view.Columns, ColumnsPanel.GetLayout(r)));
            view.Columns.SetWidth("Title", 400);
            Settle(win);
            Assert.All(RowPanels(view), r => Assert.Equal(400, Cell(r, "Title").Bounds.Width, 0.5));
            AssertAligned(view, win);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongsView_DragADivider_Resizes_DoubleClickGoesBackToAuto()
    {
        var (_, view, win) = MountSongs(20);
        try
        {
            var header = Header(view);
            var before = W(view.Columns, header.Bounds.Width, "Title");
            var grip = header.GripFor("Title")!;
            var g = InWindow(grip, win);
            var start = g.Center;
            win.MouseMove(start);
            win.MouseDown(start, MouseButton.Left);
            win.MouseMove(start + new Point(30, 0));
            win.MouseMove(start + new Point(60, 0));
            win.MouseUp(start + new Point(60, 0), MouseButton.Left);
            Settle(win);
            Assert.Equal(Math.Round(before + 60), view.Columns.UserWidth("Title"));
            Assert.Equal(Math.Round(before + 60), W(view.Columns, header.Bounds.Width, "Title"), 0.5);
            AssertAligned(view, win);

            g = InWindow(header.GripFor("Title")!, win);
            win.MouseDown(g.Center, MouseButton.Left);
            win.MouseUp(g.Center, MouseButton.Left);
            win.MouseDown(g.Center, MouseButton.Left);
            win.MouseUp(g.Center, MouseButton.Left);
            Settle(win);
            Assert.Null(view.Columns.UserWidth("Title"));
            Assert.Equal(before, W(view.Columns, header.Bounds.Width, "Title"), 0.5);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongsView_HeaderClickSorts_WithAnArrow_DragMovesTheColumnWithoutSorting()
    {
        var (vm, view, win) = MountSongs(20);
        try
        {
            var header = Header(view);
            var arrow = header.GetVisualChildren().OfType<Avalonia.Controls.Shapes.Path>().Single();
            Assert.Equal("Date Added", vm.SortColumn);
            Assert.False(arrow.IsVisible); // Date Added is not a shown column

            var genre = InWindow(Cell(header, "Genre"), win);
            var p = new Point(genre.X + 10, genre.Center.Y);
            win.MouseDown(p, MouseButton.Left);
            win.MouseUp(p, MouseButton.Left);
            Settle(win);
            Assert.Equal("Genre", vm.SortColumn);
            Assert.True(vm.SortAscending);
            Assert.True(arrow.IsVisible);
            var arrowRect = InWindow(arrow, win);
            Assert.InRange(arrowRect.X, genre.Right, genre.Right + 12); // just after the label, not on it

            win.MouseDown(p, MouseButton.Left);
            win.MouseUp(p, MouseButton.Left);
            Settle(win);
            Assert.False(vm.SortAscending); // second click flips

            // Drag Genre's header to the far left: the column moves, the sort stays.
            var title = InWindow(Cell(header, "Title"), win);
            win.MouseDown(p, MouseButton.Left);
            win.MouseMove(p + new Point(-20, 0));
            win.MouseMove(new Point(title.X + 5, p.Y));
            win.MouseUp(new Point(title.X + 5, p.Y), MouseButton.Left);
            Settle(win);
            Assert.Equal("Genre", view.Columns.Visible[0].Key);
            Assert.False(vm.SortAscending);
            Assert.Equal("Genre", vm.SortColumn);
            AssertAligned(view, win);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongsView_HeaderMenu_ListsEveryColumnWithTicks_TogglesAndResets()
    {
        var (_, view, win) = MountSongs(10);
        try
        {
            var header = Header(view);
            var menu = header.BuildMenu(view.Columns);
            var items = menu.Items.OfType<MenuItem>().ToList();
            var labels = items.Select(i => i.Header as string).ToList();
            Assert.Contains(Noctis.Localization.Loc.T("Main.Year"), labels);
            Assert.Contains(Noctis.Localization.Loc.T("Columns.Reset"), labels);
            var year = items.Single(i => (string?)i.Header == Noctis.Localization.Loc.T("Main.Year"));
            Assert.Null(year.Icon);
            Assert.NotNull(items.Single(i => (string?)i.Header == Noctis.Localization.Loc.T("LibrarySongs.Genre")).Icon);
            Assert.False(items.Single(i => (string?)i.Header == Noctis.Localization.Loc.T("LibrarySongs.Title")).IsEnabled);

            year.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(view.Columns.IsVisible("Year"));
            Assert.NotNull(year.Icon);

            view.Columns.SetWidth("Title", 333);
            view.Columns.Move("Plays", 0);
            items.Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Null(view.Columns.UserWidth("Title"));
            Assert.Equal("Title", view.Columns.Visible[0].Key);
            Assert.True(view.Columns.IsVisible("Year")); // reset keeps the chosen columns
        }
        finally { win.Close(); }
    }
}
