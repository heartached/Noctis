using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Plugins;
using Noctis.Plugins.AlbumTown.Town;
using Noctis.Plugins.AlbumTown.Views;
using Xunit;
using static Noctis.Tests.AlbumTownPluginTests;

namespace Noctis.Tests;

/// <summary>
/// Album Town with real pointer input: every gesture goes in as mouse events on the window
/// (Avalonia.Headless MouseDown / MouseMove / MouseUp / MouseWheel at window points) and is
/// routed through the visual tree to whatever is under the pointer, at 100% and at 125%, with
/// the app's own styles applied (as the plugin window gets them inside Noctis).
/// </summary>
[Collection("AlbumTown")]
public class AlbumTownInputTests
{
    private static (AlbumTownView View, Window Window, FakeTownHost Host) Open(double scaling, int albums = 30, int playlists = 2)
    {
        var host = new FakeTownHost();
        var genres = new[] { "Hip-Hop/Rap", "Rock", "Pop" };
        for (var i = 0; i < albums; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            for (var n = 0; n < 3; n++)
                host.Tracks.Add(T($"Song {i}-{n}", album: $"Album {i}", artist: $"Artist {i % 9}", plays: (i * 7 + n * 3) % 40, albumId: id,
                    genre: genres[i % genres.Length], number: n + 1, id: $"t{i}-{n}"));
        }
        for (var p = 0; p < playlists; p++)
            host.PlaylistList.Add(new PlaylistInfo("pl" + p, "Mix " + p, null, host.Tracks.Where((_, i) => i % (p + 2) == 0).Take(12).Select(t => t.Id).ToArray()));
        var view = new AlbumTownView(host, new Random(9));
        var town = AlbumGrouping.Group(host.Tracks, out var byFile, out var byTrack);
        view.SetTown(town, byFile, Array.Empty<string>());
        view.SetApartments(host.Tracks, host.PlaylistList, host.LoadApartments(), byTrack);
        EnsureAppStyles();
        // 2560x1440 at 125%, maximized: about 2048x1080 of client area.
        var window = new Window { Content = view, Width = 2048, Height = 1080 };
        window.Show();
        if (Math.Abs(scaling - 1) > 1e-9) window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();
        return (view, window, host);
    }

    /// <summary>The app's resources and styles (smooth scrolling, scroll bars, inertia) as in Noctis.</summary>
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void ShowApartments(AlbumTownView view, Window window)
    {
        Click(window, Center(TabButton(view, "Apartments"), window));
        view.ApartmentsView.Step(1 / 60f);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button TabButton(AlbumTownView view, string name)
        => view.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);

    private static Point Center(Visual v, Visual root) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), root)!.Value;
    private static Point At(Visual v, Point p, Visual root) => v.TranslatePoint(p, root)!.Value;

    private static void Click(TopLevel w, Point p, MouseButton button = MouseButton.Left)
    {
        w.MouseMove(p);
        w.MouseDown(p, button);
        w.MouseUp(p, button);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Press, 30 moves with the button held, release (as the live test did).</summary>
    private static void Drag(Window w, Point from, Point to, int steps = 30, Action? midway = null)
    {
        w.MouseMove(from);
        w.MouseDown(from, MouseButton.Left);
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            w.MouseMove(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            if (i == steps - 1) midway?.Invoke();
        }
        w.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // ── The shelf ──

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void DraggingAShelfCover_IntoTheVoid_BuildsARoom_WithASnappedGhost(double scaling)
    {
        var (view, window, _) = Open(scaling);
        ShowApartments(view, window);
        var canvas = view.ApartmentsView;
        var item = view.Shelf.Items.First(i => i.Kind == RoomKind.Album);
        var from = At(view.Shelf, view.Shelf.TileRect(item)!.Value.Center, window);
        var to = At(canvas, canvas.ToPx(3.5, 2.5), window);
        (Cells, bool)? ghost = null;
        Drag(window, from, to, midway: () => ghost = canvas.Ghost);
        Assert.Equal((new Cells(3, 2, 1, 1), true), ghost);
        var room = Assert.Single(view.Building.Rooms);
        Assert.Equal((RoomKind.Album, item.Source, new Cells(3, 2, 1, 1)), (room.Kind, room.Source, room.Cells));
        Assert.False(canvas.IsDragging);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void DoubleClickingAShelfCover_PlacesIt_AfterScrolling(double scaling)
    {
        var (view, window, _) = Open(scaling, albums: 300);
        ShowApartments(view, window);
        var shelf = view.Shelf;
        var scroll = shelf.FindAncestorOfType<ScrollViewer>()!;
        var first = shelf.Items.First(i => i.Kind == RoomKind.Album);
        var p = At(shelf, shelf.TileRect(first)!.Value.Center, window);
        Click(window, p);
        Click(window, p);
        Assert.Equal(first.Source, Assert.Single(view.Building.Rooms).Source);

        // Scrolled along: the cover under the pointer is the one placed.
        for (var i = 0; i < 10; i++) window.MouseWheel(Center(scroll, window), new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroll.Offset.X > 500);
        var later = shelf.Items.First(i => shelf.TileRect(i)!.Value.X > scroll.Offset.X + 100);
        p = At(shelf, shelf.TileRect(later)!.Value.Center, window);
        Click(window, p);
        Click(window, p);
        Assert.Contains(view.Building.Rooms, r => r.Source == later.Source);
        Assert.Equal(2, view.Building.Rooms.Count);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void TheWholeCoverSlot_TakesTheClick_NotOnlyWhereSomethingIsDrawn(double scaling)
    {
        var (view, window, _) = Open(scaling);
        ShowApartments(view, window);
        var shelf = view.Shelf;
        var item = shelf.Items.First(i => i.Kind == RoomKind.Album); // "Album 0": a short title
        var tile = shelf.TileRect(item)!.Value;
        // Just under the cover, and beside its short title: no ink there, still its slot.
        foreach (var local in new[] { new Point(tile.Center.X, tile.Bottom + 2), new Point(tile.Right - 3, tile.Bottom + 12) })
        {
            var p = At(shelf, local, window);
            Assert.Same(shelf, window.InputHitTest(p));
        }
        var under = At(shelf, new Point(tile.Right - 3, tile.Bottom + 12), window);
        Click(window, under);
        Click(window, under);
        Assert.Equal(item.Source, Assert.Single(view.Building.Rooms).Source);
        window.Close();
    }

    /// <summary>The pointer as the gestures see it (to take its capture away like Windows does
    /// when another window grabs the mouse: WM_CAPTURECHANGED).</summary>
    private static Func<IPointer?> WatchPointer(Window window)
    {
        IPointer? pointer = null;
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        return () => pointer;
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void AShelfDrag_KeepsGoing_WhenThePointerCaptureIsTakenAway(double scaling)
    {
        var (view, window, _) = Open(scaling);
        ShowApartments(view, window);
        var pointer = WatchPointer(window);
        var canvas = view.ApartmentsView;
        var item = view.Shelf.Items.First(i => i.Kind == RoomKind.Album);
        var from = At(view.Shelf, view.Shelf.TileRect(item)!.Value.Center, window);
        var to = At(canvas, canvas.ToPx(5.5, 3.5), window);
        var step = 0;
        (Cells, bool)? ghost = null;
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        for (var i = 1; i <= 30; i++, step++)
        {
            var t = i / 30.0;
            window.MouseMove(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            if (i == 10) pointer()!.Capture(null); // the platform took it
            if (i == 29) ghost = canvas.Ghost;
        }
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((new Cells(5, 3, 1, 1), true), ghost);
        Assert.Equal(new Cells(5, 3, 1, 1), Assert.Single(view.Building.Rooms).Cells);
        Assert.False(canvas.IsDragging);

        // Released where the app could not see it (outside the window): the next move, with no
        // button down, ends the drag and builds nothing.
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        for (var i = 1; i <= 10; i++)
            window.MouseMove(new Point(from.X, from.Y - i * 30), RawInputModifiers.LeftMouseButton);
        pointer()!.Capture(null);
        window.MouseMove(At(canvas, canvas.ToPx(8.5, 3.5), window));
        Dispatcher.UIThread.RunJobs();
        Assert.False(canvas.IsDragging);
        Assert.Single(view.Building.Rooms);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void ARoomDrag_KeepsGoing_WhenThePointerCaptureIsTakenAway(double scaling)
    {
        var (view, window, _) = Open(scaling);
        ShowApartments(view, window);
        var pointer = WatchPointer(window);
        var canvas = view.ApartmentsView;
        var (a, _) = TwoRooms(view);
        var rect = canvas.RoomRect(a.Cells);
        var grab = At(canvas, new Point(rect.X + rect.Width * 0.12, rect.Y + rect.Height * 0.55), window);
        var to = grab + new Vector(canvas.Cell * 2, canvas.Cell);
        window.MouseMove(grab);
        window.MouseDown(grab, MouseButton.Left);
        for (var i = 1; i <= 30; i++)
        {
            window.MouseMove(grab + (to - grab) * (i / 30.0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            if (i == 10) pointer()!.Capture(null);
        }
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Cells(3, 2, 1, 1), a.Cells);
        Assert.False(canvas.IsDragging);
        window.Close();
    }

    // ── Rooms ──

    private static (Room A, Room B) TwoRooms(AlbumTownView view)
    {
        var keys = view.Town.Select(a => a.Key).ToList();
        var a = view.Building.TryAdd(RoomKind.Album, keys[0], 1, 1)!;
        var b = view.Building.TryAdd(RoomKind.Album, keys[1], 4, 1)!;
        view.ApartmentsView.Step(1 / 60f);
        // Pets out of the way of the room's body (they are clickable too).
        foreach (var pet in view.Life.Pets) pet.X = pet.Area.Rect.Right - 0.1;
        Dispatcher.UIThread.RunJobs();
        return (a, b);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void DraggingARoom_MovesIt_AndItsCorner_ResizesIt(double scaling)
    {
        var (view, window, _) = Open(scaling);
        ShowApartments(view, window);
        var canvas = view.ApartmentsView;
        var (a, _) = TwoRooms(view);

        var rect = canvas.RoomRect(a.Cells);
        var grab = new Point(rect.X + rect.Width * 0.12, rect.Y + rect.Height * 0.55);
        (Cells, bool)? ghost = null;
        Drag(window, At(canvas, grab, window), At(canvas, grab + new Vector(canvas.Cell * 2, canvas.Cell), window), midway: () => ghost = canvas.Ghost);
        Assert.Equal((new Cells(3, 2, 1, 1), true), ghost);
        Assert.Equal(new Cells(3, 2, 1, 1), a.Cells);

        rect = canvas.RoomRect(a.Cells);
        Drag(window, At(canvas, new Point(rect.Right - 4, rect.Bottom - 4), window), At(canvas, canvas.ToPx(5.1, 4.1), window));
        Assert.Equal(new Cells(3, 2, 2, 2), a.Cells);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void TheRoomMenu_OpensFromItsButton_AndARightClick_AndItsItemsWork(double scaling)
    {
        var (view, window, host) = Open(scaling);
        ShowApartments(view, window);
        var canvas = view.ApartmentsView;
        var (a, b) = TwoRooms(view);

        var rect = canvas.RoomRect(a.Cells);
        var (button, _) = RoomPainter.MenuButton(rect.Size, canvas.Cell);
        Click(window, At(canvas, new Point(rect.X + button.X, rect.Y + button.Y), window));
        var menu = Assert.IsType<ContextMenu>(view.RoomMenuShown);
        Assert.True(menu.IsOpen);
        var play = menu.GetLogicalDescendants().OfType<MenuItem>().Single(m => (string?)m.Header == "Play album");
        var popup = TopLevel.GetTopLevel(play)!;
        Click(popup, play.TranslatePoint(new Point(play.Bounds.Width / 2, play.Bounds.Height / 2), popup)!.Value);
        Assert.Equal(a.Source, Assert.Single(host.Played).Key);
        // The menu closes (the app's menus fade out, so give it a moment).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (menu.IsOpen && sw.ElapsedMilliseconds < 3000) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(5); }
        Assert.False(menu.IsOpen);

        // Right-click anywhere on a room: its menu; "Remove room" removes it.
        rect = canvas.RoomRect(b.Cells);
        Click(window, At(canvas, new Point(rect.X + rect.Width * 0.12, rect.Y + rect.Height * 0.55), window), MouseButton.Right);
        menu = Assert.IsType<ContextMenu>(view.RoomMenuShown);
        Assert.True(menu.IsOpen);
        var remove = menu.GetLogicalDescendants().OfType<MenuItem>().Single(m => (string?)m.Header == "Remove room");
        popup = TopLevel.GetTopLevel(remove)!;
        Click(popup, remove.TranslatePoint(new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2), popup)!.Value);
        Assert.Equal(new[] { a }, view.Building.Rooms);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void ClickingAPet_OpensItsCard(double scaling)
    {
        var (view, window, _) = Open(scaling);
        ShowApartments(view, window);
        var canvas = view.ApartmentsView;
        view.Building.TryAdd(RoomKind.Album, view.Town[0].Key, 2, 2);
        canvas.Step(1 / 60f);
        var pet = Assert.Single(view.Life.Pets);
        Click(window, At(canvas, canvas.ToPx(pet.X, pet.Y - pet.Size * 0.8), window));
        Assert.True(view.IsCardOpen);
        Assert.Same(pet.Album, view.CardAlbum);
        window.Close();
    }

    // ── The plaza ──

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void ThePlaza_ClickHoverAndCtrlWheel(double scaling)
    {
        var (view, window, _) = Open(scaling, albums: 40);
        var plaza = view.Plaza;
        plaza.ManualFrames = true;
        plaza.Step(1 / 60f);
        plaza.Sync();
        var c = plaza.World.Creatures[3];
        var at = PlazaWorld.BodyCenter(c, plaza.World.BodySize);
        var p = At(plaza, new Point(at.X, at.Y), window);

        window.MouseMove(p);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(c, plaza.Hovered);

        Click(window, p);
        Assert.True(view.IsCardOpen);
        Assert.Same(c.Album, view.CardAlbum);

        var zoom = plaza.Zoom;
        window.MouseWheel(At(plaza, new Point(20, 20), window), new Vector(0, 1), RawInputModifiers.Control);
        Assert.Equal(Math.Round((zoom + 0.1) * 10) / 10, plaza.Zoom, 3);
        Assert.Equal(plaza.Zoom, view.ZoomSlider.Value, 3);
        window.Close();
    }
}
