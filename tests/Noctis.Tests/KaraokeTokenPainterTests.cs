using Noctis.Models;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>The shared word-sweep painter (karaoke card and Lyrics Page video).</summary>
public class KaraokeTokenPainterTests
{
    [Fact]
    public void DrawToken_lights_the_sung_half_and_dims_the_rest()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 120));
        surface.Canvas.Clear(SKColors.Black);
        var word = new WordTiming { Text = "HELLO", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Progress = 0.5 };
        using var face = ShareCardRenderer.ResolveTypeface(SKFontStyleWeight.Bold);
        using var paint = ShareCardRenderer.TextPaint(face, 72, SKColors.White);
        using var glow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16);
        var w = ShareCardRenderer.MeasureTextFallback(paint, "HELLO");
        KaraokeTokenPainter.DrawToken(surface.Canvas, "HELLO", 20, 90, w, word, 72, SKColors.White, 1f, 0.45f, paint, glow);
        // mean green of glyph pixels: left quarter > right quarter + 60
        Assert.True(MeanInk(surface, 20, 20 + w / 4) > MeanInk(surface, 20 + w * 3 / 4, 20 + w) + 60);
    }

    /// <summary>Mean green of the glyph pixels (green above 20, so the black background and
    /// the faintest anti-aliasing don't count) in the columns [x0, x1).</summary>
    private static double MeanInk(SKSurface surface, float x0, float x1)
    {
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        long sum = 0;
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = (int)Math.Ceiling(x0); x < (int)Math.Floor(x1); x++)
            {
                var g = bitmap.GetPixel(x, y).Green;
                if (g <= 20)
                    continue;
                sum += g;
                count++;
            }
        Assert.True(count > 0, $"no glyph pixels in columns {x0}..{x1}");
        return (double)sum / count;
    }
}
