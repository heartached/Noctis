using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Single-cover surfaces (ClearOnSourceChange=False: lyrics page, player bar, mini player)
/// must not paint an undersized cross-width cache hit — a 128px tile decode stretched to a
/// big cover showed blurry and then snapped sharp. They keep the previous cover until their
/// own decode lands. List tiles (the default) still take any width as a placeholder.
/// </summary>
[Collection("ArtworkCache")]
public class CachedImageSingleCoverFallbackTests : IDisposable
{
    public CachedImageSingleCoverFallbackTests()
    {
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
    }

    private static string P(string name) => System.IO.Path.Combine("C:\\", "single-cover", name + ".jpg");

    private static Bitmap Make(int width)
        => new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static async Task Flush(int settleMs = 120)
    {
        var until = DateTime.UtcNow.AddMilliseconds(settleMs);
        do
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Shows cover A (fully cached), then switches to cover B, whose only cache entry is
    /// <paramref name="cachedWidthOfB"/> wide, while B's own decode is parked. Returns
    /// (A's bitmap, B's cached bitmap, what was shown mid-decode, B's own decode, what was
    /// shown after it landed).
    /// </summary>
    private static async Task<(Bitmap A, Bitmap CachedB, object? MidDecode, Bitmap? RequestedWidthB, object? Final)>
        SwitchCover(bool clearOnSourceChange, int cachedWidthOfB)
    {
        using var gate = new ManualResetEventSlim(false);
        // 600px slot at scale 1 → the 768 bucket (DecodeWidth 1024 only caps it).
        const int requested = 768;
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            if (path == P("b") && width == requested) gate.Wait(5000);
            return Make(width);
        };
        var a = ArtworkCache.LoadAndCache(P("a"), requested)!;
        var cachedB = ArtworkCache.LoadAndCache(P("b"), cachedWidthOfB)!;

        var image = new CachedImage { Width = 600, Height = 600, DecodeWidth = 1024, ClearOnSourceChange = clearOnSourceChange };
        var window = new Window { Width = 800, Height = 800, Content = image };
        window.Show();
        await Flush();
        image.SourcePath = P("a");
        await Flush();
        Assert.Same(a, image.Source);
        Assert.Equal(requested, image.RequestedWidth());

        image.SourcePath = P("b");
        await Flush();
        var mid = image.Source;

        gate.Set();
        await Flush(400);
        var ownB = ArtworkCache.TryGet(P("b"), requested);
        var final = image.Source;
        window.Close();
        return (a, cachedB, mid, ownB, final);
    }

    [AvaloniaFact]
    public async Task SingleCover_UndersizedCrossWidthHit_KeepsThePreviousCoverUntilItsOwnDecode()
    {
        var r = await SwitchCover(clearOnSourceChange: false, cachedWidthOfB: 128);

        Assert.Same(r.A, r.MidDecode);
        Assert.NotNull(r.RequestedWidthB);
        Assert.Same(r.RequestedWidthB, r.Final);
    }

    [AvaloniaFact]
    public async Task SingleCover_OversizedCrossWidthHit_IsShownRightAway()
    {
        // 2048 ≥ 768 but more than twice it: not "sufficient", so the own decode still
        // runs, but it is sharp enough to show meanwhile.
        var r = await SwitchCover(clearOnSourceChange: false, cachedWidthOfB: 2048);

        Assert.Same(r.CachedB, r.MidDecode);
        Assert.Same(r.RequestedWidthB, r.Final);
    }

    [AvaloniaFact]
    public async Task ListTile_UndersizedCrossWidthHit_IsStillUsedAsPlaceholder()
    {
        var r = await SwitchCover(clearOnSourceChange: true, cachedWidthOfB: 128);

        Assert.Same(r.CachedB, r.MidDecode);
        Assert.Same(r.RequestedWidthB, r.Final);
    }
}
