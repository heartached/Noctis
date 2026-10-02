using System;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Noctis.Controls;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's Liquid Glass lens (iOS 26): the rim of a glass panel refracts what lies beneath
/// — near the edge the backdrop is pulled inward and magnified, folding at the very edge — and
/// a rim light runs round it. The shaders run on a raster surface here and must sample exactly
/// where their C# twins say; every lens setting defaults off so the desktop's panels draw as
/// before.
/// </summary>
public class GlassLensTests
{
    private const int W = 240, H = 160;

    /// <summary>A capsule 160×80 px: its ends are half circles, so the rim curves everywhere.</summary>
    private static readonly SKRect Capsule = new(40, 40, 200, 120);

    private static GlassLensFrame Frame(float band = 24, float bend = 24, float dispersion = 0, float zoom = 1, float saturation = 1,
        float toneMin = 0, float toneMax = 1) =>
        new(Capsule, new GlassCornerRadii(40, 40, 40, 40), band, bend, dispersion, zoom, saturation, toneMin, toneMax);

    /// <summary>Each pixel stores its own position: red = x, green = y (both under 256).</summary>
    private static SKSurface Coordinates()
    {
        var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var bitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)x, (byte)y, 128));
        surface.Canvas.DrawBitmap(bitmap, 0, 0);
        return surface;
    }

    private static SKPath CapsulePath()
    {
        var path = new SKPath();
        path.AddRoundRect(new SKRoundRect(Capsule, 40));
        return path;
    }

    /// <summary>Runs the lens over <paramref name="surface"/> with no frost (sigma at the floor).</summary>
    private static void Lens(SKSurface surface, GlassLensFrame frame)
    {
        using var path = CapsulePath();
        Assert.True(GlassBlur.Draw(surface.Canvas, surface, Capsule, path, sigma: 0, alpha: 1, lens: frame));
    }

    private static SKColor Pixel(SKSurface surface, int x, int y)
    {
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y);
    }

    /// <summary>Asserts the lens output at pixel (x, y) is the backdrop at the twin's sample point
    /// (pixel centres sit at +0.5; linear sampling, so a pixel's value is its position).</summary>
    private static void AssertSamples(SKSurface surface, GlassLensFrame frame, int x, int y)
    {
        var at = GlassLens.SamplePoint(new SKPoint(x + 0.5f, y + 0.5f), frame);
        var c = Pixel(surface, x, y);
        Assert.True(Math.Abs(c.Red - (at.X - 0.5f)) <= 1.5f, $"({x},{y}) red {c.Red}, expected x {at.X - 0.5f:F1}");
        Assert.True(Math.Abs(c.Green - (at.Y - 0.5f)) <= 1.5f, $"({x},{y}) green {c.Green}, expected y {at.Y - 0.5f:F1}");
    }

    [Fact]
    public void Shaders_CompileOnTheShippedSkia()
    {
        Assert.NotNull(GlassLens.Lens(out var lensError));
        Assert.Null(lensError);
        Assert.NotNull(GlassLens.Rim(out var rimError));
        Assert.Null(rimError);
    }

    [Fact]
    public void SdRoundRect_IsNegativeInside_ZeroOnTheOutline_PositiveOutside()
    {
        // Capsule half size 80×40, radius 40, measured from its centre.
        Assert.Equal(-40, GlassLens.SdRoundRect(0, 0, 80, 40, 40), 3);
        Assert.Equal(0, GlassLens.SdRoundRect(0, -40, 80, 40, 40), 3);       // top edge
        Assert.Equal(0, GlassLens.SdRoundRect(80, 0, 80, 40, 40), 3);        // tip of the right end
        Assert.Equal(-10, GlassLens.SdRoundRect(0, 30, 80, 40, 40), 3);
        Assert.Equal(5, GlassLens.SdRoundRect(85, 0, 80, 40, 40), 3);
        // On the end's arc: distance from the arc centre (40, 0) minus the radius.
        Assert.Equal(MathF.Sqrt(30 * 30 + 30 * 30) - 40, GlassLens.SdRoundRect(70, 30, 80, 40, 40), 3);
    }

    [Fact]
    public void Normal_PointsOutward_UpOnTheTopAndRadiallyOnTheEnds()
    {
        Assert.Equal(new SKPoint(0, -1), GlassLens.Normal(0, -35, 80, 40, 40));
        Assert.Equal(new SKPoint(0, 1), GlassLens.Normal(-20, 35, 80, 40, 40));
        var n = GlassLens.Normal(70, 30, 80, 40, 40);                         // arc centre (40, 0)
        Assert.Equal(0.7071f, n.X, 3);
        Assert.Equal(0.7071f, n.Y, 3);
        // The very centre never yields NaN.
        var c = GlassLens.Normal(0, 0, 80, 40, 40);
        Assert.False(float.IsNaN(c.X) || float.IsNaN(c.Y));
    }

    [Fact]
    public void RimBend_RunsFromNothingAtTheBandsEnd_ToTheFullBendAtTheEdge_SteepeningOutward()
    {
        Assert.Equal(0, GlassLens.RimBend(24, band: 24, bend: 20));
        Assert.Equal(0, GlassLens.RimBend(30, band: 24, bend: 20));
        Assert.Equal(20, GlassLens.RimBend(0, band: 24, bend: 20), 3);
        Assert.Equal(0, GlassLens.RimBend(0, band: 0, bend: 20));
        float last = 0, lastStep = 0;
        for (var depth = 23f; depth >= 0; depth -= 1)
        {
            var d = GlassLens.RimBend(depth, 24, 20);
            Assert.True(d >= last, $"bend fell at depth {depth}");
            Assert.True(d - last >= lastStep - 1e-4f, $"the profile flattened at depth {depth}");
            lastStep = d - last;
            last = d;
        }
    }

    [Fact]
    public void Lens_LeavesTheCentreClear_AndPullsTheRimInward_WhereTheMathSays()
    {
        using var surface = Coordinates();
        var frame = Frame();
        Lens(surface, frame);

        // Centre: deeper than the band, sampled in place.
        var centre = Pixel(surface, 120, 80);
        Assert.Equal(120, centre.Red);
        Assert.Equal(80, centre.Green);
        // Two pixels in from the top edge: looks well below itself (inward), same column.
        var top = Pixel(surface, 120, 42);
        Assert.Equal(120, top.Red, 1.5);
        Assert.True(top.Green > 42 + 8, $"the top rim samples y {top.Green}, not inward");
        foreach (var (x, y) in new[] { (120, 42), (120, 50), (120, 117), (44, 80), (60, 50), (185, 100), (150, 63) })
            AssertSamples(surface, frame, x, y);
        // Outside the outline nothing changes.
        Assert.Equal(new SKColor(20, 20, 128), Pixel(surface, 20, 20));
    }

    [Fact]
    public void Zoom_MagnifiesAboutTheCentre()
    {
        using var surface = Coordinates();
        var frame = Frame(band: 0, bend: 0, zoom: 2);
        Lens(surface, frame);
        var c = Pixel(surface, 140, 80);            // 20.5 px right of the centre (120, 80)...
        Assert.Equal(130, c.Red, 1.5);              // ...shows what lies 10.25 px right of it
        AssertSamples(surface, frame, 100, 70);
    }

    [Fact]
    public void Dispersion_SplitsRedFromBlueOnTheRimOnly()
    {
        using var plain = Coordinates();
        Lens(plain, Frame());
        using var split = Coordinates();
        Lens(split, Frame(dispersion: 1));

        // At the top rim red reads further inward (larger y in the green channel's terms is
        // where green looks; red's own value is x, so compare on the left end where the pull
        // is horizontal: red samples further right than green would).
        var p = Pixel(plain, 43, 80);
        var s = Pixel(split, 43, 80);
        Assert.True(s.Red > p.Red + 2, $"red {s.Red} vs plain {p.Red}: no split");
        Assert.Equal(p.Green, s.Green);
        // The centre is untouched.
        Assert.Equal(Pixel(plain, 120, 80), Pixel(split, 120, 80));
    }

    [Fact]
    public void Saturation_LeavesGreyGrey_AndMakesColourMoreVivid()
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor(120, 120, 120));
        using (var muted = new SKPaint { Color = new SKColor(160, 110, 110) })
            surface.Canvas.DrawRect(new SKRect(100, 60, 140, 100), muted);
        Lens(surface, Frame(band: 0, bend: 0, saturation: 1.5f));
        var grey = Pixel(surface, 60, 80);
        Assert.Equal(grey.Red, grey.Green);
        Assert.Equal(grey.Green, grey.Blue);
        var colour = Pixel(surface, 120, 80);
        Assert.True(colour.Red - colour.Green > 50 + 10, $"{colour}: not more saturated than 160/110/110");
    }

    [Fact]
    public void Tone_SqueezesTheBackdropsLuminance_SoTextOnTheGlassKeepsItsContrast()
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using (var black = new SKPaint { Color = SKColors.Black })
            surface.Canvas.DrawRect(new SKRect(0, 0, 120, H), black);
        using (var red = new SKPaint { Color = new SKColor(200, 40, 40) })
            surface.Canvas.DrawRect(new SKRect(140, 70, 170, 90), red);
        Lens(surface, Frame(band: 0, bend: 0, toneMin: 0.25f, toneMax: 0.6f));

        // White can show through no brighter than 0.6, black no darker than 0.25.
        Assert.Equal(153, Pixel(surface, 150, 60).Red, 2.0);
        Assert.Equal(64, Pixel(surface, 90, 80).Red, 2.0);
        // A colour keeps its hue: still red, and where the C# twin puts it.
        var c = Pixel(surface, 155, 80);
        var (r, g, b) = GlassLens.Tone(200 / 255f, 40 / 255f, 40 / 255f, 0.25f, 0.6f);
        Assert.Equal(r * 255, c.Red, 2.5);
        Assert.Equal(g * 255, c.Green, 2.5);
        Assert.True(c.Red > c.Green + 40 && c.Green == c.Blue, $"{c}");
        // Outside the panel nothing changes.
        Assert.Equal(SKColors.White, Pixel(surface, 230, 10));
    }

    [Fact]
    public void Rim_LightsTheOutline_TopBrighterThanBottom_AndNothingOutside()
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        GlassLens.DrawRim(surface.Canvas, Capsule, new GlassCornerRadii(40, 40, 40, 40), strength: 1, lineWidth: 2, glowWidth: 6, glow: 0.2f, iridescence: 0);

        var top = Pixel(surface, 120, 40);
        var bottom = Pixel(surface, 120, 119);
        Assert.True(top.Red > 120, $"top rim {top.Red}");
        Assert.True(top.Red > bottom.Red + 15, $"top {top.Red} vs bottom {bottom.Red}");
        Assert.True(Pixel(surface, 120, 80).Red < 10, "the centre is lit");
        Assert.Equal(SKColors.Black, Pixel(surface, 120, 36));
        Assert.Equal(SKColors.Black, Pixel(surface, 20, 20));
    }

    [Fact]
    public void Rim_Iridescence_ColoursTheFringeButNotTheCentre()
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        GlassLens.DrawRim(surface.Canvas, Capsule, new GlassCornerRadii(40, 40, 40, 40), strength: 1, lineWidth: 2, glowWidth: 6, glow: 0, iridescence: 1);
        var spread = 0;
        for (var x = 50; x < 190; x += 10)
        {
            var c = Pixel(surface, x, 44);
            spread = Math.Max(spread, Math.Max(Math.Abs(c.Red - c.Green), Math.Abs(c.Green - c.Blue)));
        }
        Assert.True(spread > 8, $"no colour in the fringe (max channel spread {spread})");
        Assert.Equal(SKColors.Black, Pixel(surface, 120, 80));
    }

    [Fact]
    public void TryMapToDevice_ScalesAxisAlignedPanels_AndRefusesRotatedOnes()
    {
        var radii = new GlassCornerRadii(10, 10, 10, 10);
        var m = SKMatrix.CreateScale(2.625f, 2.625f).PostConcat(SKMatrix.CreateTranslation(30, 40));
        Assert.True(GlassLens.TryMapToDevice(m, new SKRect(0, 0, 100, 40), radii, out var device, out var r, out var scale));
        Assert.Equal(new SKRect(30, 40, 30 + 262.5f, 40 + 105), device);
        Assert.Equal(26.25f, r.TopLeft, 3);
        Assert.Equal(2.625f, scale, 3);
        Assert.False(GlassLens.TryMapToDevice(SKMatrix.CreateRotationDegrees(10), new SKRect(0, 0, 100, 40), radii, out _, out _, out _));
    }

    [AvaloniaFact]
    public void Panel_LensSettingsDefaultOff_SoTheDesktopDrawsAsBefore()
    {
        var panel = new GlassPanel();
        Assert.Equal(0, panel.Refraction);
        Assert.Equal(0, panel.RefractionAmount);
        Assert.Equal(0, panel.Dispersion);
        Assert.Equal(1, panel.Magnification);
        Assert.Equal(1, panel.Saturation);
        Assert.Equal(0, panel.Specular);
        Assert.Equal(0, panel.BoxShadow.Count);
        Assert.Equal(0, panel.BackdropMinLuminance);
        Assert.Equal(1, panel.BackdropMaxLuminance);
        Assert.False(panel.HasLens);
        Assert.Null(panel.LensSettings());
        Assert.True(panel.NeedsBackdrop);                // BlurRadius 18
        panel.BlurRadius = 0;
        Assert.False(panel.NeedsBackdrop);               // plain tint, no snapshot, as before
    }

    [AvaloniaFact]
    public void Panel_ALensAlone_ReadsTheBackdropWithoutABlur()
    {
        var panel = new GlassPanel { BlurRadius = 0, Refraction = 12 };
        Assert.False(panel.HasLens);                      // a band with no bend bends nothing
        panel.RefractionAmount = 14;
        Assert.True(panel.HasLens);
        Assert.True(panel.NeedsBackdrop);
        Assert.Equal(new GlassLensSettings(12, 14, 0, 1, 1), panel.LensSettings());
        panel.Refraction = 0;
        panel.Magnification = 1.1;
        Assert.True(panel.HasLens);
    }

    [AvaloniaFact]
    public void Panel_RendersEveryLensSettingWithoutThrowing()
    {
        var panel = new GlassPanel
        {
            Width = 160, Height = 60, CornerRadius = new CornerRadius(30), Background = Brushes.White,
            UseAppGlass = false, IsGlassActive = true, BlurRadius = 3, Refraction = 16, RefractionAmount = 16,
            Dispersion = 1, Magnification = 1.1, Saturation = 1.3, Specular = 0.8,
            BoxShadow = BoxShadows.Parse("0 4 18 0 #29000000"),
        };
        var window = new Avalonia.Controls.Window { Content = panel, Width = 300, Height = 200 };
        window.Show();
        panel.IsGlassActive = false;
        panel.IsGlassActive = true;
        panel.Refraction = 0;
        window.Close();
    }
}
