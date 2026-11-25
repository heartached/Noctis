using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #218: Settings › Appearance "Album Hover Zoom" (off by default). With it on, the
/// album under the pointer in the Albums grid grows a little with a glow; render transforms
/// only, so no tile moves in layout. With it off, nothing. GitHub #250 / Discord 11-15: the
/// tiles around it used to step back to 97%, read as the whole grid moving; now no tile but
/// the hovered one carries any transform.
/// </summary>
public class AlbumHoverZoomTests
{
    private readonly ITestOutputHelper _o;
    public AlbumHoverZoomTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static double Scale(Visual v) => v.RenderTransform is TransformOperations ops ? ops.Value.M11 : v.RenderTransform?.Value.M11 ?? 1;

    /// <summary>The part of a tile that zooms (Discord 11-21): its cover and the glow behind it.</summary>
    private static Panel Art(Button tile) => tile.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("tile-art"));

    private static Border Glow(Button tile) => tile.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tile-glow"));

    /// <summary>No transform, or one within 0.1% of none: a tile just left eases back to 100% on
    /// a curve whose tail the headless clock leaves a hair above 1 (a stepped-back tile was 97%).</summary>
    private static bool IsIdentity(Visual v)
    {
        if (v.RenderTransform is not { } t) return true;
        var m = t.Value;
        return Math.Abs(m.M11 - 1) < 1e-3 && Math.Abs(m.M22 - 1) < 1e-3 && Math.Abs(m.M12) < 1e-6 && Math.Abs(m.M21) < 1e-6
            && Math.Abs(m.M31) < 0.5 && Math.Abs(m.M32) < 0.5;
    }

    /// <summary>Indexes of the tiles (other than <paramref name="hovered"/>) whose tile or cover
    /// carries a transform.</summary>
    private static List<int> Transformed(List<Button> tiles, Button? hovered) =>
        tiles.Where(t => !ReferenceEquals(t, hovered) && (!IsIdentity(t) || !IsIdentity(Art(t)))).Select(t => tiles.IndexOf(t)).ToList();

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HoveredAlbum_GrowsAndGlows_NoOtherTileMoves_OnlyWhenOn(bool on)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 30; i++)
            ((List<Album>)lib.Albums).Add(new Album { Id = Guid.NewGuid(), Name = $"Album {i:00}", Artist = "Artist", Year = 2020, Tracks = new List<Track>() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        Assert.False(settings.AlbumHoverZoom); // off by default
        settings.AlbumHoverZoom = on;
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 1400, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        List<Button> Tiles() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && Tiles().Count < 15) { Pump(1); await Task.Delay(5); }
        Pump(4);

        var tiles = Tiles().OrderBy(t => t.TranslatePoint(default, view)!.Value.Y).ThenBy(t => t.TranslatePoint(default, view)!.Value.X).ToList();
        var cols = vm.GridColumns;
        var hovered = tiles[cols + 2];           // second row, middle
        var bounds = tiles.Select(t => t.Bounds).ToList();

        var p = hovered.TranslatePoint(new Point(hovered.Bounds.Width / 2, 40), win)!.Value;
        win.MouseMove(p); Pump(2);
        await Task.Delay(350); Pump(3);

        var glow = Glow(hovered);
        _o.WriteLine($"on={on}: hovered scale={Scale(Art(hovered)):0.###} glow={glow.Opacity:0.##} others transformed=[{string.Join(",", Transformed(tiles, hovered))}]");
        if (on)
        {
            Assert.Equal(1.04, Scale(Art(hovered)), 2);
            Assert.True(IsIdentity(hovered)); // the cover grows, not the whole tile
            Assert.Equal(1, glow.Opacity, 2);
        }
        else
        {
            Assert.Equal(1, Scale(Art(hovered)), 2);
            Assert.Equal(0, glow.Opacity, 2);
        }
        // GitHub #250: no tile but the hovered one is transformed in any way.
        Assert.Empty(Transformed(tiles, hovered));
        // Render transforms only: nothing moved in layout.
        Assert.Equal(bounds, tiles.Select(t => t.Bounds).ToList());

        // Leaving the grid lets everything settle back.
        win.MouseMove(new Point(5, 1395)); Pump(2);
        await Task.Delay(350); Pump(3);
        Assert.Equal(1, Scale(Art(hovered)), 2);
        Assert.Empty(Transformed(tiles, null));
        win.Close();
    }

    /// <summary>Discord 11-15 ("hovering makes the albums next to it smaller") / GitHub #250: with
    /// either spacing, sliding across two tiles moves no layout slot and transforms no tile but
    /// the hovered one (a tile just left may still be easing back from its grow, never below
    /// 100%), hovers exactly one tile at a time (no flicker while the pointer rests) and a wheel
    /// scroll leaves the zoom on the tile actually under the pointer.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossingAndScrolling_MoveNoLayoutSlot_AndHoverFollowsThePointer(bool wide)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 60; i++)
            ((List<Album>)lib.Albums).Add(new Album { Id = Guid.NewGuid(), Name = $"Album {i:00}", Artist = "Artist", Year = 2020, Tracks = new List<Track>() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumHoverZoom = true;
        settings.AlbumGridWideSpacing = wide;
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        List<Button> Tiles() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && Tiles().Count < 15) { Pump(1); await Task.Delay(5); }
        Pump(4);
        var tiles = Tiles().OrderBy(t => t.TranslatePoint(default, view)!.Value.Y).ThenBy(t => t.TranslatePoint(default, view)!.Value.X).ToList();
        // Layout slot = the tile's own Bounds and where its (untransformed) container sits.
        List<(Rect, Point)> Slots() => tiles.Select(t => (t.Bounds, ((Visual)t.GetVisualParent()!).TranslatePoint(default, view)!.Value)).ToList();
        var slots = Slots();

        var a = tiles[vm.GridColumns + 1];
        var y = a.TranslatePoint(new Point(0, 60), win)!.Value.Y;
        var x0 = a.TranslatePoint(new Point(a.Bounds.Width - 20, 0), win)!.Value.X;
        List<int> Hovered() => tiles.Where(t => t.IsPointerOver).Select(t => tiles.IndexOf(t)).ToList();
        var seen = new List<int>();
        for (var x = x0; x < x0 + 40; x += 2)
        {
            win.MouseMove(new Point(x, y)); Pump(1);
            for (var f = 0; f < 8; f++) { await Task.Delay(16); Pump(1); }
            var h = Hovered();
            // Wide spacing leaves a 6px gap between tiles: nothing hovered there.
            Assert.True(h.Count <= 1, $"two tiles hovered at {x - x0}");
            if (h.Count == 1 && (seen.Count == 0 || seen[^1] != h[0])) seen.Add(h[0]);
            var others = Transformed(tiles, h.Count == 1 ? tiles[h[0]] : null);
            Assert.True(others.All(seen.Contains), $"tiles never hovered transformed at {x - x0}: {string.Join(",", others)}");
            Assert.All(others, i => Assert.True(Scale(Art(tiles[i])) >= 1 - 1e-9, $"tile {i} shrank to {Scale(Art(tiles[i])):0.###}"));
        }
        _o.WriteLine($"wide={wide}: hovered along the sweep {string.Join(" -> ", seen)}");
        Assert.Equal(new[] { vm.GridColumns + 1, vm.GridColumns + 2 }, seen);

        // Resting pointer: no flicker between tiles while the transforms settle.
        var rest = Hovered();
        for (var f = 0; f < 30; f++) { await Task.Delay(16); Pump(1); Assert.Equal(rest, Hovered()); }
        Assert.Equal(slots, Slots());
        Assert.Empty(Transformed(tiles, rest.Count == 1 ? tiles[rest[0]] : null));

        // A wheel scroll under a still pointer: the zoom moves to the tile now under it.
        var p = new Point(x0 + 80, y);
        win.MouseMove(p); Pump(2); await Task.Delay(300); Pump(3);
        win.MouseWheel(p, new Vector(0, -3)); Pump(2); await Task.Delay(300); Pump(3);
        Button? under = null;
        for (var v = win.GetVisualAt(p) as Visual; v != null; v = v.GetVisualParent())
            if (v is Button b && b.Classes.Contains("album-tile")) { under = b; break; }
        Assert.NotNull(under);
        var over = Tiles().Where(t => t.IsPointerOver).ToList();
        Assert.Equal(new[] { under }, over);
        // A tile the pointer crossed while the grid scrolled eases back; then only `under` is grown.
        var all = Tiles();
        Assert.All(Transformed(all, under), i => Assert.True(Scale(Art(all[i])) >= 1 - 1e-9, $"tile {i} shrank to {Scale(Art(all[i])):0.###}"));
        for (var f = 0; f < 60 && Transformed(all, under).Count > 0; f++) { await Task.Delay(16); Pump(1); }
        Assert.Empty(Transformed(all, under));
        Assert.Equal(1.04, Scale(Art(under!)), 2);
        win.Close();
    }

    /// <summary>A wide window makes big covers: grown from its centre, a first-row or edge-column
    /// tile ran past the scroller's viewport and lost ~5px of cover (2400px: top-left cover at
    /// -4.3,-5.4, right column 4px past the viewport). It now grows away from those edges. And
    /// the hovered tile is drawn above its neighbours: in the row grid its .hover-zoom ZIndex
    /// sorted nothing (the Button is its TileRow presenter's only child), so its row and
    /// presenter are lifted. Holds in the row grid and in Masonry, with either spacing.</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EdgeTiles_GrowInsideTheViewport_AndTheHoveredTileIsOnTop(bool wide, bool masonry)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 60; i++)
            ((List<Album>)lib.Albums).Add(new Album { Id = Guid.NewGuid(), Name = $"Album {i:00}", Artist = "Artist", Year = 2020, Tracks = new List<Track>() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumHoverZoom = true;
        settings.AlbumGridWideSpacing = wide;
        settings.AlbumGridMasonry = masonry;
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 2400, Height = 1400, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        List<Button> Tiles() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible).ToList();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && Tiles().Count < 15) { Pump(1); await Task.Delay(5); }
        Pump(4);
        Assert.Equal(masonry, vm.IsMasonryView);
        var tiles = Tiles().OrderBy(t => t.TranslatePoint(default, view)!.Value.Y).ThenBy(t => t.TranslatePoint(default, view)!.Value.X).ToList();
        var sv = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.IsEffectivelyVisible);
        var cols = vm.GridColumns;

        // Top-left, top-right and a tile in the middle of the second row.
        foreach (var idx in new[] { 0, cols - 1, cols + 1 })
        {
            var t = tiles[idx];
            var p = t.TranslatePoint(new Point(t.Bounds.Width / 2, 40), win)!.Value;
            win.MouseMove(p); Pump(2); await Task.Delay(350); Pump(3);
            Assert.Equal(1.04, Scale(Art(t)), 2);
            Assert.Empty(Transformed(tiles, t));

            var cover = t.GetVisualDescendants().OfType<Border>().First(b => b.ClipToBounds);
            var tl = cover.TranslatePoint(default, sv)!.Value;
            var br = cover.TranslatePoint(new Point(cover.Bounds.Width, cover.Bounds.Height), sv)!.Value;
            _o.WriteLine($"wide={wide} masonry={masonry} tile {idx}: cover ({tl.X:0.#},{tl.Y:0.#})-({br.X:0.#},{br.Y:0.#}) viewport {sv.Viewport.Width:0.#}");
            Assert.True(tl.X >= -0.01 && tl.Y >= -0.01 && br.X <= sv.Viewport.Width + 0.01, $"tile {idx} cover clipped by the viewport");

            // Drawn above every neighbour: walk up to the scroller, the hovered branch is the
            // highest ZIndex among its siblings at each panel level.
            for (Visual v = t; v is not ScrollContentPresenter && v.GetVisualParent() is Visual parent; v = parent)
            {
                if (parent is not Panel panel || panel.Children.Count < 2) continue;
                Assert.True(panel.Children.Where(c => c != v).All(c => c.ZIndex < v.ZIndex),
                    $"tile {idx}: {v.GetType().Name} not above its siblings in {parent.GetType().Name}");
            }
        }
        win.Close();
    }

    /// <summary>Discord 11-21 (Halil, 1.7.1): the whole tile (cover, title, "artist · year") grew
    /// as one block, so the cover grew away from its own middle and the text lines moved; and the
    /// glow sat on the Button's template, which an Avalonia 12 TemplatedControl clips to its
    /// bounds, so only a faint hard-edged box round the whole tile was left of it. Now only the
    /// cover grows, round its centre (inward at the grid's edges), the title lines stay where they
    /// were, nothing between the glow and the scroller clips, and where the scroller's own edge
    /// would cut the glow it is not shown at all. Row grid and Masonry, either spacing.</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CoverGrowsRoundItsCentre_TextStays_GlowWholeOrNone(bool wide, bool masonry)
    {
        // The premise of the glow fix: a TemplatedControl clips to its bounds unless told not to.
        Assert.True(new Button().ClipToBounds);

        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 60; i++)
            ((List<Album>)lib.Albums).Add(new Album { Id = Guid.NewGuid(), Name = $"Album {i:00}", Artist = "Artist", Year = 2020, Tracks = new List<Track>() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumHoverZoom = true;
        settings.AlbumGridWideSpacing = wide;
        settings.AlbumGridMasonry = masonry;
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 1200, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        List<Button> Tiles() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible).ToList();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && Tiles().Count < 15) { Pump(1); await Task.Delay(5); }
        Pump(4);
        var tiles = Tiles().OrderBy(t => t.TranslatePoint(default, view)!.Value.Y).ThenBy(t => t.TranslatePoint(default, view)!.Value.X).ToList();
        var cols = vm.GridColumns;

        Border Cover(Button t) => t.GetVisualDescendants().OfType<Border>().First(b => b.ClipToBounds);
        Point Centre(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), view)!.Value;
        List<Point> TextAt(Button t) => t.GetVisualDescendants().OfType<Noctis.Controls.HighlightTextBlock>()
            .Select(x => x.TranslatePoint(default, view)!.Value).ToList();

        // A middle tile of the second row: room all round.
        var mid = tiles[cols + 1];
        var centre = Centre(Cover(mid));
        var text = TextAt(mid);
        Assert.NotEmpty(text);
        var p = mid.TranslatePoint(new Point(mid.Bounds.Width / 2, 40), win)!.Value;
        win.MouseMove(p); Pump(2); await Task.Delay(350); Pump(3);
        Assert.Equal(1.04, Scale(Art(mid)), 2);
        Assert.True(IsIdentity(mid), "the whole tile was transformed");
        var grown = Centre(Cover(mid));
        _o.WriteLine($"wide={wide} masonry={masonry}: cover centre {centre} -> {grown}, cover {Cover(mid).Bounds.Size}");
        Assert.True(Math.Abs(grown.X - centre.X) < 0.5 && Math.Abs(grown.Y - centre.Y) < 0.5, $"cover centre moved {centre} -> {grown}");
        Assert.Equal(text, TextAt(mid));
        // The glow is shown, sized to the cover, and nothing up to the scroller clips it.
        var glow = Glow(mid);
        Assert.Equal(1, glow.Opacity, 2);
        Assert.Equal(Cover(mid).Bounds.Size, glow.Bounds.Size);
        for (var v = glow.GetVisualParent(); v is not null and not ScrollContentPresenter; v = v.GetVisualParent())
            Assert.False(v.ClipToBounds, $"{v.GetType().Name} clips the hovered cover's glow");

        // The top-left tile: grows inside the viewport (inward where it has no room; wide spacing
        // leaves room), and the viewport's edge would cut its glow, so none.
        var corner = tiles[0];
        p = corner.TranslatePoint(new Point(corner.Bounds.Width / 2, 40), win)!.Value;
        win.MouseMove(p); Pump(2); await Task.Delay(350); Pump(3);
        Assert.Equal(1.04, Scale(Art(corner)), 2);
        var sv = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.IsEffectivelyVisible);
        var tl = Cover(corner).TranslatePoint(default, sv)!.Value;
        Assert.True(tl.X >= -0.01 && tl.Y >= -0.01, $"top-left cover clipped at {tl}");
        Assert.Contains("glow-cut", corner.Classes);
        Assert.Equal(0, Glow(corner).Opacity, 2);
        win.Close();
    }

    /// <summary>Discord 11-23 ("the hover thingy for albums doesn't apply for songs here"): the
    /// same zoom on the Favorites grid, songs and albums alike. Only the hovered cover grows,
    /// round its own centre, with its glow; the title lines stay put; no other tile moves; a
    /// Ctrl-selected tile's plate does not grow with it. Off, nothing.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Favorites_HoveredCover_GrowsAndGlows_TextStays_OnlyWhenOn(bool on)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 20; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i:00}", Artist = "Artist", Album = $"Single {i}", AlbumArtist = "Artist",
                FilePath = $"/music/{i}.mp3", Duration = TimeSpan.FromMinutes(3), IsFavorite = true, AlbumId = Guid.NewGuid(),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumHoverZoom = on;
        var vm = new FavoritesViewModel(player, lib, persistence, new SidebarViewModel(persistence, lib), settings);
        vm.Refresh();
        var view = new FavoritesView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 1000, Content = view };
        win.Show(); Pump();
        List<Button> Tiles() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible).ToList();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && Tiles().Count < 15) { Pump(1); await Task.Delay(5); }
        Pump(4);
        var tiles = Tiles().OrderBy(t => t.TranslatePoint(default, view)!.Value.Y).ThenBy(t => t.TranslatePoint(default, view)!.Value.X).ToList();
        var cols = vm.GridColumns;

        Border Cover(Button t) => t.GetVisualDescendants().OfType<Border>().First(b => b.ClipToBounds);
        Point Centre(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), view)!.Value;
        List<Point> TextAt(Button t) => t.GetVisualDescendants().OfType<Noctis.Controls.HighlightTextBlock>()
            .Select(x => x.TranslatePoint(default, view)!.Value).ToList();

        // Ctrl-select the tile, then hover it: a middle tile of the second row.
        var mid = tiles[cols + 1];
        var bounds = tiles.Select(t => t.Bounds).ToList();
        var centre = Centre(Cover(mid));
        var text = TextAt(mid);
        var p = mid.TranslatePoint(new Point(mid.Bounds.Width / 2, 40), win)!.Value;
        win.MouseDown(p, Avalonia.Input.MouseButton.Left, TestKeys.Primary);
        win.MouseUp(p, Avalonia.Input.MouseButton.Left, TestKeys.Primary);
        win.MouseMove(p); Pump(2); await Task.Delay(350); Pump(3);
        Assert.Contains("ctrl-selected", mid.Classes);

        var ring = mid.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("select-ring"));
        _o.WriteLine($"on={on}: scale={Scale(Art(mid)):0.###} glow={Glow(mid).Opacity:0.##} ring={Scale(ring):0.###} others=[{string.Join(",", Transformed(tiles, mid))}]");
        if (on)
        {
            Assert.Equal(1.04, Scale(Art(mid)), 2);
            Assert.Equal(1, Glow(mid).Opacity, 2);
            var grown = Centre(Cover(mid));
            Assert.True(Math.Abs(grown.X - centre.X) < 0.5 && Math.Abs(grown.Y - centre.Y) < 0.5, $"cover centre moved {centre} -> {grown}");
        }
        else
        {
            Assert.Equal(1, Scale(Art(mid)), 2);
            Assert.Equal(0, Glow(mid).Opacity, 2);
        }
        Assert.True(IsIdentity(mid), "the whole tile was transformed");
        Assert.Equal(1, Scale(ring), 2);        // the selection plate keeps its size
        Assert.Equal(text, TextAt(mid));        // the title lines stay put
        Assert.Empty(Transformed(tiles, mid));  // no other tile moves
        Assert.Equal(bounds, tiles.Select(t => t.Bounds).ToList());
        win.Close();
    }
}
