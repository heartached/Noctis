using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noctis.Controls;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The artwork cache's byte budget used to be an accounting cap only: evicted bitmaps
/// were never disposed, and pages parked by the view cache kept theirs reachable, so
/// the real footprint sat far above the budget. These pin the reference-counted
/// lifetime that replaced it, and the size-driven decode width in CachedImage that
/// stops a 180px tile from decoding a 768px cover.
/// </summary>
[Collection("ArtworkCache")]
public class ArtworkCacheLifetimeTests : IDisposable
{
    private readonly List<int> _requestedWidths = new();

    public ArtworkCacheLifetimeTests()
    {
        // Grace first: a leftover from an earlier test must dispose now, not in 2 s.
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
        ArtworkCache.MaxCacheBytes = 128L * 1024 * 1024;
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            _requestedWidths.Add(width);
            return new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        };
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
        ArtworkCache.MaxCacheBytes = 128L * 1024 * 1024;
    }

    private static string Path(string name) => System.IO.Path.Combine("C:\\", "art", name + ".jpg");

    [AvaloniaFact]
    public void Invalidate_DisposesAnUnheldBitmap_ButNotOneStillOnScreen()
    {
        var shown = ArtworkCache.LoadAndCache(Path("a"), 256)!;
        var idle = ArtworkCache.LoadAndCache(Path("b"), 256)!;
        ArtworkCache.Acquire(shown);
        Assert.Equal(2, ArtworkCache.LiveBitmapCount);

        ArtworkCache.Invalidate(Path("a"));
        ArtworkCache.Invalidate(Path("b"));

        // b had no holder: gone. a is held: still alive until its holder lets go.
        Assert.Equal(1, ArtworkCache.LiveBitmapCount);
        Assert.Equal(0, ArtworkCache.Count);
        Assert.True(IsDisposed(idle));
        Assert.False(IsDisposed(shown));

        ArtworkCache.Release(shown);
        Assert.Equal(0, ArtworkCache.LiveBitmapCount);
        Assert.True(IsDisposed(shown));
    }

    /// <summary>11-05: the phone turns GC memory pressure off (on Android it forces full collections,
    /// each an explicit ART GC on the UI thread). The desktop keeps it, and flipping the switch while
    /// covers are cached never leaves pressure registered or removes more than was added.</summary>
    [AvaloniaFact]
    public void MemoryPressure_FollowsTheSwitch_AndStaysBalanced()
    {
        try
        {
            Assert.True(ArtworkCache.UseMemoryPressure);   // desktop default
            var before = ArtworkCache.PressureBytes;
            ArtworkCache.LoadAndCache(Path("p1"), 256);
            Assert.Equal(before + 256L * 256 * 4, ArtworkCache.PressureBytes);

            ArtworkCache.UseMemoryPressure = false;         // the phone's setting
            ArtworkCache.LoadAndCache(Path("p2"), 256);
            Assert.Equal(before + 256L * 256 * 4, ArtworkCache.PressureBytes);

            ArtworkCache.ClearForTests();                   // disposes both: only p1's pressure comes off
            Assert.Equal(before, ArtworkCache.PressureBytes);
        }
        finally { ArtworkCache.UseMemoryPressure = true; }
    }

    [AvaloniaFact]
    public void Eviction_DisposesWhatItDrops_AndTheBudgetHolds()
    {
        // 256px BGRA = 256 KB each; a 1 MB budget fits four.
        ArtworkCache.MaxCacheBytes = 1L * 1024 * 1024;
        var first = ArtworkCache.LoadAndCache(Path("0"), 256)!;
        for (var i = 1; i < 40; i++)
            ArtworkCache.LoadAndCache(Path(i.ToString()), 256);

        Assert.True(ArtworkCache.ResidentBytes <= ArtworkCache.MaxCacheBytes,
            $"resident {ArtworkCache.ResidentBytes} over budget");
        Assert.True(IsDisposed(first), "the oldest entry was evicted but never disposed");
        // Nothing evicted lingers: everything alive is exactly what the cache still lists.
        Assert.Equal(ArtworkCache.Count, ArtworkCache.LiveBitmapCount);
    }

    [AvaloniaFact]
    public void Release_AfterEviction_DisposesOnTheLastHolder()
    {
        ArtworkCache.MaxCacheBytes = 1L * 1024 * 1024;
        var held = ArtworkCache.LoadAndCache(Path("held"), 256)!;
        ArtworkCache.Acquire(held);
        ArtworkCache.Acquire(held);
        for (var i = 0; i < 40; i++)
            ArtworkCache.LoadAndCache(Path(i.ToString()), 256);

        Assert.Null(ArtworkCache.TryGet(Path("held"), 256)); // evicted from the cache
        Assert.False(IsDisposed(held));                       // but two holders remain

        ArtworkCache.Release(held);
        Assert.False(IsDisposed(held));
        ArtworkCache.Release(held);
        Assert.True(IsDisposed(held));
    }

    [AvaloniaFact]
    public void DecodeWidths_RoundUpToBuckets_SoSimilarSurfacesShareOneDecode()
    {
        Assert.Equal(128, ArtworkCache.NormalizeDecodeWidth(64));
        Assert.Equal(256, ArtworkCache.NormalizeDecodeWidth(180));
        Assert.Equal(384, ArtworkCache.NormalizeDecodeWidth(360));
        Assert.Equal(384, ArtworkCache.NormalizeDecodeWidth(384));
        Assert.Equal(512, ArtworkCache.NormalizeDecodeWidth(385));
        Assert.Equal(2048, ArtworkCache.NormalizeDecodeWidth(9000));
    }

    [AvaloniaFact]
    public void TryGetAnyWidth_ReportsALargerCachedDecodeAsSufficient_WithinTwice()
    {
        ArtworkCache.LoadAndCache(Path("x"), 512);
        var got = ArtworkCache.TryGetAnyWidth(Path("x"), 384, out var sufficient);
        Assert.NotNull(got);
        Assert.True(sufficient);

        got = ArtworkCache.TryGetAnyWidth(Path("x"), 128, out sufficient);
        Assert.NotNull(got);
        Assert.False(sufficient); // 512 for a 128 request is four times too big: decode the small one
    }

    [AvaloniaFact]
    public async Task CachedImage_DecodesForItsOwnWidth_NotTheCap()
    {
        var image = new CachedImage { Width = 180, Height = 180, DecodeWidth = 768 };
        var window = new Window { Width = 400, Height = 400, Content = image };
        window.Show();
        await Flush();
        Assert.Equal(256, image.RequestedWidth()); // arranged into a 180px slot at 1x

        // Arranged, then given a path: the request is sized for the control, not the cap.
        image.SourcePath = Path("tile");
        await Flush();

        Assert.Equal(new[] { 256 }, _requestedWidths); // 180 logical px at 1x → 256 bucket
        Assert.NotNull(image.Source);
        Assert.NotNull(ArtworkCache.TryGet(Path("tile"), 256));
        Assert.Null(ArtworkCache.TryGet(Path("tile"), 768));
        window.Close();
    }

    [AvaloniaFact]
    public async Task CachedImage_GivenAPathBeforeItsFirstArrange_EndsUpAtItsOwnWidth()
    {
        // A recycled list container gets SourcePath before layout runs. Whatever the
        // platform does with the first arrange, the bitmap on screen must end up at
        // the control's size and be the one the cache lists for it.
        var image = new CachedImage { Width = 180, Height = 180, DecodeWidth = 768, SourcePath = Path("row") };
        var window = new Window { Width = 400, Height = 400, Content = image };
        window.Show();
        await Flush();

        var shown = Assert.IsAssignableFrom<Bitmap>(image.Source);
        Assert.Equal(256, shown.PixelSize.Width);
        Assert.Same(shown, ArtworkCache.TryGet(Path("row"), 256));
        window.Close();
    }

    [AvaloniaFact]
    public async Task CachedImage_LetsGoOfItsBitmap_WhenItLeavesTheTree()
    {
        var image = new CachedImage { Width = 100, Height = 100, SourcePath = Path("parked") };
        var host = new ContentControl { Content = image };
        var window = new Window { Width = 400, Height = 400, Content = host };
        window.Show();
        await Flush();
        var bitmap = image.Source as Bitmap;
        Assert.NotNull(bitmap);

        host.Content = null; // the page is parked, the row is recycled
        await Flush();
        Assert.Null(image.Source);

        // With no holder left, invalidating disposes it outright.
        ArtworkCache.Invalidate(Path("parked"));
        Assert.True(IsDisposed(bitmap!));

        // Coming back re-acquires from the cache.
        host.Content = image;
        await Flush();
        Assert.NotNull(image.Source);
        window.Close();
    }

    /// <summary>Runs queued jobs, ticks the headless render timer (which is what runs
    /// layout there) and yields for the pool-thread decode, a few times over.</summary>
    private static async Task Flush()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static bool IsDisposed(Bitmap bitmap)
    {
        try { _ = bitmap.PixelSize; return false; }
        catch (ObjectDisposedException) { return true; }
        catch (NullReferenceException) { return true; }
    }
}

/// <summary>
/// The classes in this collection change ArtworkCache's process-wide state (DisposeGrace,
/// MaxCacheBytes, ClearForTests, which disposes every cached bitmap even while shown). Run
/// in parallel, that disposed bitmaps other UI tests still had on screen (UiScaleTests:
/// ObjectDisposedException from Bitmap.Size in CachedImage.ArrangeOverride on CI).
/// </summary>
[CollectionDefinition("ArtworkCache", DisableParallelization = true)]
public class ArtworkCacheCollection { }
