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
/// D:\NoctisWT\art-1114\linkmenu).
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

    private static (MenuFlyout flyout, MenuItem sub, Window win) OpenFlyout(IReadOnlyList<Album> albums, ThemeVariant variant)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var sub = new MenuItem { Header = Loc.T("AlbumDetail.LinkTracksFrom"), Tag = "LinkTracksFrom" };
        AlbumLinkMenu.Fill(sub, albums, new AsyncRelayCommand<Album?>(_ => System.Threading.Tasks.Task.CompletedTask));
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "Play" }, new MenuItem { Header = "Show Folder" }, sub, new Separator(), new MenuItem { Header = "Remove from Library" } } };
        var anchor = new Button { Content = "…", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(0, 40, 60, 0) };
        var win = new Window { Width = 1200, Height = 900, Content = anchor, RequestedThemeVariant = variant };
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

    [AvaloniaFact]
    public void Probe_RenderTheSubmenuCard()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering
        var env = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        var outDir = string.IsNullOrWhiteSpace(env) ? @"D:\NoctisWT\art-1114\linkmenu" : env;
        Directory.CreateDirectory(outDir);
        var albums = Library(withArt: true);
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var (flyout, sub, win) = OpenFlyout(albums, variant);
            try
            {
                Pump(600);
                var card = Card(sub);
                var root = TopLevel.GetTopLevel(card)!;
                root.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"linkmenu-{variant}-top.png"));
                var scroller = card.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroller.Offset = new Vector(0, scroller.Extent.Height);
                Pump(300);
                root.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"linkmenu-{variant}-bottom.png"));
                var flyoutRoot = TopLevel.GetTopLevel((Control)flyout.Popup.Child!)!;
                flyoutRoot.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"linkmenu-{variant}-parent.png"));
                _o.WriteLine($"{variant}: card {card.Bounds}");
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
