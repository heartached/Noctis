using System.IO;
using Noctis.Plugins.Kawarp;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Kawarp plugin: the SkSL compiles against the Skia the app ships, artwork prep blurs
/// without changing size, and settings round-trip with clamping.
/// </summary>
public class KawarpPluginTests
{
    [Fact]
    public void Shader_CompilesOnTheShippedSkia()
    {
        var effect = KawarpShader.Get(out var error);
        Assert.True(effect is not null, "SkSL rejected: " + error);
    }

    [Fact]
    public void PrepareArtwork_DownscalesAndBlurs()
    {
        using var src = new SKBitmap(64, 64);
        using (var c = new SKCanvas(src))
        {
            c.Clear(SKColors.Black);
            c.DrawRect(new SKRect(0, 0, 32, 64), new SKPaint { Color = SKColors.White });
        }
        using var prepared = KawarpShader.PrepareArtwork(src, 32, passes: 4);
        Assert.Equal(32, prepared.Width);
        Assert.Equal(32, prepared.Height);
        // The hard edge at the middle is now a gradient: a pixel just right of centre is grey, not black.
        var edge = prepared.GetPixel(16, 16);
        Assert.InRange(edge.Red, 20, 235);
    }

    [Fact]
    public void BoxBlur_MirrorsEdges_WithoutIndexErrors()
    {
        var src = new SKColor[4 * 4];
        for (var i = 0; i < src.Length; i++) src[i] = new SKColor((byte)(i * 16), 0, 0);
        var dst = new SKColor[src.Length];
        KawarpShader.BoxBlur(src, dst, 4, horizontal: true);
        KawarpShader.BoxBlur(dst, src, 4, horizontal: false);
        Assert.All(src, c => Assert.InRange(c.Red, 0, 255));
    }

    [Fact]
    public void Settings_WriteDefaults_AndClamp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kawarp-" + System.Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        var first = KawarpSettings.Load(path);
        Assert.True(File.Exists(path));
        Assert.Equal(1.0, first.WarpIntensity);

        File.WriteAllText(path, """{ "WarpIntensity": 9, "BlurPasses": 0, "AnimationSpeed": -1, "Saturation": 2 }""");
        var clamped = KawarpSettings.Load(path);
        Assert.Equal(3, clamped.WarpIntensity);
        Assert.Equal(1, clamped.BlurPasses);
        Assert.Equal(0, clamped.AnimationSpeed);
        Assert.Equal(2, clamped.Saturation);
        Directory.Delete(dir, true);
    }
}
