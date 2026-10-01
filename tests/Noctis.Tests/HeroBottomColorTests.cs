using Avalonia.Media;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone album hero's page colour: the full-bleed cover fades into it, so it must be the
/// colour of the cover's BOTTOM rows (the edge extractor reads all four edges and the sky of
/// a landscape cover painted the page under its sand). Synthetic covers, 64×64 like the
/// extractor's working size unless a file decode is under test.
/// </summary>
public class HeroBottomColorTests
{
    private const int N = 64;

    private static byte[] Fill(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (r, g, b) = pixel(x, y);
            var i = (y * w + x) * 3;
            rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
        }
        return rgb;
    }

    /// <summary>OKLab distance ×100: about one "just noticeable difference" per unit.</summary>
    private static double Distance(Color a, (byte R, byte G, byte B) b)
    {
        var (l1, a1, b1) = DominantColorExtractor.ToOkLab(a.R, a.G, a.B);
        var (l2, a2, b2) = DominantColorExtractor.ToOkLab(b.R, b.G, b.B);
        return 100 * Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    [Fact]
    public void SolidCover_IsItsOwnColour()
    {
        var c = DominantColorExtractor.PickHeroBottomColor(Fill(N, N, (_, _) => (0x3A, 0x6E, 0xA5)), N, N);
        Assert.NotNull(c);
        Assert.True(Distance(c!.Value, (0x3A, 0x6E, 0xA5)) < 0.5, $"got {c}");
    }

    /// <summary>Sky over sand: the page continues the sand the hero fades out of, never the sky.</summary>
    [Fact]
    public void SkyOverSand_TakesTheBottom()
    {
        (byte, byte, byte) sky = (0x8C, 0xB8, 0xE8), sand = (0xC8, 0xA4, 0x78);
        var rgb = Fill(N, N, (_, y) => y < N * 3 / 4 ? sky : sand);
        var c = DominantColorExtractor.PickHeroBottomColor(rgb, N, N)!.Value;
        Assert.True(Distance(c, sand) < 1, $"got {c}");
        // The four-edge extractor votes the sky in (two thirds of the ring): the bug this replaces.
        var edge = DominantColorExtractor.PickEdgeBackgroundColor(rgb, N, N)!.Value;
        Assert.True(Distance(edge, sky) < Distance(edge, sand));
    }

    /// <summary>A white-framed print: the frame is the cover's bottom edge, so the page is white
    /// and the frame runs on into it.</summary>
    [Fact]
    public void WhiteFramedPrint_ContinuesTheFrame()
    {
        const int frame = 5;
        var rgb = Fill(N, N, (x, y) =>
            x < frame || y < frame || x >= N - frame || y >= N - frame ? ((byte)0xF4, (byte)0xF2, (byte)0xEE) : ((byte)0x20, (byte)0x30, (byte)0x28));
        var c = DominantColorExtractor.PickHeroBottomColor(rgb, N, N)!.Value;
        Assert.True(Distance(c, (0xF4, 0xF2, 0xEE)) < 1, $"got {c}");
    }

    /// <summary>A noisy photo floor with a black-and-white Parental Advisory label in the bottom
    /// corner: the label and the grain must not pull the colour off the floor.</summary>
    [Fact]
    public void NoisyPhoto_WithAnAdvisoryLabel_StaysOnTheFloor()
    {
        var rnd = new Random(42);
        (byte, byte, byte) floor = (0x6B, 0x4A, 0x36);
        var rgb = Fill(N, N, (x, y) =>
        {
            if (x >= N - 12 && y >= N - 8) return ((x + y) % 3 == 0) ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);
            byte J(byte v) => (byte)Math.Clamp(v + rnd.Next(-14, 15), 0, 255);
            return y < N / 2 ? ((byte)0x30, (byte)0x60, (byte)0x90) : (J(floor.Item1), J(floor.Item2), J(floor.Item3));
        });
        var c = DominantColorExtractor.PickHeroBottomColor(rgb, N, N)!.Value;
        Assert.True(Distance(c, floor) < 2.5, $"got {c}");
    }

    /// <summary>Two flat halves along the bottom: the larger one wins outright. A plain average
    /// would invent a muddy mid grey-blue found nowhere on the cover.</summary>
    [Fact]
    public void SplitBottom_TakesTheLargerSide_NotAMuddyAverage()
    {
        (byte, byte, byte) navy = (0x1C, 0x24, 0x4A), white = (0xF0, 0xF0, 0xF0);
        var rgb = Fill(N, N, (x, _) => x < N * 6 / 10 ? navy : white);
        var c = DominantColorExtractor.PickHeroBottomColor(rgb, N, N)!.Value;
        Assert.True(Distance(c, navy) < 1, $"got {c}");
    }

    /// <summary>No chroma cap here (the edge extractor caps at 0.15): a capped page under a
    /// neon cover is a visible band where the fade ends.</summary>
    [Fact]
    public void NeonCover_IsNotDesaturated()
    {
        var c = DominantColorExtractor.PickHeroBottomColor(Fill(N, N, (_, _) => (0xF6, 0x5A, 0xFB)), N, N)!.Value;
        Assert.True(Distance(c, (0xF6, 0x5A, 0xFB)) < 0.5, $"got {c}");
    }

    [Fact]
    public void NoPixels_IsNull()
    {
        Assert.Null(DominantColorExtractor.PickHeroBottomColor(Array.Empty<byte>(), 0, 0));
        Assert.Null(DominantColorExtractor.PickHeroBottomColor(new byte[3], 2, 2));
    }

    private static string WritePng(int w, int h, Action<SKCanvas> draw)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) draw(canvas);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Path.GetTempPath(), $"noctis-hero-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        return path;
    }

    private static void Rect(SKCanvas canvas, SKColor color, float l, float t, float r, float b)
    {
        using var paint = new SKPaint { Color = color };
        canvas.DrawRect(new SKRect(l, t, r, b), paint);
    }

    /// <summary>From a file: the bottom colour and the per-row luminance (what the status bar
    /// sits on as the page scrolls) both come back, from one decode, and stay cached.</summary>
    [Fact]
    public void FromFile_ReadsTheBottomAndTheRowLuminance()
    {
        var path = WritePng(400, 400, c =>
        {
            c.Clear(new SKColor(0x90, 0xC0, 0xF0));
            Rect(c, new SKColor(0x5A, 0x3C, 0x28), 0, 260, 400, 400);
        });
        try
        {
            Assert.Null(DominantColorExtractor.GetCachedHeroColors(path));
            var hero = DominantColorExtractor.ExtractHeroColorsFromFile(path);
            Assert.NotNull(hero);
            Assert.True(Distance(hero!.Bottom, (0x5A, 0x3C, 0x28)) < 1, $"bottom {hero.Bottom}");
            Assert.Equal(N, hero.RowLuminance.Count);
            var sky = DominantColorExtractor.GetRelativeLuminance(Color.FromRgb(0x90, 0xC0, 0xF0));
            var earth = DominantColorExtractor.GetRelativeLuminance(Color.FromRgb(0x5A, 0x3C, 0x28));
            Assert.Equal(sky, hero.RowLuminance[0], 2);
            Assert.Equal(earth, hero.RowLuminance[N - 1], 2);
            Assert.Equal(hero.Bottom, DominantColorExtractor.ExtractHeroBottomColorFromFile(path));
            Assert.Same(hero, DominantColorExtractor.GetCachedHeroColors(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>The hero shows the cover UniformToFill in a square, so a tall cover loses its top
    /// and bottom: the colour comes from the bottom of the square that is actually on screen.</summary>
    [Fact]
    public void TallCover_ReadsTheVisibleSquare()
    {
        var path = WritePng(200, 400, c =>
        {
            c.Clear(new SKColor(0x22, 0x88, 0x44));                    // the visible square (y 100..300)
            Rect(c, new SKColor(0xE0, 0x20, 0x20), 0, 300, 200, 400);  // cropped away below it
        });
        try
        {
            var bottom = DominantColorExtractor.ExtractHeroBottomColorFromFile(path)!.Value;
            Assert.True(Distance(bottom, (0x22, 0x88, 0x44)) < 1, $"got {bottom}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FromFile_MissingOrEmptyPath_IsNull()
    {
        Assert.Null(DominantColorExtractor.ExtractHeroColorsFromFile(null));
        Assert.Null(DominantColorExtractor.ExtractHeroColorsFromFile(Path.Combine(Path.GetTempPath(), "no-such-cover.png")));
    }
}
