using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone beta 2, #mobile-beta: the Now Playing background "almost always prefers a very dark
/// colour"; a cover of 60% black and 40% very bright colour got a dark background. The wash
/// (PreBlurredArtworkConverter "wash") now pulls a cover's dull pixels toward its strongest
/// vivid colour before the blur (Vivify), and leaves a cover with no real colour as it is.
/// Synthetic premultiplied BGRA pixels, the converter's own 128 px working format.
/// </summary>
public class MobileWashVividTests
{
    private static byte[] Pixels(int count, params (double Share, byte R, byte G, byte B)[] parts)
    {
        var pixels = new byte[count * 4];
        var at = 0;
        foreach (var (share, r, g, b) in parts)
        {
            var end = Math.Min(count, at + (int)Math.Round(share * count));
            for (; at < end; at++)
            {
                pixels[at * 4] = b;
                pixels[at * 4 + 1] = g;
                pixels[at * 4 + 2] = r;
                pixels[at * 4 + 3] = 255;
            }
        }
        return pixels;
    }

    private static (double R, double G, double B) Mean(byte[] pixels)
    {
        double r = 0, g = 0, b = 0;
        var n = pixels.Length / 4;
        for (var i = 0; i < pixels.Length; i += 4) { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; }
        return (r / n, g / n, b / n);
    }

    [Fact]
    public void SixtyPercentBlack_FortyPercentVivid_TheVividColourWins()
    {
        var pixels = Pixels(128 * 128, (0.6, 0, 0, 0), (0.4, 230, 30, 40));
        var before = Mean(pixels);

        Assert.Equal<(byte, byte, byte)?>(((byte)40, (byte)30, (byte)230), PreBlurredArtworkConverter.VividAccent(pixels));
        PreBlurredArtworkConverter.Vivify(pixels);
        var after = Mean(pixels);

        // Before: a dark maroon (the old wash's average). After: red, most of the way to the cover's.
        Assert.True(before.R < 100, $"before R {before.R:F0}");
        Assert.True(after.R > 180, $"after R {after.R:F0}");
        Assert.True(after.R > after.G * 3 && after.R > after.B * 3, $"after {after}");
        // The red pixels keep their own colour.
        Assert.Equal(new byte[] { 40, 30, 230, 255 }, pixels[^4..]);
    }

    [Fact]
    public void BlackAndTwoVividColours_TheBiggerOneWins()
    {
        var pixels = Pixels(10_000, (0.55, 5, 5, 5), (0.30, 20, 200, 60), (0.15, 40, 90, 240));
        var accent = PreBlurredArtworkConverter.VividAccent(pixels);
        Assert.NotNull(accent);
        Assert.True(accent!.Value.G > 150 && accent.Value.R < 80, $"accent {accent}");
    }

    [Fact]
    public void MostlyOneColour_ThatColour()
    {
        var pixels = Pixels(10_000, (0.8, 40, 200, 90), (0.2, 250, 250, 250));
        PreBlurredArtworkConverter.Vivify(pixels);
        var mean = Mean(pixels);
        Assert.True(mean.G > mean.R * 1.6 && mean.G > mean.B * 1.6, $"mean {mean}");
        // White (no chroma) is dull: it goes green as well.
        Assert.True(pixels[^3] > 180 && pixels[^2] < 120, $"white became {pixels[^2]},{pixels[^3]},{pixels[^4]}");
    }

    [Fact]
    public void NoMeaningfulColour_LeftAsItIs()
    {
        // Black, greys and a 2% speck of colour: no colour to show, so dark stays dark.
        var pixels = Pixels(10_000, (0.6, 0, 0, 0), (0.38, 120, 120, 125), (0.02, 255, 0, 0));
        var copy = (byte[])pixels.Clone();
        Assert.Null(PreBlurredArtworkConverter.VividAccent(pixels));
        PreBlurredArtworkConverter.Vivify(pixels);
        Assert.Equal(copy, pixels);
    }

    [Fact]
    public void DarkSaturatedCover_IsNotVivid_LeftAsItIs()
    {
        // A deep navy cover: saturated but dark; the wash keeps it navy (no brighter colour to pick).
        var pixels = Pixels(10_000, (1.0, 10, 20, 70));
        var copy = (byte[])pixels.Clone();
        PreBlurredArtworkConverter.Vivify(pixels);
        Assert.Equal(copy, pixels);
    }

    [Fact]
    public void TransparentPixels_StayTransparent()
    {
        var pixels = Pixels(1_000, (0.5, 0, 0, 0), (0.5, 230, 30, 40));
        pixels[0] = pixels[1] = pixels[2] = pixels[3] = 0;
        PreBlurredArtworkConverter.Vivify(pixels);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels[..4]);
    }
}
