using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noctis.Mobile.ViewModels;
using SkiaSharp;

namespace Noctis.Mobile.Views;

/// <summary>
/// Paints DiscArtwork's two bitmaps, once per cover (the disc) and once per style and size (its
/// light), at the device pixel size they are drawn at. Coordinates are pixels; R is the radius.
/// <para>
/// Vinyl: a black record, the cover as its centre label (a third of the record, as a 12-inch's),
/// fine grooves in a few bands with the gaps between tracks, a smooth run-out and rim; its light is
/// the bow-tie sheen a lamp leaves on grooves, a catch-light on the rim and the silver spindle.
/// CD: the cover printed over the disc from the mirror band to the rim, the clear hub and the
/// hole (see-through); its light, a rainbow strongest on the bare silver, faint on the print.
/// </para>
/// </summary>
internal static class DiscPainter
{
    // Vinyl proportions (of R).
    internal const double LabelRadius = 0.345, GroovesFrom = 0.38, GroovesTo = 0.965, SpindleRadius = 0.017;
    // The gaps between tracks (of R).
    private static readonly double[] TrackGaps = { 0.47, 0.585, 0.70, 0.80, 0.885 };
    // CD proportions (of R): the hole, the clear hub, the mirror band, the print, the rim.
    internal const double CdHole = 0.125, CdClear = 0.19, CdMirror = 0.30, CdPrint = 0.985;

    public static SKImage PaintDisc(NowPlayingArtworkStyle kind, int pixels, IImage? cover) =>
        Paint(pixels, (ctx, r) =>
        {
            if (kind == NowPlayingArtworkStyle.CD) Cd(ctx, r, cover);
            else Vinyl(ctx, r, cover);
        });

    public static SKImage PaintLight(NowPlayingArtworkStyle kind, int pixels) =>
        Paint(pixels, (ctx, r) =>
        {
            if (kind == NowPlayingArtworkStyle.CD) CdLight(ctx, r);
            else VinylLight(ctx, r);
        });

    private static SKImage Paint(int pixels, Action<DrawingContext, double> draw)
    {
        var size = new PixelSize(pixels, pixels);
        using var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
            draw(ctx, pixels / 2.0);
        // Copied into an immutable Skia image: the render thread draws it every frame, straight
        // on the canvas (DiscSpinHandler).
        var stride = pixels * 4;
        var buffer = Marshal.AllocHGlobal(stride * pixels);
        try
        {
            target.CopyPixels(new PixelRect(size), buffer, stride * pixels, stride);
            var colour = target.Format == PixelFormat.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Bgra8888;
            return SKImage.FromPixelCopy(new SKImageInfo(pixels, pixels, colour, SKAlphaType.Premul), buffer, stride)
                ?? throw new InvalidOperationException("SKImage.FromPixelCopy returned null");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- Vinyl ----------------------------------------------------------------------------

    private static void Vinyl(DrawingContext ctx, double r, IImage? cover)
    {
        var c = new Point(r, r);
        // The body: near-black, a breath lighter toward the middle.
        ctx.DrawEllipse(Radial(Color.FromRgb(0x1B, 0x1B, 0x1E), Color.FromRgb(0x10, 0x10, 0x12), Color.FromRgb(0x0A, 0x0A, 0x0B)), null, c, r, r);

        // The grooves: a hairline every ~1.5 px of the screen, each a slightly different grey
        // (fixed seed: the same record every time), none inside the gaps between tracks.
        var step = Math.Max(1.25, r / 320);
        var rng = new Random(33);
        for (var radius = GroovesFrom * r; radius < GroovesTo * r; radius += step)
        {
            if (InGap(radius / r, step / r)) continue;
            var alpha = (byte)(7 + rng.Next(10));
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)), step * 0.45), c, radius, radius);
        }
        // The gaps: smooth, glossier bands.
        foreach (var gap in TrackGaps)
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)), r * 0.006), c, gap * r, gap * r);
        // The run-out groove, and the rim's turned edge.
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), Math.Max(1, r / 400)), c, 0.367 * r, 0.367 * r);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), Math.Max(1, r / 300)), c, r - r / 160, r - r / 160);

        // The label: the cover, round, with a pressed edge.
        var label = LabelRadius * r;
        DrawCover(ctx, cover, c, label, Color.FromRgb(0x2A, 0x2A, 0x2E));
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x73, 0x00, 0x00, 0x00)), Math.Max(1, r / 260)), c, label, label);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Math.Max(1, r / 480)), c, label * 0.93, label * 0.93);
        // The spindle's hole (the spindle itself is in the light: it does not turn).
        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x09)), null, c, SpindleRadius * r * 1.35, SpindleRadius * r * 1.35);
    }

    private static bool InGap(double radius, double halfWidth)
    {
        foreach (var gap in TrackGaps)
            if (Math.Abs(radius - gap) < 0.004 + halfWidth) return true;
        return false;
    }

    private static void VinylLight(DrawingContext ctx, double r)
    {
        var c = new Point(r, r);
        // The bow tie: two soft wedges of light across the grooves, opposite each other.
        using (ctx.PushGeometryClip(Ring(c, GroovesFrom * r - 1, r)))
        {
            ctx.DrawEllipse(Sheen(peak: 0x34, second: 0x26, width: 0.07, rainbow: 0), null, c, r, r);
            ctx.DrawEllipse(Sheen(peak: 0x10, second: 0x0C, width: 0.16, rainbow: 0), null, c, r, r);
        }
        // A soft overhead light over the whole record, the label included.
        ctx.DrawEllipse(new RadialGradientBrush
        {
            Center = new RelativePoint(0.3, 0.22, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.3, 0.22, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1) },
        }, null, c, r, r);
        RimLight(ctx, c, r, 0x50);
        // The spindle: brushed steel, lit from the top left.
        var s = SpindleRadius * r;
        ctx.DrawEllipse(new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(0xF2, 0xF3, 0xF5), 0), new GradientStop(Color.FromRgb(0x9C, 0xA1, 0xA8), 0.65), new GradientStop(Color.FromRgb(0x5E, 0x62, 0x68), 1) },
        }, new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)), Math.Max(1, r / 500)), c, s, s);
    }

    // ---- CD -------------------------------------------------------------------------------

    private static void Cd(DrawingContext ctx, double r, IImage? cover)
    {
        var c = new Point(r, r);
        // The clear hub: a tint of the plastic, the hole left see-through.
        using (ctx.PushGeometryClip(Ring(c, CdHole * r, CdClear * r)))
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, 0xE4, 0xE8, 0xEE)), null, c, r, r);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF)), Math.Max(1, r / 360)), c, CdHole * r, CdHole * r);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)), Math.Max(1.2, r / 220)), c, 0.163 * r, 0.163 * r);   // the stacking ring

        // The mirror band and the rim: bare silver.
        var silver = Radial(Color.FromRgb(0xE4, 0xE7, 0xEB), Color.FromRgb(0xB4, 0xBA, 0xC2), Color.FromRgb(0xD3, 0xD7, 0xDC));
        using (ctx.PushGeometryClip(Ring(c, CdClear * r, CdMirror * r + 1)))
            ctx.DrawEllipse(silver, null, c, r, r);
        using (ctx.PushGeometryClip(Ring(c, CdPrint * r - 1, r)))
            ctx.DrawEllipse(silver, null, c, r, r);
        // Faint pressing rings in the mirror band.
        var rng = new Random(12);
        for (var radius = CdClear * r + 2; radius < CdMirror * r; radius += Math.Max(2, r / 90))
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(10 + rng.Next(14)), 0x60, 0x66, 0x70)), 1), c, radius, radius);

        // The print: the cover across the disc, the hub cut out of it.
        using (ctx.PushGeometryClip(Ring(c, CdMirror * r, CdPrint * r)))
        {
            if (cover != null) DrawCover(ctx, cover, c, CdPrint * r, Color.FromRgb(0xC9, 0xCD, 0xD3));
            else ctx.DrawEllipse(silver, null, c, r, r);
        }
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), Math.Max(1, r / 400)), c, CdMirror * r, CdMirror * r);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00)), Math.Max(1, r / 400)), c, CdPrint * r, CdPrint * r);
    }

    private static void CdLight(DrawingContext ctx, double r)
    {
        var c = new Point(r, r);
        // The bare silver catches the rainbow; the hole stays clear.
        using (ctx.PushGeometryClip(Ring(c, CdHole * r, CdMirror * r)))
            ctx.DrawEllipse(Sheen(peak: 0xB0, second: 0x90, width: 0.11, rainbow: 0x40), null, c, r, r);
        using (ctx.PushGeometryClip(Ring(c, CdPrint * r - 1, r)))
            ctx.DrawEllipse(Sheen(peak: 0xC0, second: 0xA0, width: 0.12, rainbow: 0x50), null, c, r, r);
        // The print's gloss: a white wedge with a faint rainbow at its edges.
        using (ctx.PushGeometryClip(Ring(c, CdMirror * r, CdPrint * r)))
        {
            ctx.DrawEllipse(Sheen(peak: 0x2E, second: 0x22, width: 0.05, rainbow: 0), null, c, r, r);
            ctx.DrawEllipse(Sheen(peak: 0x26, second: 0x1E, width: 0.13, rainbow: 0x0A), null, c, r, r);
        }
        RimLight(ctx, c, r, 0x80);
    }

    // ---- Shared ---------------------------------------------------------------------------

    /// <summary>The cover, square-cropped, filling a circle of <paramref name="radius"/> (or a
    /// plain fill without one).</summary>
    private static void DrawCover(DrawingContext ctx, IImage? cover, Point c, double radius, Color fallback)
    {
        var dest = new Rect(c.X - radius, c.Y - radius, radius * 2, radius * 2);
        using (ctx.PushGeometryClip(new EllipseGeometry(dest)))
        {
            if (cover is { Size: { Width: > 0, Height: > 0 } size })
            {
                var side = Math.Min(size.Width, size.Height);
                var src = new Rect((size.Width - side) / 2, (size.Height - side) / 2, side, side);
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(cover, src, dest);
            }
            else
            {
                ctx.DrawEllipse(Radial(Lighter(fallback, 0.12), fallback, Darker(fallback, 0.2)), null, c, radius, radius);
            }
        }
    }

    /// <summary>The rim's catch-light: bright where the lamp is (top left), gone around the back.</summary>
    private static void RimLight(DrawingContext ctx, Point c, double r, byte peak)
    {
        var brush = new ConicGradientBrush { Angle = 0 };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(peak, 0xFF, 0xFF, 0xFF), 0.85));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        var w = Math.Max(1.2, r / 220);
        ctx.DrawEllipse(null, new Pen(brush, w), c, r - w / 2, r - w / 2);
    }

    /// <summary>
    /// The bow-tie sheen: two wedges of light at the top left and bottom right, as a lamp leaves
    /// on a grooved or pitted disc (narrow at the centre, by geometry), white tipped with a
    /// rainbow of <paramref name="rainbow"/> alpha on its flanks (a CD's diffraction). Built as
    /// 72 stops of a conic gradient.
    /// </summary>
    internal static ConicGradientBrush Sheen(byte peak, byte second, double width, byte rainbow)
    {
        var brush = new ConicGradientBrush { Angle = 0 };
        const int Stops = 72;
        for (var i = 0; i <= Stops; i++)
        {
            var t = (double)i / Stops;   // 0 = top, clockwise
            // Distance to the two wedges (top left at 0.875, bottom right at 0.375), wrapped.
            var d1 = Wrap(t - 0.875);
            var d2 = Wrap(t - 0.375);
            var w1 = Bump(d1, width) * peak;
            var w2 = Bump(d2, width) * second;
            var white = Math.Max(w1, w2);
            // The rainbow rides the flanks of each wedge: hue runs across the wedge.
            var flank = Math.Max(Bump(d1, width * 2.2) - Bump(d1, width * 0.6), Bump(d2, width * 2.2) - Bump(d2, width * 0.6));
            var hueT = Math.Abs(d1) < Math.Abs(d2) ? d1 : d2;
            var hue = (hueT / (width * 2.2) + 1) * 0.5;   // 0..1 across the flank
            var tint = Hue(hue);
            var ra = Math.Max(0, flank) * rainbow;
            var a = white + ra;
            if (a <= 0.5)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), t));
                continue;
            }
            // Blend white and the hue by their shares of the alpha.
            var k = ra / a;
            byte Mix(byte channel) => (byte)Math.Round(0xFF * (1 - k) + channel * k);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Min(255, Math.Round(a)), Mix(tint.R), Mix(tint.G), Mix(tint.B)), t));
        }
        return brush;
    }

    private static double Wrap(double d) => d - Math.Round(d);

    /// <summary>A soft bump: 1 at 0, falling smoothly to 0 at ±<paramref name="width"/>.</summary>
    private static double Bump(double d, double width)
    {
        var x = Math.Abs(d) / width;
        if (x >= 1) return 0;
        var s = 1 - x * x;
        return s * s;
    }

    private static Color Hue(double h)
    {
        h = (h % 1 + 1) % 1 * 6;
        var x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        // Pastel: a diffraction rainbow on silver is never fully saturated.
        static byte P(double v) => (byte)Math.Round(255 * (0.45 + 0.55 * v));
        return Color.FromRgb(P(r), P(g), P(b));
    }

    private static Geometry Ring(Point c, double inner, double outer) => new GeometryGroup
    {
        FillRule = FillRule.EvenOdd,
        Children =
        {
            new EllipseGeometry(new Rect(c.X - outer, c.Y - outer, outer * 2, outer * 2)),
            new EllipseGeometry(new Rect(c.X - inner, c.Y - inner, inner * 2, inner * 2)),
        },
    };

    private static RadialGradientBrush Radial(Color centre, Color middle, Color edge) => new()
    {
        GradientStops = { new GradientStop(centre, 0), new GradientStop(middle, 0.7), new GradientStop(edge, 1) },
    };

    private static Color Lighter(Color c, double t) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    private static Color Darker(Color c, double t) =>
        Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
}
