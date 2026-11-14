using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Xunit;
using Track = Noctis.Models.Track;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: the album page's Link Tracks From ▸ (Discord 11-11) "looks very cluttered" and
/// should show the E for explicit albums. Rows: cover, title (trimmed) with the E right after
/// it, the song count quiet and right-aligned; albums and EPs first, then singles, each by
/// title, split by a separator; a fixed-width card that scrolls past ~9 rows. With
/// NOCTIS_TEST_SKIA=1 the probe saves the real submenu card as PNGs (NOCTIS_RENDER_OUT or
/// D:\NoctisWT\art-1114\linkmenu2).
/// </summary>
public class AlbumLinkMenuTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-linkmenu-" + Guid.NewGuid().ToString("N"));

    public AlbumLinkMenuTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static Album MakeAlbum(string name, int songs, bool explicitAlbum = false, string? art = null)
    {
        var id = Track.ComputeAlbumId("Bad Bunny", name);
        var tracks = Enumerable.Range(1, songs).Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"{name} {i}", Album = name, AlbumArtist = "Bad Bunny", Artist = "Bad Bunny",
            TrackNumber = i, AlbumId = id, IsExplicit = explicitAlbum && i == 1,
        }).ToList();
        return new Album { Id = id, Name = name, Artist = "Bad Bunny", Tracks = tracks, TrackCount = songs, ArtworkPath = art };
    }

    /// <summary>The owner's screenshot list (Bad Bunny), shuffled, plus a long explicit album title.</summary>
    private List<Album> Library(bool withArt = false)
    {
        var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42", "#1B998B", "#C5D86D" };
        var n = 0;
        string? Art() => withArt ? CoverFile(colors[n++ % colors.Length]) : null;
        return new List<Album>
        {
            MakeAlbum("Soy Peor - Single", 1, true, Art()),
            MakeAlbum("YHLQMDLG", 20, true, Art()),
            MakeAlbum("6 Rings - Single", 1, false, Art()),
            MakeAlbum("DeBÍ TiRAR MáS FOToS", 17, true, Art()),
            MakeAlbum("ALAMBRE PúA - Single", 1, true, Art()),
            MakeAlbum("Amorfoda - Single", 1, false, Art()),
            MakeAlbum("Un Verano Sin Ti", 23, true, Art()),
            MakeAlbum("Chambea - Single", 1, true, Art()),
            MakeAlbum("EL ÚLTIMO TOUR DEL MUNDO", 16, true, Art()),
            MakeAlbum("De Museo - Single", 1, false, Art()),
            MakeAlbum("LAS QUE NO IBAN A SALIR", 10, true, Art()),
            MakeAlbum("Me Llueven 3.0 (feat. Kevin Roldan, Nengo Flow & Ñengo) - Single", 1, true, Art()),
            MakeAlbum("nadie sabe lo que va a pasar mañana", 22, true, Art()),
            MakeAlbum("Pa Ti (feat. Bryant Myers) - Single", 1, false),
            MakeAlbum("Panamera (Remix) [feat. Arcángel, Ñengo Flow & De La Ghetto] - Single", 1, true, Art()),
            MakeAlbum("Solo de Mí - Single", 1, false, Art()),
            MakeAlbum("Tú No Metes Cabra (feat. Daddy Yankee, Anuel AA & Cosculluela) [Remix] - Single", 1, true, Art()),
            MakeAlbum("UN PREVIEW - Single", 1, true, Art()),
            MakeAlbum("Una Velita - Single", 1, false, Art()),
            MakeAlbum("Up Next (Live From Apple Piazza Liberty) - EP", 4, false, Art()),
            MakeAlbum("Vete - Single", 1, true, Art()),
            MakeAlbum("WHERE SHE GOES - Single", 1, false),
            MakeAlbum("Yonaguni - Single", 1, false, Art()),
            MakeAlbum("X 100PRE (Edición Especial Extendida Para Coleccionistas)", 15, true, Art()),
        };
    }

    private string CoverFile(string hex)
    {
        var path = Path.Combine(_root, hex.TrimStart('#') + ".png");
        if (File.Exists(path)) return path;
        var c = Color.Parse(hex);
        using var bmp = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            var px = new byte[64 * 64 * 4];
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                {
                    var o = (y * 64 + x) * 4;
                    var shade = 1.0 - (x + y) / 256.0;
                    px[o] = (byte)(c.B * shade); px[o + 1] = (byte)(c.G * shade); px[o + 2] = (byte)(c.R * shade); px[o + 3] = 255;
                }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, fb.Address, px.Length);
        }
        bmp.Save(path);
        return path;
    }

    private static TextBlock Title(MenuItem row) =>
        ((Grid)((Grid)row.Header!).Children[0]).Children.OfType<TextBlock>().Single();

    private static Border Badge(MenuItem row) =>
        ((Grid)((Grid)row.Header!).Children[0]).Children.OfType<Border>().Single();

    private static TextBlock Count(MenuItem row) => ((Grid)row.Header!).Children.OfType<TextBlock>().Single();

    [AvaloniaFact]
    public void Rows_AlbumsFirst_ThenSingles_ByTitle_WithCount_Badge_Cover_AndTheSameCommand()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var albums = Library();
        var command = new AsyncRelayCommand<Album?>(_ => System.Threading.Tasks.Task.CompletedTask);
        var item = new MenuItem { Header = "Link Tracks From" };
        AlbumLinkMenu.Fill(item, albums, command);

        Assert.True(item.IsVisible);
        var rows = item.Items.OfType<MenuItem>().ToList();
        Assert.Equal(albums.Count, rows.Count);
        Assert.All(rows, r => Assert.Same(command, r.Command));
        var order = rows.Select(r => (Album)r.CommandParameter!).ToList();
        var fulls = order.TakeWhile(a => !AlbumLinkMenu.IsSingle(a)).ToList();
        var singles = order.Skip(fulls.Count).ToList();
        Assert.All(singles, s => Assert.True(AlbumLinkMenu.IsSingle(s), s.Name));
        Assert.Equal(new[]
        {
            "DeBÍ TiRAR MáS FOToS", "EL ÚLTIMO TOUR DEL MUNDO", "LAS QUE NO IBAN A SALIR", "nadie sabe lo que va a pasar mañana",
            "Un Verano Sin Ti", "Up Next (Live From Apple Piazza Liberty) - EP", "X 100PRE (Edición Especial Extendida Para Coleccionistas)", "YHLQMDLG",
        }, fulls.Select(a => a.Name));
        Assert.Equal(singles.Select(AlbumLinkMenu.DisplayName).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), singles.Select(AlbumLinkMenu.DisplayName));

        // One separator, between the groups.
        var sep = Assert.Single(item.Items.OfType<Separator>());
        Assert.Equal(fulls.Count, item.Items.IndexOf(sep));

        foreach (var row in rows)
        {
            var album = (Album)row.CommandParameter!;
            Assert.Equal(AlbumLinkMenu.DisplayName(album), Title(row).Text);
            Assert.Equal(TextTrimming.CharacterEllipsis, Title(row).TextTrimming);
            Assert.Equal(album.IsExplicit, Badge(row).IsVisible);
            Assert.Contains("explicit-badge", Badge(row).Classes);
            Assert.Equal(Loc.Plural("Count.Songs", album.Tracks.Count), Count(row).Text);
            Assert.DoesNotContain("·", Title(row).Text);
            var thumb = Assert.IsType<Border>(row.Icon);
            Assert.Contains("mv2-cover", thumb.Classes);
            Assert.Equal(album.Name, ToolTip.GetTip(row));
        }
        Assert.Equal("1 song", Count(rows.Last()).Text);
        Assert.Equal("17 songs", Count(rows.First()).Text);
        // A single reads without its " - Single" (its group says it; the tooltip keeps it).
        Assert.Equal("6 Rings", Title(rows.First(r => ((Album)r.CommandParameter!).Name == "6 Rings - Single")).Text);

        // Refilling replaces the rows; an empty list hides the submenu.
        AlbumLinkMenu.Fill(item, albums.Take(2).ToList(), command);
        Assert.Equal(3, item.Items.Count); // an album, the separator, a single
        AlbumLinkMenu.Fill(item, Array.Empty<Album>(), command);
        Assert.Empty(item.Items);
        Assert.False(item.IsVisible);
    }

    /// <param name="overlay">Host the menu in the window's overlay layer, as the main window does
    /// (".glass-overlay"), so the window's mouse and keyboard input reach the submenu.</param>
    private static (MenuFlyout flyout, MenuItem sub, Window win) OpenFlyout(IReadOnlyList<Album> albums, ThemeVariant variant, bool overlay = false)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var sub = new MenuItem { Header = Loc.T("AlbumDetail.LinkTracksFrom"), Tag = "LinkTracksFrom" };
        AlbumLinkMenu.Fill(sub, albums, new AsyncRelayCommand<Album?>(_ => System.Threading.Tasks.Task.CompletedTask));
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "Play" }, new MenuItem { Header = "Show Folder" }, sub, new Separator(), new MenuItem { Header = "Remove from Library" } } };
        var anchor = new Button { Content = "…", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(0, 40, 60, 0) };
        var win = new Window { Width = 1200, Height = 900, Content = anchor, RequestedThemeVariant = variant };
        if (overlay) win.Classes.Add("glass-overlay");
        win.Show();
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        sub.IsSubMenuOpen = true;
        Pump(400);
        return (flyout, sub, win);
    }

    private static Border Card(MenuItem sub) =>
        (Border)sub.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup").Child!;

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Rect BoundsIn(Visual v, Visual root) => new(v.TranslatePoint(new Point(0, 0), root)!.Value, v.Bounds.Size);

    [AvaloniaFact]
    public void TheCard_IsFixedWidth_ShowsAboutNineRows_AndScrollsTheRest_BadgeRightAfterTheTitle()
    {
        var (flyout, sub, win) = OpenFlyout(Library(), ThemeVariant.Dark);
        try
        {
            var card = Card(sub);
            Assert.Equal(AlbumLinkMenu.CardWidth, card.Width);
            Assert.Equal(AlbumLinkMenu.CardWidth, card.MaxWidth);
            Assert.Equal(AlbumLinkMenu.CardMaxHeight, card.MaxHeight);
            Assert.InRange(AlbumLinkMenu.CardMaxHeight, 9 * 32, 10 * 32 + 14);
            Assert.True(card.Bounds.Height <= AlbumLinkMenu.CardMaxHeight + 0.5, $"card {card.Bounds.Height} tall");
            Assert.Equal(AlbumLinkMenu.CardWidth, card.Bounds.Width, 1);
            var scroller = card.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height + 32 * 10, $"extent {scroller.Extent.Height} viewport {scroller.Viewport.Height}");

            var rows = sub.Items.OfType<MenuItem>().ToList();
            Assert.All(rows, r => Assert.Equal(32, r.Bounds.Height, 1));
            foreach (var row in rows.Take(9))
            {
                var title = BoundsIn(Title(row), row);
                var count = BoundsIn(Count(row), row);
                Assert.True(title.Right <= count.Left, $"{Title(row).Text}: title {title} runs under the count {count}");
                if (!Badge(row).IsVisible) continue;
                var badge = BoundsIn(Badge(row), row);
                Assert.Equal(6, badge.Left - title.Right, 1); // right after the title, trimmed or not
                Assert.True(badge.Right <= count.Left, $"{Title(row).Text}: badge {badge} under the count {count}");
            }
            // Counts line up on the right edge.
            var rights = rows.Take(9).Select(r => BoundsIn(Count(r), r).Right).ToList();
            Assert.All(rights, r => Assert.Equal(rights[0], r, 1));
        }
        finally
        {
            flyout.Hide();
            win.Close();
        }
    }

    private static ScrollViewer Scroller(MenuItem sub) => Card(sub).GetVisualDescendants().OfType<ScrollViewer>().First();

    private static Rect InWindow(Visual v, Window win) => new(v.TranslatePoint(new Point(0, 0), win)!.Value, v.Bounds.Size);

    // Owner 11-14: "remove the arrows on top and bottom, theres also a bug that it autoscrolls
    // sometimes. when i use the scrollwheel to scroll, it should have the same animation like
    // inside of the albums section."

    [AvaloniaFact]
    public void TheScroller_HasNoArrowStrips_ButTheAppsPillScrollbar()
    {
        var (flyout, sub, win) = OpenFlyout(Library(), ThemeVariant.Dark, overlay: true);
        try
        {
            var sv = Scroller(sub);
            Assert.True(SubmenuScroller.GetIsEnabled(sv));
            // The Fluent menu scroller's arrow strips are RepeatButtons outside any ScrollBar.
            Assert.DoesNotContain(sv.GetVisualDescendants().OfType<RepeatButton>(), b => b.FindAncestorOfType<ScrollBar>() == null);
            var bar = sv.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(bar.IsVisible && bar.Bounds.Height > 100, $"bar {bar.Bounds} visible={bar.IsVisible}");
            // The viewport is the whole card inside its padding and border: no strip takes a row's room.
            Assert.Equal(AlbumLinkMenu.CardMaxHeight - 14, sv.Viewport.Height, 1);
        }
        finally
        {
            flyout.Hide();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void ThePointer_RestingOrMoving_OverTheRowsAndEdges_NeverScrollsTheList()
    {
        var (flyout, sub, win) = OpenFlyout(Library(), ThemeVariant.Dark, overlay: true);
        try
        {
            var sv = Scroller(sub);
            var box = InWindow(sv, win);
            Assert.Equal(0, sv.Offset.Y);
            // Down through every row to the half row at the bottom edge (this used to scroll by
            // 14 px: the hovered row is focused and was brought into view), then rest there.
            for (var y = box.Top + 2; y < box.Bottom - 0.5; y += 6)
            {
                win.MouseMove(new Point(box.Left + 120, y), RawInputModifiers.None);
                Pump(20);
                Assert.Equal(0, sv.Offset.Y);
            }
            win.MouseMove(new Point(box.Left + 120, box.Bottom - 2), RawInputModifiers.None);
            Pump(1200);
            Assert.Equal(0, sv.Offset.Y);

            // Scrolled to the end, resting at the top edge does not scroll it back either.
            var max = sv.Extent.Height - sv.Viewport.Height;
            sv.Offset = new Vector(0, max);
            Pump(50);
            for (var y = box.Top + 30; y > box.Top + 0.5; y -= 4)
            {
                win.MouseMove(new Point(box.Left + 120, y), RawInputModifiers.None);
                Pump(20);
            }
            Pump(1200);
            Assert.Equal(max, sv.Offset.Y, 1);
        }
        finally
        {
            flyout.Hide();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void AWheelNotch_GlidesLikeTheAlbumsPage_ThroughInBetweenOffsets()
    {
        var (flyout, sub, win) = OpenFlyout(Library(), ThemeVariant.Dark, overlay: true);
        try
        {
            var sv = Scroller(sub);
            Assert.True(SmoothScrollBehavior.GetIsEnabled(sv));
            Assert.Equal(220, SmoothScrollBehavior.GetStep(sv));
            Assert.Equal(380, SmoothScrollBehavior.GetSettleMs(sv));
            var box = InWindow(sv, win);
            var at = new Point(box.Left + 120, box.Top + 60);
            win.MouseMove(at, RawInputModifiers.None);
            Pump(30);
            win.MouseWheel(at, new Vector(0, -1));
            var seen = new List<double>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 700) { Pump(10); seen.Add(sv.Offset.Y); }
            _o.WriteLine(string.Join(", ", seen.Select(v => v.ToString("0"))));
            Assert.Equal(220, seen[^1], 1);
            // Eased over several frames, rather than jumping a line (50 px) or the whole step.
            var between = seen.Where(v => v > 1 && v < 219).Distinct().Count();
            Assert.True(between >= 5, $"only {between} in-between offsets: {string.Join(", ", seen)}");
            Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second >= p.First - 0.01), "the glide went backwards");
        }
        finally
        {
            flyout.Hide();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void KeyboardDown_PastTheLastVisibleRow_BringsItIntoView()
    {
        var (flyout, sub, win) = OpenFlyout(Library(), ThemeVariant.Dark, overlay: true);
        try
        {
            var sv = Scroller(sub);
            var rows = sub.Items.OfType<MenuItem>().ToList();
            Assert.True(rows[0].Focus(NavigationMethod.Directional));
            Pump(30);
            for (var i = 0; i < 12; i++)
            {
                win.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Pump(20);
            }
            var focused = Assert.IsType<MenuItem>(TopLevel.GetTopLevel(sv)!.FocusManager!.GetFocusedElement());
            Assert.True(rows.IndexOf(focused) >= 10, $"focus on row {rows.IndexOf(focused)}");
            Assert.True(sv.Offset.Y > 0, "the list did not follow the keyboard");
            var row = InWindow(focused, win);
            var view = InWindow(sv, win);
            Assert.True(row.Top >= view.Top - 0.5 && row.Bottom <= view.Bottom + 0.5, $"row {row} outside {view}");
        }
        finally
        {
            flyout.Hide();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void Probe_RenderTheSubmenuCard()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering
        var env = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        var outDir = string.IsNullOrWhiteSpace(env) ? @"D:\NoctisWT\art-1114\linkmenu2" : env;
        Directory.CreateDirectory(outDir);
        var albums = Library(withArt: true);
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        foreach (var after in new[] { false, true })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var (flyout, sub, win) = OpenFlyout(albums, variant);
            try
            {
                var tag = after ? "after" : "before";
                var scroller = Scroller(sub);
                // "before": the Fluent menu scroller (arrow strips) the template sets without SubmenuScroller.
                if (!after) SubmenuScroller.SetIsEnabled(scroller, false);
                Pump(600);
                var card = Card(sub);
                var root = TopLevel.GetTopLevel(card)!;
                root.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{tag}-{variant}-top.png"));
                scroller.Offset = new Vector(0, 160);
                Pump(300);
                root.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{tag}-{variant}-middle.png"));
                scroller.Offset = new Vector(0, scroller.Extent.Height);
                Pump(300);
                root.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{tag}-{variant}-bottom.png"));
                _o.WriteLine($"{variant} {tag}: card {card.Bounds} viewport {scroller.Viewport}");
            }
            finally
            {
                flyout.Hide();
                win.Close();
            }
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
