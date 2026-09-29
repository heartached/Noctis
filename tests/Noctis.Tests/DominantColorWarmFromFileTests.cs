using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyrics page warms its background colours from the cover FILE on a worker
/// (<see cref="DominantColorExtractor.WarmFromFile"/>) instead of analysing the on-screen
/// Bitmap on the UI thread. These pin that the file path fills every cache the UI reads,
/// gives the colours the image actually has, and agrees with the Bitmap-based extractor.
/// </summary>
public class DominantColorWarmFromFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-warm-" + Guid.NewGuid().ToString("N"));

    public DominantColorWarmFromFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static readonly SKColor Red = new(220, 40, 40);
    private static readonly SKColor Blue = new(40, 60, 200);

    /// <summary>Writes a PNG: one colour, or <paramref name="left"/>|<paramref name="right"/> halves.</summary>
    private string WritePng(string name, int w, int h, SKColor left, SKColor? right = null, SKColorType type = SKColorType.Bgra8888)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, type,
            type == SKColorType.Gray8 ? SKAlphaType.Opaque : SKAlphaType.Premul));
        bmp.Erase(left);
        if (right is { } r)
            bmp.Erase(r, new SKRectI(w / 2, 0, w, h));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_dir, name + ".png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static void Near(SKColor expected, Color actual, int tolerance, string what)
    {
        Assert.True(Math.Abs(expected.Red - actual.R) <= tolerance
                    && Math.Abs(expected.Green - actual.G) <= tolerance
                    && Math.Abs(expected.Blue - actual.B) <= tolerance,
            $"{what}: expected ~{expected}, got #{actual.R:X2}{actual.G:X2}{actual.B:X2}");
    }

    private static void Near(Color expected, Color actual, int tolerance, string what)
        => Near(new SKColor(expected.R, expected.G, expected.B), actual, tolerance, what);

    [Fact]
    public void SolidCover_FillsAllCaches_WithItsColour()
    {
        var c = new SKColor(200, 80, 40);
        var path = WritePng("solid", 300, 300, c);
        Assert.False(DominantColorExtractor.HasCachedColors(path));

        Assert.True(DominantColorExtractor.WarmFromFile(path));
        Assert.True(DominantColorExtractor.HasCachedColors(path));

        // Cache hits: the bitmap argument is never looked at.
        var dummy = (Bitmap)null!;
        Near(c, DominantColorExtractor.GetOrExtractDominantColor(path, dummy), 2, "dominant");
        var (dom, sec) = DominantColorExtractor.GetOrExtractPalette(path, dummy);
        Near(c, dom, 2, "palette dominant");
        Near(c, sec, 2, "palette secondary");
        Near(c, DominantColorExtractor.GetOrExtractAverageColor(path, dummy), 2, "average");
    }

    [Fact]
    public void TwoColourCover_LargerThanTheDecodeCap_PaletteIsBothHalves_AverageIsTheMean()
    {
        // 1200 wide: past the 512 subsample cap, and non-square, so the resize path runs.
        var path = WritePng("halves", 1200, 800, Red, Blue);

        Assert.True(DominantColorExtractor.WarmFromFile(path));

        var dummy = (Bitmap)null!;
        var (dom, sec) = DominantColorExtractor.GetOrExtractPalette(path, dummy);
        Near(Blue, dom, 10, "palette dominant (the darker half)");
        Near(Red, sec, 10, "palette secondary");
        var mean = new SKColor(130, 50, 120);
        Near(mean, DominantColorExtractor.GetOrExtractAverageColor(path, dummy), 4, "average");
        Near(mean, DominantColorExtractor.GetOrExtractDominantColor(path, dummy), 6, "dominant (symmetric weights)");
    }

    [Fact]
    public void GreyscalePng_IsConvertedToBgra()
    {
        // A Gray8 PNG decodes to a Gray8 SKBitmap; the 50x50 resize must convert it,
        // not reinterpret one byte per pixel as BGRA.
        var path = WritePng("grey", 300, 300, new SKColor(128, 128, 128), type: SKColorType.Gray8);

        Assert.True(DominantColorExtractor.WarmFromFile(path));
        var avg = DominantColorExtractor.GetOrExtractAverageColor(path, (Bitmap)null!);
        Near(new SKColor(128, 128, 128), avg, 3, "average");
    }

    [Fact]
    public void MissingOrGarbageFile_ReturnsFalse_AndCachesNothing()
    {
        var missing = Path.Combine(_dir, "missing.png");
        Assert.False(DominantColorExtractor.WarmFromFile(missing));
        Assert.False(DominantColorExtractor.HasCachedColors(missing));

        var garbage = Path.Combine(_dir, "garbage.png");
        File.WriteAllBytes(garbage, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Assert.False(DominantColorExtractor.WarmFromFile(garbage));
        Assert.False(DominantColorExtractor.HasCachedColors(garbage));
    }

    /// <summary>
    /// The file path and the UI (Bitmap / RenderTargetBitmap) path give the same colours.
    /// The Bitmap path needs real Skia rendering: run with NOCTIS_TEST_SKIA=1.
    /// </summary>
    [AvaloniaFact]
    public void FilePath_AgreesWithBitmapPath()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");

        foreach (var (name, w, h, left, right) in new[]
                 {
                     ("p-solid", 300, 300, new SKColor(60, 150, 90), (SKColor?)null),
                     ("p-halves", 1200, 800, Red, (SKColor?)Blue),
                 })
        {
            var path = WritePng(name, w, h, left, right);
            using var bitmap = new Bitmap(path);
            var uiDominant = DominantColorExtractor.ExtractDominantColor(bitmap);
            var (uiDom, uiSec) = DominantColorExtractor.ExtractColorPalette(bitmap);
            var uiAverage = DominantColorExtractor.ExtractAverageColor(bitmap);

            Assert.True(DominantColorExtractor.WarmFromFile(path));
            var dummy = (Bitmap)null!;
            Near(uiDominant, DominantColorExtractor.GetOrExtractDominantColor(path, dummy), 6, $"{name} dominant");
            var (dom, sec) = DominantColorExtractor.GetOrExtractPalette(path, dummy);
            Near(uiDom, dom, 6, $"{name} palette dominant");
            Near(uiSec, sec, 6, $"{name} palette secondary");
            Near(uiAverage, DominantColorExtractor.GetOrExtractAverageColor(path, dummy), 6, $"{name} average");
        }
    }
}
