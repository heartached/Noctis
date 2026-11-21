using Avalonia.Media;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>"Accent follows album art": the cover colour must land in a legible accent range.</summary>
public class ArtworkAccentTests
{
    [Fact]
    public void DarkNavy_IsLiftedIntoAccentLightness()
    {
        var hex = ArtworkAccent.TameForAccent("#1A1A2E");
        Assert.NotNull(hex);
        var hsl = Color.Parse(hex!).ToHsl();
        Assert.InRange(hsl.L, 0.42, 0.6);
        Assert.True(hsl.S >= 0.45, $"saturation {hsl.S}");
        Assert.InRange(hsl.H, 220, 260); // hue kept (blue)
    }

    [Fact]
    public void SaturatedRed_KeepsHueAndStaysInRange()
    {
        var hsl = Color.Parse(ArtworkAccent.TameForAccent("#E74856")!).ToHsl();
        Assert.InRange(hsl.H, 350, 360);
        Assert.InRange(hsl.L, 0.42, 0.6);
    }

    [Fact]
    public void GreyCover_ReturnsNull_SoUserAccentStays()
    {
        Assert.Null(ArtworkAccent.TameForAccent("#808080"));
        Assert.Null(ArtworkAccent.TameForAccent("#F2F2F2"));
        Assert.Null(ArtworkAccent.TameForAccent(null));
        Assert.Null(ArtworkAccent.TameForAccent("nope"));
    }

    /// <summary>GitHub #255: a black-and-white cover gives a neutral accent that reads on the
    /// theme, instead of leaving the user's own accent on screen.</summary>
    [Fact]
    public void GreyCover_GivesWhiteOnADarkTheme_AndNearBlackOnALightOne()
    {
        var dark = Color.Parse(ArtworkAccent.ForCover("#E8E8E8", lightTheme: false)!);
        Assert.True(dark.R == dark.G && dark.G == dark.B && dark.R >= 0xE0, $"dark theme: {dark}");
        var light = Color.Parse(ArtworkAccent.ForCover("#E8E8E8", lightTheme: true)!);
        Assert.True(light.R == light.G && light.G == light.B && light.R <= 0x40, $"light theme: {light}");
    }

    [Fact]
    public void ForCover_KeepsAColourfulCoversHue_AndStaysNullWithoutACover()
    {
        Assert.Equal(ArtworkAccent.TameForAccent("#E74856"), ArtworkAccent.ForCover("#E74856", lightTheme: false));
        Assert.Null(ArtworkAccent.ForCover(null, lightTheme: false));
        Assert.Null(ArtworkAccent.ForCover("nope", lightTheme: true));
    }

    /// <summary>The reporter's case end to end: a mostly white cover with black line art. Its
    /// accent source must stay grey (the share card's colour is given a saturation floor, which
    /// is where the spectrum's stray hue came from), so the accent turns white.</summary>
    [Fact]
    public void BlackAndWhiteCoverFile_YieldsAGreySource_AndTheNeutralAccent()
    {
        var path = Path.Combine(Path.GetTempPath(), "noctis-bw-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var bmp = new SkiaSharp.SKBitmap(400, 400))
            {
                using (var canvas = new SkiaSharp.SKCanvas(bmp))
                {
                    canvas.Clear(SkiaSharp.SKColors.White);
                    using var ink = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, StrokeWidth = 14, IsStroke = true };
                    canvas.DrawCircle(200, 200, 120, ink);
                    canvas.DrawLine(40, 60, 360, 340, ink);
                    canvas.DrawRect(30, 30, 150, 70, ink);
                }
                using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data.ToArray());
            }

            var source = Noctis.Services.ArtworkVibrantColor.GetAccentSourceHex(path);
            Assert.NotNull(source);
            Assert.True(Color.Parse(source!).ToHsl().S < 0.12, $"source {source} is not grey");
            Assert.Null(Noctis.Services.ArtworkVibrantColor.GetAccentHex(path)); // the old answer: user accent kept
            Assert.Equal(ArtworkAccent.NeutralAccent(lightTheme: false), ArtworkAccent.ForCover(source, lightTheme: false));
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp file */ }
        }
    }
}
