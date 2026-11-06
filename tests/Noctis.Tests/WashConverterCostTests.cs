using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone beta: "song skipping is a bit laggy". Every skip hands Now Playing, the lyrics page and
/// the queue page a new cover bitmap, and each one's backdrop is the cover's "wash"
/// (PreBlurredArtworkConverter), made on the UI thread. Now Playing's cover is decoded at the
/// screen's pixel size (~1080 px on a phone): the wash read all of it into one managed array
/// first, a 4.6 MB large-object allocation per cover per page, the kind that brings on the
/// phone's long full collections. These pin what one wash costs.
/// </summary>
public class WashConverterCostTests
{
    private readonly ITestOutputHelper _output;
    public WashConverterCostTests(ITestOutputHelper output) => _output = output;

    private static Bitmap Cover(int size, int seed)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var rng = new Random(seed);
        var row = new byte[fb.RowBytes];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = x * 4;
                row[i] = (byte)(x * 255 / size);
                row[i + 1] = (byte)(y * 255 / size);
                row[i + 2] = (byte)rng.Next(256);
                row[i + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bitmap;
    }

    /// <summary>The banded read gives exactly the pixels the whole-cover read gave (a verbatim
    /// port of it), so no wash changes.</summary>
    [AvaloniaTheory]
    [InlineData(1080, 1080)]
    [InlineData(1000, 700)]
    [InlineData(100, 100)]
    // TODO: in full-suite runs both the reference and the banded read see junk from this 129x3
    // WriteableBitmap (alpha 0 in an all-opaque cover); passes alone. Investigate.
    [InlineData(129, 3, Skip = "Quarantined for 1.6.8: the 129x3 test bitmap reads back junk in full-suite runs only.")]
    public void BandedDownscale_MatchesTheWholeCoverRead(int width, int height)
    {
        var cover = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var rng = new Random(width * 31 + height);
        var stride = width * 4;
        var all = new byte[stride * height];
        rng.NextBytes(all);
        for (var i = 3; i < all.Length; i += 4) all[i] = 255;   // opaque, so every value is valid premultiplied
        using (var fb = cover.Lock())
            for (var y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(all, y * stride, fb.Address + y * fb.RowBytes, stride);

        var banded = PreBlurredArtworkConverter.ReadDownscaled(cover, out var w, out var h);

        // The old read: the whole cover at once.
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(all, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { cover.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), all.Length, stride); }
        finally { handle.Free(); }

        var scale = Math.Min(1.0, 128 / (double)Math.Max(width, height));
        Assert.Equal(Math.Max(1, (int)Math.Round(width * scale)), w);
        Assert.Equal(Math.Max(1, (int)Math.Round(height * scale)), h);
        var reference = new byte[w * h * 4];
        for (var dy = 0; dy < h; dy++)
        {
            var y0 = dy * height / h;
            var y1 = Math.Max(y0 + 1, (dy + 1) * height / h);
            for (var dx = 0; dx < w; dx++)
            {
                var x0 = dx * width / w;
                var x1 = Math.Max(x0 + 1, (dx + 1) * width / w);
                long b = 0, g = 0, r = 0, a = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        var i = y * stride + x * 4;
                        b += all[i]; g += all[i + 1]; r += all[i + 2]; a += all[i + 3];
                    }
                var n = (y1 - y0) * (x1 - x0);
                var di = (dy * w + dx) * 4;
                reference[di] = (byte)(b / n);
                reference[di + 1] = (byte)(g / n);
                reference[di + 2] = (byte)(r / n);
                reference[di + 3] = (byte)(a / n);
            }
        }
        Assert.Equal(reference, banded);
    }

    [AvaloniaFact]
    public void Wash_Of_A_PhoneSized_Cover_AllocatesLittle()
    {
        var conv = new PreBlurredArtworkConverter();
        // Warm-up (JIT, kernel).
        conv.Convert(Cover(256, 0), typeof(object), PreBlurredArtworkConverter.WashParameter, CultureInfo.InvariantCulture);

        const int runs = 6;
        var covers = new Bitmap[runs];
        for (var i = 0; i < runs; i++) covers[i] = Cover(1080, i + 1);
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        foreach (var cover in covers)
            Assert.NotNull(conv.Convert(cover, typeof(object), PreBlurredArtworkConverter.WashParameter, CultureInfo.InvariantCulture));
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / runs;
        var kb = (GC.GetAllocatedBytesForCurrentThread() - alloc) / runs / 1024.0;
        _output.WriteLine($"1080 px cover wash: {ms:F1} ms, {kb:F0} KB allocated (desktop CPU)");
        Assert.True(kb < 1024, $"{kb:F0} KB allocated per wash");
    }
}
