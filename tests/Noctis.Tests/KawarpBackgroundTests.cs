using Noctis.Controls;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Built-in Kawarp background (GitHub #58): the SkSL compiles against the Skia the app ships,
/// artwork prep blurs without changing size, and the shared cover image survives until the
/// last queued draw op lets go (the crash-fix contract).
/// </summary>
public class KawarpBackgroundTests
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
        // The hard white|black edge at the middle is now a gradient.
        var edge = prepared.GetPixel(16, 16);
        Assert.InRange(edge.Red, 20, 235);
    }

    [Fact]
    public void SharedImage_LivesUntilTheLastHolderReleases()
    {
        using var bmp = new SKBitmap(4, 4);
        var shared = new SharedImage(SKImage.FromBitmap(bmp));

        Assert.True(shared.TryRetain());   // draw op #1
        shared.Release();                  // UI thread lets go (track change / detach)
        Assert.True(shared.IsAlive);
        Assert.NotNull(shared.Image);

        shared.Release();                  // draw op #1 retired by the compositor
        Assert.False(shared.IsAlive);
        Assert.Null(shared.Image);
        Assert.False(shared.TryRetain());
    }
}
