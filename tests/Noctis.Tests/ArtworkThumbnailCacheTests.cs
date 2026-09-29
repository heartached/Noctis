using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The disk thumbnail cache behind <see cref="ArtworkCache"/> misses (09-24, Jafezy's choppy
/// Albums scroll): a 3000px cover decoded to a grid tile costs ~88 ms, the same pixels from a
/// 384px lossless thumbnail ~2 ms. These pin that a thumbnail is pixel-identical to the full
/// decode, that a replaced or broken file never serves stale pixels, and that the folder
/// stays bounded.
/// </summary>
[Collection("ArtworkCache")]
public class ArtworkThumbnailCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-thumb-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _thumbs;
    private readonly long _maxBytes = ArtworkThumbnailCache.MaxBytes;

    public ArtworkThumbnailCacheTests()
    {
        Directory.CreateDirectory(_root);
        _thumbs = Path.Combine(_root, "thumbs");
        ArtworkThumbnailCache.Enable(_thumbs);
    }

    public void Dispose()
    {
        ArtworkThumbnailCache.Disable();
        ArtworkThumbnailCache.MaxBytes = _maxBytes;
        try { Directory.Delete(_root, recursive: true); } catch { /* a background delete may still hold a file */ }
    }

    /// <summary>A noisy cover (so a lossy or resampled copy could not pass as equal).</summary>
    private string WriteCover(string name, int size, int seed, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var rnd = new Random(seed);
        var pixels = new byte[size * size * 4];
        rnd.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        var path = Path.Combine(_root, name);
        using var data = bitmap.Encode(format, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>The thumbnails of these covers (by their path key, so nothing else in the folder counts).</summary>
    private string[] Thumbs(params string[] covers)
    {
        if (!Directory.Exists(_thumbs)) return Array.Empty<string>();
        var keys = covers.Select(c => Path.GetFileName(ArtworkThumbnailCache.ThumbPath(_thumbs, c, 0, 0, 0)).Split('-')[0] + "-").ToList();
        return Directory.GetFiles(_thumbs, "*.thumb")
            .Where(f => keys.Any(k => Path.GetFileName(f).StartsWith(k, StringComparison.Ordinal)))
            .ToArray();
    }

    private static byte[] Pixels(SKBitmap bitmap) => bitmap.Bytes;

    [Fact]
    public void SecondDecode_ComesFromTheThumbnail_PixelForPixel()
    {
        var cover = WriteCover("cover.png", 1600, seed: 1);
        using var first = ArtworkThumbnailCache.DecodeToWidth(cover, 384)!;
        Assert.Equal(384, first.Width);
        Assert.Single(Thumbs(cover));

        // Garble the source but keep its size and write time: only the thumbnail can still
        // produce the original pixels.
        var stamp = File.GetLastWriteTimeUtc(cover);
        var length = new FileInfo(cover).Length;
        File.WriteAllBytes(cover, new byte[length]);
        File.SetLastWriteTimeUtc(cover, stamp);

        using var second = ArtworkThumbnailCache.DecodeToWidth(cover, 384);
        Assert.NotNull(second);
        Assert.Equal(first.Width, second!.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.True(Pixels(first).AsSpan().SequenceEqual(Pixels(second)), "the thumbnail changed the pixels");
    }

    [Fact]
    public void ReplacedCover_IsNeverServedFromTheOldThumbnail()
    {
        var cover = WriteCover("cover.png", 1600, seed: 1);
        using (ArtworkThumbnailCache.DecodeToWidth(cover, 384)) { }

        WriteCover("cover.png", 1600, seed: 2);
        File.SetLastWriteTimeUtc(cover, DateTime.UtcNow.AddMinutes(1));
        using var fresh = Helpers.SkiaArtworkDecoder.DecodeToWidth(cover, 384)!;
        using var served = ArtworkThumbnailCache.DecodeToWidth(cover, 384)!;

        Assert.True(Pixels(fresh).AsSpan().SequenceEqual(Pixels(served)), "a replaced cover showed the old picture");
    }

    [Fact]
    public void BrokenThumbnail_IsRebuiltFromTheSource()
    {
        var cover = WriteCover("cover.png", 1600, seed: 3);
        using var reference = Helpers.SkiaArtworkDecoder.DecodeToWidth(cover, 384)!;
        using (ArtworkThumbnailCache.DecodeToWidth(cover, 384)) { }
        var thumb = Assert.Single(Thumbs(cover));
        File.WriteAllBytes(thumb, new byte[] { 1, 2, 3 }); // a crash mid-write, a foreign file

        using var served = ArtworkThumbnailCache.DecodeToWidth(cover, 384)!;
        Assert.True(Pixels(reference).AsSpan().SequenceEqual(Pixels(served)));
        using var rebuilt = Helpers.SkiaArtworkDecoder.DecodeToWidth(Assert.Single(Thumbs(cover)), 384);
        Assert.NotNull(rebuilt);
    }

    [Fact]
    public void OnlyARealShrinkIsCached()
    {
        var small = WriteCover("small.png", 600, seed: 4);   // < 2× the 384 request: decodes fast anyway
        var big = WriteCover("big.png", 2000, seed: 5);
        using (ArtworkThumbnailCache.DecodeToWidth(small, 384)) { }
        using (ArtworkThumbnailCache.DecodeToWidth(big, 1024)) { } // above the thumbnail cap (hero, lyrics)
        Assert.Empty(Thumbs(small, big));

        using (ArtworkThumbnailCache.DecodeToWidth(big, 768)) { }
        Assert.Single(Thumbs(small, big));
    }

    [Fact]
    public void Disabled_ReadsTheSourceAndWritesNothing()
    {
        ArtworkThumbnailCache.Disable();
        var cover = WriteCover("cover.png", 1600, seed: 6);
        using var decoded = ArtworkThumbnailCache.DecodeToWidth(cover, 384);
        Assert.NotNull(decoded);
        Assert.Empty(Thumbs(cover));
    }

    [Fact]
    public async Task Invalidate_DeletesOnlyThatCoversThumbnails()
    {
        var a = WriteCover("a.png", 1600, seed: 7);
        var b = WriteCover("b.png", 1600, seed: 8);
        using (ArtworkThumbnailCache.DecodeToWidth(a, 384)) { }
        using (ArtworkThumbnailCache.DecodeToWidth(a, 256)) { }
        using (ArtworkThumbnailCache.DecodeToWidth(b, 384)) { }
        Assert.Equal(3, Thumbs(a, b).Length);

        ArtworkThumbnailCache.Invalidate(a);
        for (var i = 0; i < 100 && Thumbs(a).Length > 0; i++) await Task.Delay(20);

        var left = Assert.Single(Thumbs(a, b));
        Assert.Equal(ArtworkThumbnailCache.ThumbPath(_thumbs, b, 384, new FileInfo(b).Length, File.GetLastWriteTimeUtc(b).Ticks), left);
    }

    [Fact]
    public void Trim_DropsTheLeastRecentlyUsedFirst()
    {
        var covers = Enumerable.Range(0, 4).Select(i => WriteCover($"c{i}.png", 1024, seed: 10 + i)).ToList();
        foreach (var c in covers)
            using (ArtworkThumbnailCache.DecodeToWidth(c, 256)) { }
        var thumbs = covers.Select(c => ArtworkThumbnailCache.ThumbPath(_thumbs, c, 256, new FileInfo(c).Length, File.GetLastWriteTimeUtc(c).Ticks)).ToList();
        Assert.All(thumbs, t => Assert.True(File.Exists(t)));
        for (var i = 0; i < thumbs.Count; i++)
            File.SetLastWriteTimeUtc(thumbs[i], DateTime.UtcNow.AddDays(-10 + i)); // c0 oldest … c3 newest

        // One byte over budget: the trim goes down to four fifths of it, oldest first.
        ArtworkThumbnailCache.MaxBytes = thumbs.Sum(t => new FileInfo(t).Length) - 1;
        ArtworkThumbnailCache.Trim();

        Assert.False(File.Exists(thumbs[0]));
        Assert.True(File.Exists(thumbs[3]));
        Assert.True(Thumbs(covers.ToArray()).Sum(t => new FileInfo(t).Length) <= ArtworkThumbnailCache.MaxBytes / 5 * 4);
    }
}
