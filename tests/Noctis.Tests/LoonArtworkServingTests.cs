using Noctis.Services.Loon;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression cover for the Discord cover-art relay path. The relay forwards Discord's
/// fetch to this client and fails it with a 504 if no response arrives, so the rules are:
/// every request gets an answer, and the answer is always small and honestly typed.
/// </summary>
public class LoonArtworkServingTests
{
    private static byte[] EncodePng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void ThumbnailAlwaysFitsDiscordsMaxDimension()
    {
        // Embedded cover art is routinely 3000x3000. Shipping the original is what timed
        // the relay out, so oversized input must come back scaled down.
        var thumb = LoonClient.MakeThumbnail(EncodePng(3000, 3000), 512);

        Assert.NotNull(thumb);
        using var decoded = SKBitmap.Decode(thumb);
        Assert.NotNull(decoded);
        Assert.True(decoded!.Width <= 512 && decoded.Height <= 512,
            $"expected <=512px, got {decoded.Width}x{decoded.Height}");
    }

    [Fact]
    public void ThumbnailIsAlwaysJpegEvenWhenSourceIsPng()
    {
        // PersistenceService.SaveArtwork writes whatever bytes came out of the tag into
        // "{albumId}.jpg", so a PNG under the old 2MB resize threshold was served as
        // image/jpeg with PNG bytes. Re-encoding keeps the declared type honest.
        var thumb = LoonClient.MakeThumbnail(EncodePng(300, 300), 512);

        Assert.NotNull(thumb);
        Assert.True(thumb!.Length > 3);
        Assert.Equal(0xFF, thumb[0]);
        Assert.Equal(0xD8, thumb[1]);   // JPEG SOI
        Assert.Equal(0xFF, thumb[2]);
    }

    [Fact]
    public void SmallSourceIsStillReEncodedNotPassedThrough()
    {
        var png = EncodePng(64, 64);
        var thumb = LoonClient.MakeThumbnail(png, 512);

        Assert.NotNull(thumb);
        Assert.False(thumb!.AsSpan().StartsWith(png.AsSpan()),
            "a small PNG must not be passed through as-is under an image/jpeg header");
    }

    [Fact]
    public void ThumbnailReturnsNullForUndecodableBytes()
    {
        // Must be null (-> answer the relay with an empty response) rather than falling
        // back to the multi-megabyte original, which is what caused the timeouts.
        Assert.Null(LoonClient.MakeThumbnail(new byte[] { 1, 2, 3, 4, 5 }, 512));
        Assert.Null(LoonClient.MakeThumbnail([], 512));
    }

    [Fact]
    public void UrlIsNotMintedForArtworkOutsideTheServedDirectory()
    {
        // The public URL carries only the file name and the relay resolves it inside the
        // served artwork directory. A path from anywhere else passes File.Exists locally
        // but can never be fulfilled, so Discord renders a broken-image placeholder.
        var served = Path.Combine(Path.GetTempPath(), "noctis-loon-served-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(Path.GetTempPath(), "noctis-loon-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(served);
        Directory.CreateDirectory(elsewhere);
        try
        {
            var stray = Path.Combine(elsewhere, "cover.jpg");
            File.WriteAllBytes(stray, EncodePng(8, 8));

            Assert.False(LoonClient.IsServableArtworkPath(served, stray));
            Assert.True(LoonClient.IsServableArtworkPath(served, Path.Combine(served, "cover.jpg")));
        }
        finally
        {
            Directory.Delete(served, true);
            Directory.Delete(elsewhere, true);
        }
    }
}
