using System;
using Noctis.Controls;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The menu glass in motion (GlassPanel.Lite, phone 11-14: the full glass cost frames over 25 ms
/// through most of the pop on an S23): lite, the frost is one blur through the lens's tone, with
/// neither the spill's wide blur nor the lens shader; at 0 it is the full glass, unchanged; in
/// between the two cross-fade, and the lite frost sits close enough to the full one that the
/// cross-fade shows no step.
/// </summary>
public class GlassLiteTests
{
    private const int W = 240, H = 160;
    private static readonly SKRect Panel = new(40, 30, 200, 130);

    // The phone menu's dark glass (MenuGlass.axaml): tone 0.22..0.41, curve 2.5, lift 1, spill 0.58.
    private static GlassLensFrame Frame() =>
        new(Panel, new GlassCornerRadii(28, 28, 28, 28), 14, 10, 0, 1, 1.2f, 0.22f, 0.41f, 2.5f, 1, 0.58f, 36, 0.16f);

    private static SKSurface Grey(byte level)
    {
        var s = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        s.Canvas.Clear(new SKColor(level, level, level));
        return s;
    }

    private static SKColor DrawAt(float lite, byte backdrop, out (int Lens, int Spill, int Lite) passes)
    {
        using var surface = Grey(backdrop);
        using var path = new SKPath();
        path.AddRoundRect(new SKRoundRect(Panel, 28));
        var cache = new GlassBackdropCache();
        var (lens0, spill0, lite0) = (GlassLens.LensPasses, GlassLens.SpillPasses, GlassLens.LitePasses);
        Assert.True(cache.Draw(surface.Canvas, surface, path, 9, 1, wholePanelRepainted: true, Frame(), lite));
        passes = (GlassLens.LensPasses - lens0, GlassLens.SpillPasses - spill0, GlassLens.LitePasses - lite0);
        cache.Release();
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel((int)Panel.MidX, (int)Panel.MidY);
    }

    [Fact]
    public void Lite_SkipsTheSpillAndTheLens_FullRunsThem_InBetweenRunsBoth()
    {
        DrawAt(1, 200, out var lite);
        Assert.Equal((0, 0, 1), lite);

        DrawAt(0, 200, out var full);
        Assert.Equal((1, 1, 0), full);

        DrawAt(0.5f, 200, out var both);
        Assert.Equal((1, 1, 1), both);
    }

    [Theory]
    [InlineData((byte)20)]
    [InlineData((byte)128)]
    [InlineData((byte)235)]
    public void TheLiteFrost_SitsInTheGlasssToneRange_NoStepIntoTheFullGlass(byte backdrop)
    {
        var full = DrawAt(0, backdrop, out _);
        var lite = DrawAt(1, backdrop, out _);
        var half = DrawAt(0.5f, backdrop, out _);
        Assert.InRange(Math.Abs(full.Green - lite.Green), 0, 6);
        // The cross-fade lands between the two.
        Assert.InRange(half.Green, Math.Min(full.Green, lite.Green) - 1, Math.Max(full.Green, lite.Green) + 1);
    }

    [Fact]
    public void LiteZero_IsTheFullGlass_PixelForPixel()
    {
        using var a = Grey(90);
        using var b = Grey(90);
        using var path = new SKPath();
        path.AddRoundRect(new SKRoundRect(Panel, 28));
        var ca = new GlassBackdropCache();
        var cb = new GlassBackdropCache();
        ca.Draw(a.Canvas, a, path, 9, 0.8f, true, Frame());
        cb.Draw(b.Canvas, b, path, 9, 0.8f, true, Frame(), lite: 0);
        ca.Release();
        cb.Release();
        using var ia = a.Snapshot();
        using var ib = b.Snapshot();
        Assert.Equal(ia.Encode(SKEncodedImageFormat.Png, 100).ToArray(), ib.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }
}
