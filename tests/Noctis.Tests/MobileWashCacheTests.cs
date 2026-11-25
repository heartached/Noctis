using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noctis.Converters;
using Noctis.Mobile.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Colour Wash's per-cover cache (PreBlurredArtworkConverter's "wash"). Its builder reported
/// that a freshly written wash bitmap could not be read back ("another cover's pixels or none")
/// and kept a 64 KB copy of every wash's pixels beside it. The cause is the headless test
/// platform: its bitmap stub hands every Lock a fresh native buffer and frees it on unlock, and
/// CopyPixels reads another fresh one, so whatever a test wrote is gone (the phone's Skia bitmap
/// keeps its pixels; NOCTIS_TEST_SKIA=1 shows both). The wash now keeps only the two tones the
/// veil needs, measured from the pixels while they are in hand.
/// </summary>
[Collection("Wash memory")]
public class MobileWashCacheTests
{
    private readonly ITestOutputHelper _o;
    public MobileWashCacheTests(ITestOutputHelper o) => _o = o;

    private static WriteableBitmap Cover(Color c, int size = 128)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[size * 4];
        for (var x = 0; x < size; x++)
        {
            row[x * 4] = c.B;
            row[x * 4 + 1] = c.G;
            row[x * 4 + 2] = c.R;
            row[x * 4 + 3] = 255;
        }
        for (var y = 0; y < size; y++) Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        return bitmap;
    }

    private static object? Wash(Bitmap cover) => new PreBlurredArtworkConverter().Convert(cover, typeof(IImage),
        PreBlurredArtworkConverter.WashParameter, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The cause, shown on whichever platform runs the tests: pixels written through
    /// Lock come back through CopyPixels on Skia (the phone), and need not on the headless stub.</summary>
    [AvaloniaFact]
    public void ReadingBack_AWrittenBitmap_IsAPlatformMatter()
    {
        using var bitmap = Cover(Color.FromRgb(10, 200, 30), size: 4);
        var back = new byte[4 * 4 * 4];
        var handle = GCHandle.Alloc(back, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, 4, 4), handle.AddrOfPinnedObject(), back.Length, 16); }
        finally { handle.Free(); }
        var kept = back[0] == 30 && back[1] == 200 && back[2] == 10;
        _o.WriteLine($"real rendering: {HeadlessTestApp.RealRendering}, pixels read back: {kept}");
        if (HeadlessTestApp.RealRendering) Assert.True(kept);   // Skia, as the phone draws
    }

    /// <summary>
    /// What a washed cover keeps alive beside its 128 px wash bitmap (native): before, a 64 KB copy
    /// of the wash's pixels per cover, for as long as the cover stayed decoded; now its two tones.
    /// The managed heap is the whole process's, so whatever another thread keeps or frees between
    /// the two readings counts too (a busy run once read 8 KB a cover over 20). The class runs
    /// alone (<see cref="WashMemoryCollection"/>), over enough covers that a stray megabyte is a
    /// few KB a cover, and keeps the least of three rounds: noise only has to miss one round,
    /// while a copy of the pixels is in every round, 64 KB a cover each time.
    /// </summary>
    [AvaloniaFact]
    public void AWash_KeepsItsTones_NotACopyOfItsPixels()
    {
        const int Count = 100;
        using (var warm = Cover(Colors.Gray)) Wash(warm);   // the downscale's pooled buffer, the JIT
        var least = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var covers = new List<WriteableBitmap>();
            var washes = new List<object?>();
            var before = GC.GetTotalMemory(forceFullCollection: true);
            for (var i = 0; i < Count; i++)
            {
                var cover = Cover(Color.FromRgb((byte)(i * 2), 90, (byte)(200 - round)));
                covers.Add(cover);
                washes.Add(Wash(cover));
            }
            var after = GC.GetTotalMemory(forceFullCollection: true);
            var perWash = (after - before) / Count;
            _o.WriteLine($"round {round}: managed memory kept per washed cover: {perWash} B");
            Assert.All(washes, w => Assert.NotNull(WashBrush.Measure(Assert.IsAssignableFrom<IImage>(w))));
            least = Math.Min(least, perWash);
            GC.KeepAlive(washes);
            foreach (var cover in covers) cover.Dispose();
        }
        Assert.True(least < 8 * 1024, $"{least} B kept per washed cover");
    }

    /// <summary>The tones are measured where the wash is made, on the UI thread for Now Playing's
    /// binding: the same pick as WashBrush's per-pixel luminance, from a table, at a 128 px wash's
    /// size well under a millisecond.</summary>
    [Fact]
    public void TheTones_MatchThePerPixelLuminance_AndAreCheap()
    {
        var rng = new Random(7);
        var pixels = new byte[128 * 128 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = (byte)rng.Next(256);
            pixels[i + 3] = a;
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)rng.Next(a + 1);   // premultiplied
        }
        // The loop WashBrush.Measure ran before (DominantColorExtractor per pixel).
        Color brightest = Colors.Black, darkest = Colors.White;
        double most = -1, least = 2;
        var reference = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            byte r = pixels[i + 2], g = pixels[i + 1], b = pixels[i];
            var clear = 255 - pixels[i + 3];
            var overBlack = Color.FromRgb(r, g, b);
            var overWhite = Color.FromRgb((byte)Math.Min(255, r + clear), (byte)Math.Min(255, g + clear), (byte)Math.Min(255, b + clear));
            var lb = Noctis.Services.DominantColorExtractor.GetRelativeLuminance(overBlack);
            var lw = Noctis.Services.DominantColorExtractor.GetRelativeLuminance(overWhite);
            if (lb > most) { most = lb; brightest = overBlack; }
            if (lw < least) { least = lw; darkest = overWhite; }
        }
        var referenceMs = reference.Elapsed.TotalMilliseconds;
        PreBlurredArtworkConverter.MeasureTones(pixels);   // JIT
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tones = PreBlurredArtworkConverter.MeasureTones(pixels);
        var ms = clock.Elapsed.TotalMilliseconds;
        _o.WriteLine($"tones of a 128 px wash: {ms:0.000} ms (per-pixel luminance: {referenceMs:0.000} ms)");
        Assert.Equal(brightest, tones.Brightest);
        Assert.Equal(darkest, tones.Darkest);
        Assert.True(ms < 5, $"{ms} ms");
    }

    /// <summary>The cache is bounded by the covers themselves (ArtworkCache's budget): a wash, and
    /// what it keeps, goes once its cover is let go, however many songs were skipped through.</summary>
    [AvaloniaFact]
    public void AWash_GoesWithItsCover()
    {
        var weak = new List<WeakReference>();
        for (var i = 0; i < 30; i++) weak.Add(WashOfADroppedCover(Color.FromRgb((byte)(i * 8), 40, 120)));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alive = weak.Count(w => w.IsAlive);
        _o.WriteLine($"washes still alive after their covers went: {alive} of {weak.Count}");
        Assert.Equal(0, alive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WashOfADroppedCover(Color c)
    {
        var cover = Cover(c);
        var wash = Wash(cover);
        Assert.NotNull(wash);
        cover.Dispose();
        return new WeakReference(wash);
    }
}

/// <summary>The wash memory test reads the process's managed heap: beside other classes it read
/// what they kept and freed too. This collection runs alone.</summary>
[CollectionDefinition("Wash memory", DisableParallelization = true)]
public class WashMemoryCollection { }
