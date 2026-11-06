using System;
using System.Linq;
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
/// Phone beta: "it drops fps when moving menus around, especially when having to load covers".
/// A grid of cold covers started one pool decode per tile at once (each a full-size decode of a
/// 3000 px cover on the phone), taking the cores the UI and render threads need. The phone caps
/// them (CachedImage.MaxConcurrentDecodes = 2); every cover still loads.
/// </summary>
[Collection("ArtworkCache")]
public class CachedImageDecodeGateTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public CachedImageDecodeGateTests(ITestOutputHelper output)
    {
        _output = output;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        CachedImage.MaxConcurrentDecodes = 0;
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
    }

    private static Bitmap Make(int width) =>
        new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private async Task<int> PeakConcurrentDecodes(int limit)
    {
        ArtworkCache.ClearForTests();
        CachedImage.MaxConcurrentDecodes = limit;
        var running = 0;
        var peak = 0;
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            Thread.Sleep(40);   // a cover decode
            Interlocked.Decrement(ref running);
            return Make(width);
        };
        var host = new WrapPanel();
        var window = new Window { Width = 900, Height = 900, Content = host };
        window.Show();
        var tag = Guid.NewGuid().ToString("N");
        var images = Enumerable.Range(0, 16)
            .Select(i => new CachedImage { Width = 120, Height = 120, DecodeWidth = 128, SourcePath = $"C:\\art\\{tag}-{i}.jpg" })
            .ToList();
        foreach (var image in images) host.Children.Add(image);

        var deadline = Environment.TickCount64 + 30_000;
        while (images.Any(i => i.Source == null) && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
        Assert.All(images, i => Assert.NotNull(i.Source));
        window.Close();
        return peak;
    }

    [AvaloniaFact]
    public async Task ColdGrid_DecodesAtMostTheCapAtOnce_AndEveryCoverLoads()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), io);
        try
        {
            var unbounded = await PeakConcurrentDecodes(0);
            var capped = await PeakConcurrentDecodes(2);
            _output.WriteLine($"16 cold covers: peak concurrent decodes {unbounded} unbounded, {capped} capped at 2");
            Assert.True(capped <= 2, $"{capped} decodes at once");
            Assert.True(unbounded > 2, "without the cap the decodes did not pile up: the test proves nothing");
        }
        finally
        {
            ThreadPool.SetMinThreads(workers, io);
        }
    }
}
