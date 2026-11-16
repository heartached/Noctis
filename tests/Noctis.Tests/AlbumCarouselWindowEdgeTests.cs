using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #244: the Albums page's full carousel stopped at a hard edge on the left, where the
/// page begins beside the sidebar rail (76 px, 236 pinned, plus the page's 12 px inset), while
/// on the right it ran nearly to the window's edge. With BleedToWindowEdges the row is drawn to
/// both window edges (under the rail) while its layout stays centred on the strip; the page
/// hosts' clips are lifted only while it is shown, and a carousel without it (the artist page)
/// clips to its bounds as before.
/// </summary>
public class AlbumCarouselWindowEdgeTests
{
    private static List<Album> Many(int n) => Enumerable.Range(0, n)
        .Select(i => new Album { Id = Guid.NewGuid(), Name = $"Album {i:D4}", Artist = "A", Tracks = new List<Track>() })
        .ToList();

    private sealed record Page(Window Window, DockPanel Root, ContentControl Host, AlbumCarousel Carousel);

    /// <summary>MainWindow's shape: RootPanel (rail margin) › page host (a clipping
    /// ContentControl) › the Albums page's 12,8,2,8 dock › the carousel (0,4,10,110).</summary>
    private static Page Mount(bool bleed, double railMargin = 76)
    {
        var carousel = new AlbumCarousel
        {
            Albums = Many(400),
            BleedToWindowEdges = bleed,
            Margin = new Thickness(0, 4, 10, 110),
        };
        var dock = new DockPanel { Margin = new Thickness(12, 8, 2, 8) };
        dock.Children.Add(carousel);
        var host = new ContentControl { Content = dock, ClipToBounds = true };
        var root = new DockPanel { Margin = new Thickness(railMargin, 0, 0, 0) };
        root.Children.Add(host);
        var layer = new Panel();
        layer.Children.Add(root);
        var win = new Window { Width = 1366, Height = 768, Content = layer };
        win.Show();
        Pump();
        return new Page(win, root, host, carousel);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    private static Rect ClipInWindow(AlbumCarousel c, Window w)
    {
        var r = c.Clip!.Bounds;
        var tl = c.TranslatePoint(r.TopLeft, w)!.Value;
        var br = c.TranslatePoint(r.BottomRight, w)!.Value;
        return new Rect(tl, br);
    }

    [AvaloniaTheory]
    [InlineData(76.0)]   // collapsed rail
    [InlineData(236.0)]  // Keep sidebar expanded
    [InlineData(0.0)]    // hidden sidebar (Hide sidebar until hover, fullscreen)
    public void Row_IsDrawnToBothWindowEdges_LayoutStaysCentredOnTheStrip(double railMargin)
    {
        var off = Mount(bleed: false, railMargin);
        double centre;
        int builtOff;
        try
        {
            centre = off.Carousel.TranslatePoint(new Point(off.Carousel.Bounds.Width / 2, 0), off.Window)!.Value.X;
            builtOff = off.Carousel.RealizedCount;
            Assert.True(off.Carousel.ClipToBounds);
            Assert.Null(off.Carousel.Clip);
        }
        finally { off.Window.Close(); }

        var page = Mount(bleed: true, railMargin);
        try
        {
            var c = page.Carousel;
            Assert.Equal(railMargin + 12, c.ReachLeft, 1);
            Assert.Equal(12, c.ReachRight, 1);
            var clip = ClipInWindow(c, page.Window);
            Assert.Equal(0, clip.Left, 1);
            Assert.Equal(page.Window.Bounds.Width, clip.Right, 1);
            // Vertically it still clips to the strip.
            Assert.Equal(0, c.Clip!.Bounds.Top, 1);
            Assert.Equal(c.Bounds.Height, c.Clip.Bounds.Height, 1);
            // Same layout: the row is centred where it was.
            Assert.Equal(centre, c.TranslatePoint(new Point(c.Bounds.Width / 2, 0), page.Window)!.Value.X, 1);
            // The hosts between the strip and the window draw past their bounds while it shows.
            Assert.False(c.ClipToBounds);
            Assert.False(page.Host.ClipToBounds);
            // The wider reach builds the albums that now land on screen.
            Assert.True(c.RealizedCount >= builtOff, $"{c.RealizedCount} < {builtOff}");
            if (railMargin > 0) Assert.True(c.RealizedCount > builtOff, $"{c.RealizedCount} <= {builtOff}");
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void FollowsTheRail_PinnedAndUnpinned()
    {
        var page = Mount(bleed: true);
        try
        {
            page.Root.Margin = new Thickness(236, 0, 0, 0);
            Pump();
            Assert.Equal(0, ClipInWindow(page.Carousel, page.Window).Left, 1);
            page.Root.Margin = new Thickness(76, 0, 0, 0);
            Pump();
            Assert.Equal(0, ClipInWindow(page.Carousel, page.Window).Left, 1);
            Assert.Equal(page.Window.Bounds.Width, ClipInWindow(page.Carousel, page.Window).Right, 1);
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void HiddenOrLeft_RestoresThePageClips()
    {
        var page = Mount(bleed: true);
        try
        {
            var c = page.Carousel;
            // Grid / Masonry view: the carousel is hidden, the page clips as before.
            c.IsVisible = false;
            Pump();
            Assert.True(c.ClipToBounds);
            Assert.True(page.Host.ClipToBounds);
            Assert.Null(c.Clip);
            Assert.Equal(0, c.ReachLeft);

            c.IsVisible = true;
            Pump();
            Assert.False(page.Host.ClipToBounds);
            Assert.NotNull(c.Clip);

            // Leaving the page (out of the tree) gives the host its clip back.
            ((DockPanel)c.Parent!).Children.Remove(c);
            Pump();
            Assert.True(page.Host.ClipToBounds);
            Assert.True(c.ClipToBounds);
            Assert.Null(c.Clip);
        }
        finally { page.Window.Close(); }
    }

    /// <summary>Drawn, not just laid out (the #184 lesson): real Skia pixels left of the page,
    /// in the rail's margin, carry album cards only with the reach on.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cards_AreDrawnLeftOfThePage_OnlyWithTheReach(bool bleed)
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var page = Mount(bleed);
        try
        {
            page.Window.Background = Brushes.Black;
            page.Carousel.SnapTo(200); // albums on both sides
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            using var frame = page.Window.CaptureRenderedFrame()!;
            using var ms = new MemoryStream();
            frame.Save(ms);
            var dir = Environment.GetEnvironmentVariable("NOCTIS_244_SHOTS");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                frame.Save(Path.Combine(dir, $"carousel-{(bleed ? "on" : "off")}.png"));
            }
            ms.Position = 0;
            using var shot = Avalonia.Media.Imaging.WriteableBitmap.Decode(ms);
            var scale = shot.PixelSize.Width / page.Window.Bounds.Width;
            using var l = shot.Lock();
            var px = new byte[4];
            var lit = 0;
            // The rail's margin (x < 76), across the card band.
            var c = page.Carousel;
            var top = c.TranslatePoint(new Point(0, 0), page.Window)!.Value.Y;
            for (var x = 2; x < 74; x += 4)
            for (var y = top + 20; y < top + c.Bounds.Height - AlbumCarousel.CaptionHeight - 20; y += 8)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    l.Address + (int)(y * scale) * l.RowBytes + (int)(x * scale) * 4, px, 0, 4);
                if (px[0] + px[1] + px[2] > 30) lit++;
            }
            if (bleed) Assert.True(lit > 0, "no card drawn left of the page");
            else Assert.Equal(0, lit);
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void WithoutIt_TheStripClipsToItsBounds_AsOnTheArtistPage()
    {
        var page = Mount(bleed: false);
        try
        {
            Assert.True(page.Carousel.ClipToBounds);
            Assert.True(page.Host.ClipToBounds);
            Assert.Null(page.Carousel.Clip);
            Assert.Equal(0, page.Carousel.ReachLeft);
        }
        finally { page.Window.Close(); }
    }
}
