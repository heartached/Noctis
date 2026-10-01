using Noctis.Helpers;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The metadata dialog's "W × H" chip must report the cover's REAL size, not the size the
/// preview happened to be decoded at (Discord, veil 2026-09-22: a 3000×3000 cover read
/// "512 × 512" because the preview is decoded down to 512 wide to save memory).
/// </summary>
public class ArtworkSourceSizeTests
{
    private static byte[] Jpeg(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.OrangeRed);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    [Fact]
    public void ReadsTheEncodedSize_NotADecodedOne()
    {
        var size = SkiaArtworkDecoder.ReadPixelSize(Jpeg(3000, 3000));
        Assert.NotNull(size);
        Assert.Equal(3000, size!.Value.Width);
        Assert.Equal(3000, size.Value.Height);

        var wide = SkiaArtworkDecoder.ReadPixelSize(Jpeg(1200, 800));
        Assert.Equal((1200, 800), (wide!.Value.Width, wide.Value.Height));
    }

    [Fact]
    public void NotAnImage_ReturnsNull()
    {
        Assert.Null(SkiaArtworkDecoder.ReadPixelSize(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(SkiaArtworkDecoder.ReadPixelSize(null));
        Assert.Null(SkiaArtworkDecoder.ReadPixelSize(System.Array.Empty<byte>()));
    }

    /// <summary>The album page's artwork viewer (#114) captions the cover file's real size.</summary>
    [Fact]
    public void ReadsTheSize_FromACoverFile()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noctis-size-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var cover = System.IO.Path.Combine(dir, "cover.jpg");
            System.IO.File.WriteAllBytes(cover, Jpeg(1400, 1200));
            var size = SkiaArtworkDecoder.ReadFilePixelSize(cover);
            Assert.Equal((1400, 1200), (size!.Value.Width, size.Value.Height));

            var junk = System.IO.Path.Combine(dir, "junk.jpg");
            System.IO.File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
            Assert.Null(SkiaArtworkDecoder.ReadFilePixelSize(junk));
            Assert.Null(SkiaArtworkDecoder.ReadFilePixelSize(System.IO.Path.Combine(dir, "missing.jpg")));
            Assert.Null(SkiaArtworkDecoder.ReadFilePixelSize(null));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
