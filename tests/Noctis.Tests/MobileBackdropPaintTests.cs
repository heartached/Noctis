using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Mobile.Services;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings › Background's page brush drawn straight onto the page (BackdropPaint): which
/// brushes take that path, the Stretch Fill mapping, the repaint on every nudge, and (with
/// NOCTIS_TEST_SKIA=1) that it draws what the brush did. Beta 6 "the lag" (11-27): as a brush the
/// mesh was rasterized into a page-sized picture on every paint, three times a frame.</summary>
public class MobileBackdropPaintTests
{
    [AvaloniaFact]
    public void TheMeshAndTheWash_AreDrawnDirectly_OtherBrushesPaintAsBefore()
    {
        var mesh = new MeshBrush();
        mesh.Set(BackgroundCatalog.For(BackgroundCatalog.Find("ember")!, false), 0.6);
        Assert.True(BackdropPaint.DrawsDirectly(mesh.Brush));
        Assert.True(BackdropPaint.DrawsDirectly(new WashBrush(light: false).Brush));

        Assert.False(BackdropPaint.DrawsDirectly(null));
        Assert.False(BackdropPaint.DrawsDirectly(new SolidColorBrush(Colors.Black)));
        Assert.False(BackdropPaint.DrawsDirectly(new LinearGradientBrush()));
        Assert.False(BackdropPaint.DrawsDirectly(new DrawingBrush()));                       // nothing to draw
        var drawing = new GeometryDrawing { Brush = Brushes.Red, Geometry = new RectangleGeometry(new Rect(0, 0, 10, 10)) };
        Assert.False(BackdropPaint.DrawsDirectly(new DrawingBrush(drawing) { Stretch = Stretch.Uniform }));
        Assert.False(BackdropPaint.DrawsDirectly(new DrawingBrush(drawing) { Stretch = Stretch.Fill, TileMode = TileMode.Tile }));
        Assert.False(BackdropPaint.DrawsDirectly(new DrawingBrush(drawing) { Stretch = Stretch.Fill, Transform = new RotateTransform(5) }));
        Assert.False(BackdropPaint.DrawsDirectly(new DrawingBrush(drawing)
        {
            Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, 0.5, 0.5, RelativeUnit.Relative),
        }));
        Assert.True(BackdropPaint.DrawsDirectly(new DrawingBrush(drawing) { Stretch = Stretch.Fill }));
    }

    [Fact]
    public void FillTransform_StretchesTheDrawingsBoundsOverTheTarget()
    {
        var m = BackdropPaint.FillTransform(new Rect(0, 0, 100, 100), new Rect(0, 4, 412, 880));
        Near(new Point(0, 4), new Point(0, 0).Transform(m));
        Near(new Point(412, 884), new Point(100, 100).Transform(m));
        Near(new Point(206, 444), new Point(50, 50).Transform(m));

        m = BackdropPaint.FillTransform(new Rect(10, 20, 30, 40), new Rect(0, 0, 60, 80));
        Near(new Point(0, 0), new Point(10, 20).Transform(m));
        Near(new Point(60, 80), new Point(40, 60).Transform(m));

        static void Near(Point expected, Point actual)
        {
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);
        }
    }

    [AvaloniaFact]
    public void EveryNudge_Repaints_OnlyWhileInTheTree()
    {
        var mesh = new MeshBrush();
        var palette = BackgroundCatalog.For(BackgroundCatalog.Find("ember")!, false);
        var repaints = 0;
        var watch = new BackdropPaint.Watch(() => repaints++);
        watch.Brush(mesh.Brush);
        mesh.Set(palette, 0.6);
        Assert.Equal(0, repaints);                // not in the tree yet: not hooked

        watch.Attached(true);
        mesh.Set(palette, 0.8);
        mesh.Drift(12);
        mesh.Rest();
        Assert.Equal(3, repaints);

        var wash = new WashBrush(light: false);
        watch.Brush(wash.Brush);                  // another brush: the old one is let go
        mesh.Drift(13);
        Assert.Equal(3, repaints);
        wash.Step(1);
        Assert.Equal(4, repaints);

        watch.Brush(new SolidColorBrush(Colors.Black));
        wash.Step(1);
        Assert.Equal(4, repaints);

        watch.Brush(mesh.Brush);
        watch.Attached(false);                    // out of the tree: the shared brush holds no page
        mesh.Drift(14);
        Assert.Equal(4, repaints);
    }

    /// <summary>With real rendering: a new cover swapped into the wash shows on a page already
    /// drawn (the swap is inside the drawing, which the page recorded once; the nudge redraws it).</summary>
    [AvaloniaFact]
    public void APageBackdrop_ShowsTheNewCover_WhenTheWashSwapsIt()
    {
        if (!HeadlessTestApp.RealRendering) return;   // the stubs draw nothing
        var wash = new WashBrush(light: false);
        wash.Set(Cover(Colors.Red), Colors.Transparent, fade: false);
        var window = new Window { Width = 100, Height = 200, Content = new PageBackdrop { Brush = wash.Brush } };
        window.Show();
        var red = Centre(AccentWindowTintTests.Shot(window, null));
        wash.Set(Cover(Colors.Blue), Colors.Transparent, fade: false);
        var blue = Centre(AccentWindowTintTests.Shot(window, null));
        window.Close();
        Assert.True(red.R > 200 && red.B < 60, $"first cover: {red}");
        Assert.True(blue.B > 200 && blue.R < 60, $"after the swap: {blue}");

        static Color Centre(WriteableBitmap shot)
        {
            using var fb = shot.Lock();
            var px = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + fb.Size.Height / 2 * fb.RowBytes + fb.Size.Width / 2 * 4, px, 0, 4);
            return Color.FromArgb(px[3], px[2], px[1], px[0]);
        }
    }

    /// <summary>With real rendering: each Background brush drawn directly matches the brush as
    /// Avalonia paints it (both ways on one window, side by side), the sheet's rounded top too.</summary>
    [AvaloniaFact]
    public void DrawnDirectly_LooksLikeTheBrush()
    {
        if (!HeadlessTestApp.RealRendering) return;   // the stubs draw nothing
        var mesh = new MeshBrush();
        mesh.Set(BackgroundCatalog.For(BackgroundCatalog.Find("ember")!, false), 0.8);
        mesh.Drift(7);
        var wash = new WashBrush(light: false);
        wash.Set(Cover(Colors.SteelBlue, Colors.OrangeRed), Color.FromArgb(0x60, 0, 0, 0), fade: false);

        foreach (var (name, brush) in new (string, IBrush)[] { ("mesh", mesh.Brush), ("wash", wash.Brush) })
        {
            var corners = new CornerRadius(30, 30, 0, 0);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            grid.Children.Add(new Border { Background = brush, CornerRadius = corners, ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0) });
            var direct = new PageBackdrop { Brush = brush, CornerRadius = corners, Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetColumn(direct, 1);
            grid.Children.Add(direct);
            var window = new Window { Width = 400, Height = 600, Content = grid, Background = Brushes.White };
            window.Show();
            var shot = AccentWindowTintTests.Shot(window, null);
            using var fb = shot.Lock();
            var rows = new byte[fb.RowBytes * fb.Size.Height];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, rows, 0, rows.Length);
            long diff = 0, off = 0, n = 0;
            var w = fb.Size.Width / 2;
            for (var y = 0; y < fb.Size.Height; y += 3)
            for (var x = 0; x < w; x += 3)
            {
                var a = y * fb.RowBytes + x * 4;
                var b = a + w * 4;
                for (var c = 0; c < 3; c++)
                {
                    var d = Math.Abs(rows[a + c] - rows[b + c]);
                    diff += d;
                    if (d > 12) off++;
                    n++;
                }
            }
            var dir = Environment.GetEnvironmentVariable("NOCTIS_BACKDROP_SHOTS");
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); shot.Save(Path.Combine(dir, name + ".png")); }
            window.Close();
            Assert.True((double)diff / n < 1.5, $"{name}: mean difference {(double)diff / n:0.00}");
            // Only the rounded corners' antialiased rims may differ (a few pixels).
            Assert.True(off < n / 500, $"{name}: {off} of {n} samples differ by more than 12");
        }
    }

    private static WriteableBitmap Cover(Color top, Color? bottom = null, int size = 32)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[size * 4];
        for (var y = 0; y < size; y++)
        {
            var c = bottom is { } b && y >= size / 2 ? b : top;
            for (var x = 0; x < size; x++)
            {
                row[x * 4] = c.B; row[x * 4 + 1] = c.G; row[x * 4 + 2] = c.R; row[x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bitmap;
    }
}
