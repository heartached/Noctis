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
    /// </summary>
    [AvaloniaFact]
    public void AWash_KeepsItsTones_NotACopyOfItsPixels()
    {
        const int Count = 20;
        using (var warm = Cover(Colors.Gray)) Wash(warm);   // the downscale's pooled buffer, the JIT
        var covers = new List<WriteableBitmap>();
        var washes = new List<object?>();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        for (var i = 0; i < Count; i++)
        {
            var cover = Cover(Color.FromRgb((byte)(i * 12), 90, 200));
            covers.Add(cover);
            washes.Add(Wash(cover));
        }
        var after = GC.GetTotalMemory(forceFullCollection: true);
        var perWash = (after - before) / Count;
        _o.WriteLine($"managed memory kept per washed cover: {perWash} B");
        Assert.All(washes, w => Assert.NotNull(WashBrush.Measure(Assert.IsAssignableFrom<IImage>(w))));
        Assert.True(perWash < 8 * 1024, $"{perWash} B kept per washed cover");
        GC.KeepAlive(washes);
        foreach (var cover in covers) cover.Dispose();
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
