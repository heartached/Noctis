using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Jafezy, 11-04): "covers above 1000 pixels, laptop 3840 x 2400: scrolling my library
/// is slow when I maximise Noctis and smooth when I make the window smaller". In Automatic mode
/// the Albums grid keeps five covers per row, so a maximised 4K window draws ~700 device px
/// tiles (the 768 bucket) where a small window draws ~300 (the 384 bucket).
///
/// The benchmark (explicit: NOCTIS_PERF_HARNESS=1) scrolls the real Albums view, with the real
/// cover cache and disk thumbnails over real 3000×3000 cover files, at a maximised 4K window
/// (Interface Size 2.0 on a 3840×2400 window: 1920×1200 logical, tiles at device size) and at
/// a small one, and prints decodes, decoded bytes, UI-thread time per scroll frame and
/// allocations. NOCTIS_TEST_SKIA=1 adds the software-rasterized frame time (its UI numbers
/// carry the headless pipeline's own induced collections; compare the stub runs).
///
/// 11-04, 100 JPEG covers, wheel glide down/up/down (stubs, 24 cores):
///   before: 4K cold pool 31.0 s, 15 decodes at once; flick (160 px/frame) 38.2 s, 192 decodes
///   after:  4K cold pool 16.9 s, 4 at once;          flick 18.0 s, 154 decodes (tiles passed skip)
/// The rest of the gap to a small window (pool 12.3 s) is the 768 bucket's four times the pixels.
/// </summary>
[Collection("ArtworkCache")]
public class HiDpiGridScrollCostTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-hidpi-" + Guid.NewGuid().ToString("N"));
    private readonly long _maxBytes = ArtworkCache.MaxCacheBytes;

    public HiDpiGridScrollCostTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_root);
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
        ArtworkCache.MaxCacheBytes = _maxBytes;
        ArtworkThumbnailCache.Disable();
        UiScale.SetFactor(1.0);
        CachedImage.MaxConcurrentDecodes = 0;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static bool BenchEnabled => Environment.GetEnvironmentVariable("NOCTIS_PERF_HARNESS") == "1";

    /// <summary>A cover that compresses like a real one: gradient, shapes, title text, mild grain.</summary>
    internal static void WriteCover(string path, int size, int seed, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            var rnd = new Random(seed);
            SKColor C() => new((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(size, size),
                new[] { C(), C(), C() }, null, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, size, size, paint);
            using var shape = new SKPaint { IsAntialias = true };
            for (var i = 0; i < 12; i++)
            {
                shape.Color = C().WithAlpha(160);
                canvas.DrawCircle(rnd.Next(size), rnd.Next(size), rnd.Next(size / 12, size / 4), shape);
            }
            using var font = new SKFont(SKTypeface.Default, size / 10f);
            using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawText($"ALBUM {seed}", size * 0.08f, size * 0.85f, font, text);
        }
        // Grain: ±6 per channel, enough to keep PNG near real sizes.
        var p = bitmap.GetPixelSpan().ToArray();
        var noise = new byte[4093];
        new Random(seed * 7919).NextBytes(noise);
        for (var i = 0; i < p.Length; i++)
        {
            if ((i & 3) == 3) continue;
            p[i] = (byte)Math.Clamp(p[i] + noise[i % 4093] % 13 - 6, 0, 255);
        }
        System.Runtime.InteropServices.Marshal.Copy(p, 0, bitmap.GetPixels(), p.Length);
        using var data = bitmap.Encode(format, 92);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
            });
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
            });
        }
        // App.axaml's tile style (FastDownscale), which the test app does not load.
        if (!app.Styles.OfType<Avalonia.Styling.Style>().Any(s => s.Setters.OfType<Avalonia.Styling.Setter>()
                .Any(x => x.Property == CachedImage.FastDownscaleProperty)))
        {
            var style = new Avalonia.Styling.Style(x => x.OfType<Button>().Class("album-tile").Descendant().OfType<CachedImage>());
            style.Setters.Add(new Avalonia.Styling.Setter(CachedImage.FastDownscaleProperty, true));
            app.Styles.Add(style);
        }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public System.Threading.Tasks.Task PreloadAsync() => System.Threading.Tasks.Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public System.Threading.Tasks.Task FlushAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed record ScrollResult(string Name, int Tile, int Bucket, int Decodes, long DecodedBytes,
        int SourceDecodes, double PoolMs, double UiMedian, double UiP95, double UiMax, double RenderMedian,
        int Gen0, int Gen1, int Gen2, long AllocMb, long PeakResidentMb, int Pass2Decodes, double DrainMs);

    private void Report(ScrollResult r)
    {
        var line = $"{r.Name}: tile {r.Tile}px dev -> bucket {r.Bucket} | decodes {r.Decodes} ({r.SourceDecodes} from source, {r.Pass2Decodes} on revisits) " +
                   $"{r.DecodedBytes / (1024 * 1024)} MB decoded, pool {r.PoolMs:0} ms | UI/frame median {r.UiMedian:0.00} p95 {r.UiP95:0.00} max {r.UiMax:0.0} ms" +
                   (r.RenderMedian > 0 ? $" | raster median {r.RenderMedian:0.0} ms" : "") +
                   $" | GC {r.Gen0}/{r.Gen1}/{r.Gen2} alloc {r.AllocMb} MB | cache peak {r.PeakResidentMb} MB | drain {r.DrainMs:0} ms";
        Log(line);
    }

    /// <summary>Test output, and a line in NOCTIS_PERF_OUT (tag = NOCTIS_PERF_TAG) when set.</summary>
    private void Log(string line)
    {
        _o.WriteLine(line);
        var outPath = Environment.GetEnvironmentVariable("NOCTIS_PERF_OUT");
        if (!string.IsNullOrEmpty(outPath))
            File.AppendAllText(outPath, (Environment.GetEnvironmentVariable("NOCTIS_PERF_TAG") ?? "?") + "\t" + line + "\n");
    }

    [AvaloniaFact]
    public void Bench_AlbumsGridScroll_Maximized4K_vs_SmallWindow()
    {
        if (!BenchEnabled) return;
        var albums = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_PERF_ALBUMS"), out var n) && n > 0 ? n : 100;
        var format = Environment.GetEnvironmentVariable("NOCTIS_PERF_FORMAT") == "png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        var coverDir = Path.Combine(_root, "covers");
        Directory.CreateDirectory(coverDir);
        // Eight distinct covers copied to one file per album (each its own path, as in a library).
        var sources = Enumerable.Range(0, 8).Select(i =>
        {
            var p = Path.Combine(coverDir, $"src{i}.{(format == SKEncodedImageFormat.Png ? "png" : "jpg")}");
            WriteCover(p, 3000, i + 1, format);
            return p;
        }).ToList();
        var covers = Enumerable.Range(0, albums).Select(i =>
        {
            var p = Path.Combine(coverDir, $"cover{i:000}{Path.GetExtension(sources[0])}");
            File.Copy(sources[i % sources.Count], p);
            return p;
        }).ToList();
        Log($"{albums} covers 3000x3000 {format}, {new FileInfo(sources[0]).Length / 1024} KB each");

        UiScale.Install();
        // NOCTIS_PERF_GATE: most cache-miss decodes at once (0 = unlimited, as before 11-04; unset = the app's limit).
        CachedImage.MaxConcurrentDecodes = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_PERF_GATE"), out var gate)
            ? gate : CachedImage.DesktopDecodeLimit(Environment.ProcessorCount);
        Log($"decode gate {CachedImage.MaxConcurrentDecodes}, glide step {Environment.GetEnvironmentVariable("NOCTIS_PERF_STEP") ?? "40"} px/frame");
        // Small first, then maximised, each on a cold cache and a cold thumbnail folder.
        Report(RunScroll("small 1600x1400 @2.0", 1600, 1400, 2.0, covers));
        Report(RunScroll("max 3840x2400 @2.0", 3840, 2400, 2.0, covers));
        // Warm thumbnails (a second session): the maximised scroll again, same folder.
        Report(RunScroll("max 3840x2400 @2.0 (thumbs warm)", 3840, 2400, 2.0, covers, keepThumbs: true));
    }

    [Fact]
    public void DesktopDecodeLimit_LeavesCoresToTheUi_ButKeepsTwoGoing()
    {
        Assert.Equal(2, CachedImage.DesktopDecodeLimit(1));
        Assert.Equal(2, CachedImage.DesktopDecodeLimit(4));
        Assert.Equal(3, CachedImage.DesktopDecodeLimit(6));
        Assert.Equal(4, CachedImage.DesktopDecodeLimit(8));
        Assert.Equal(4, CachedImage.DesktopDecodeLimit(24));
    }

    [Fact]
    public void Thumbnail_IsEncodedAtTheFastLosslessEffort_AndStaysPixelExact()
    {
        // Effort 70 cost 4-5x the time for files within 1% (Bench_CoverDecodeSteps_ByTileWidth).
        Assert.True(ArtworkThumbnailCache.LosslessEffort <= 25);
        var cover = Path.Combine(_root, "cover.jpg");
        WriteCover(cover, 2000, 5, SKEncodedImageFormat.Jpeg);
        ArtworkThumbnailCache.Enable(Path.Combine(_root, "thumbs"));
        using var first = ArtworkThumbnailCache.DecodeToWidth(cover, 768)!;   // source decode, writes the thumbnail
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "thumbs"), "*.thumb"));
        using var second = ArtworkThumbnailCache.DecodeToWidth(cover, 768)!;  // from the thumbnail
        Assert.Equal(first.Width, second.Width);
        Assert.True(first.GetPixelSpan().SequenceEqual(second.GetPixelSpan()));
    }

    [Fact]
    public void Bench_CoverDecodeSteps_ByTileWidth()
    {
        if (!BenchEnabled) return;
        foreach (var format in new[] { SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png })
        {
            var src = Path.Combine(_root, "c." + (format == SKEncodedImageFormat.Png ? "png" : "jpg"));
            WriteCover(src, 3000, 3, format);
            foreach (var q in new[] { 0f, 10f, 25f, 50f, 70f })
            {
                using var bmp = SkiaArtworkDecoder.DecodeToWidth(src, 768)!;
                using var pix = bmp.PeekPixels();
                double enc = 0, de = 0; long size = 0;
                for (var i = 0; i < 4; i++)
                {
                    var sw = Stopwatch.StartNew();
                    using var data = pix.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, q));
                    if (i > 0) enc += sw.Elapsed.TotalMilliseconds;
                    size = data.Size;
                    var thumb = Path.Combine(_root, $"q{q}.webp");
                    File.WriteAllBytes(thumb, data.ToArray());
                    sw.Restart();
                    using (SkiaArtworkDecoder.DecodeToWidth(thumb, 768)) { }
                    if (i > 0) de += sw.Elapsed.TotalMilliseconds;
                }
                Log($"{format} 768 lossless q{q}: encode {enc / 3:0.0} ms, {size / 1024} KB, decode {de / 3:0.0} ms");
            }
            foreach (var width in new[] { 384, 512, 768 })
            {
                double dec = 0, enc = 0, thumbDec = 0; long size = 0;
                const int runs = 5;
                for (var i = 0; i < runs + 1; i++)
                {
                    var sw = Stopwatch.StartNew();
                    using var bmp = SkiaArtworkDecoder.DecodeToWidth(src, width)!;
                    var t1 = sw.Elapsed.TotalMilliseconds;
                    using var pix = bmp.PeekPixels();
                    sw.Restart();
                    using var data = pix.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 70));
                    var t2 = sw.Elapsed.TotalMilliseconds;
                    var thumb = Path.Combine(_root, $"t{width}.webp");
                    File.WriteAllBytes(thumb, data.ToArray());
                    sw.Restart();
                    using (SkiaArtworkDecoder.DecodeToWidth(thumb, width)) { }
                    var t3 = sw.Elapsed.TotalMilliseconds;
                    if (i == 0) continue;
                    dec += t1; enc += t2; thumbDec += t3; size = data.Size;
                }
                Log($"{format} 3000 -> {width}: source decode {dec / runs:0.0} ms, thumb encode (lossless q70) {enc / runs:0.0} ms ({size / 1024} KB), thumb decode {thumbDec / runs:0.0} ms");
            }
        }
    }

    /// <summary>Counts GCStart events by reason (1 = induced, 0/4 = allocation), to tell who forces the collections.</summary>
    private sealed class GcReasons : System.Diagnostics.Tracing.EventListener
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Counts = new();
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Informational, (System.Diagnostics.Tracing.EventKeywords)1);
        }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName?.StartsWith("GCStart") != true || e.Payload == null) return;
            var i = e.PayloadNames!.IndexOf("Reason"); var d = e.PayloadNames.IndexOf("Depth");
            Counts.AddOrUpdate($"reason{e.Payload[i]}/gen{e.Payload[d]}", 1, (_, n) => n + 1);
        }
    }

    private int _thumbRun;

    private ScrollResult RunScroll(string name, int width, int height, double scale, IReadOnlyList<string> covers, bool keepThumbs = false)
    {
        ArtworkCache.ClearForTests();
        if (!keepThumbs) _thumbRun++;
        ArtworkThumbnailCache.Enable(Path.Combine(_root, "thumbs" + _thumbRun));
        EnsureAppStyles();

        var decodes = 0; long decodedBytes = 0; var sourceDecodes = 0; long poolTicks = 0;
        var pass = 1; var pass2 = 0;
        var running = 0; var peakRunning = 0;
        ArtworkCache.DecoderOverride = (path, w) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peakRunning)) < now && Interlocked.CompareExchange(ref peakRunning, now, seen) != seen) { }
            var sw = Stopwatch.StartNew();
            var dir = ArtworkThumbnailCache.Directory!;
            var before = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.thumb").Length : 0;
            using var decoded = ArtworkThumbnailCache.DecodeToWidth(path, w);
            var after = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.thumb").Length : 0;
            // Real pixels with real Skia; with the drawing stubs a same-size stand-in (the stub
            // backend cannot wrap Skia pixels), so the layout still sees a bitmap of that size.
            Bitmap? bmp = decoded is null ? null
                : HeadlessTestApp.RealRendering ? SkiaArtworkDecoder.ToAvaloniaBitmap(decoded)
                : new WriteableBitmap(new PixelSize(decoded.Width, decoded.Height), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            Interlocked.Add(ref poolTicks, sw.Elapsed.Ticks);
            Interlocked.Increment(ref decodes);
            if (after > before) Interlocked.Increment(ref sourceDecodes);
            if (Volatile.Read(ref pass) >= 2) Interlocked.Increment(ref pass2);
            if (decoded != null) Interlocked.Add(ref decodedBytes, (long)decoded.Width * decoded.Height * 4);
            Interlocked.Decrement(ref running);
            return bmp;
        };

        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        UiScale.SetFactor(scale);
        window.Show();
        Pump(4);

        var cols = Math.Max(1, vm.GridColumns);
        foreach (var chunk in covers.Select((c, i) => (c, i)).GroupBy(x => x.i / cols))
        {
            vm.FilteredAlbumRows.Add(new AlbumRow
            {
                Albums = chunk.Select(x => new Album
                {
                    Id = Guid.NewGuid(), Name = $"Album {x.i}", Artist = "Artist", Year = 2020,
                    ArtworkPath = x.c, Tracks = new List<Track>(),
                }).ToList(),
            });
        }
        Pump(6);
        WaitDecodes(window);

        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Extent.Height > s.Viewport.Height);
        var device = UiScale.DeviceScaling(view);
        var tile = (int)Math.Ceiling(vm.TileArtworkSize * device);
        var bucket = ArtworkCache.NormalizeDecodeWidth(Math.Min(tile, 768));
        var max = scroller.Extent.Height - scroller.Viewport.Height;

        using var reasons = new GcReasons();
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var alloc = GC.GetTotalAllocatedBytes(true);
        var ui = new List<double>(); var raster = new List<double>();
        long peak = 0;
        // A wheel glide: ~40 logical px per 16 ms frame by default. Down, up, down.
        var step = double.TryParse(Environment.GetEnvironmentVariable("NOCTIS_PERF_STEP"), out var st) && st > 0 ? st : 40;
        var offsets = new List<double>();
        for (var y = 0.0; y <= max; y += step) offsets.Add(y);
        var path = offsets.Concat(Enumerable.Reverse(offsets)).Concat(offsets).ToList();
        for (var i = 0; i < path.Count; i++)
        {
            if (i == offsets.Count) Volatile.Write(ref pass, 2);
            var sw = Stopwatch.StartNew();
            // UI-thread work only (jobs + layout + realization), not the headless render tick.
            // With real Skia the headless pipeline induces gen2 collections of its own (GC
            // reason 7, InducedNotForced; 20 ticks at 3840×2400 = 11, while 40 cover bitmaps,
            // 10 decodes or 10 cache loads = 0-1), which would swamp the numbers.
            scroller.Offset = new Vector(0, path[i]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            ui.Add(sw.Elapsed.TotalMilliseconds);
            if (HeadlessTestApp.RealRendering && Environment.GetEnvironmentVariable("NOCTIS_PERF_RASTER") != "0" && i % 8 == 0)
            {
                var r = Stopwatch.StartNew();
                using var frame = window.CaptureRenderedFrame();
                raster.Add(r.Elapsed.TotalMilliseconds);
            }
            peak = Math.Max(peak, ArtworkCache.ResidentBytes);
            var rest = 16 - (int)sw.ElapsedMilliseconds;
            if (rest > 0) Thread.Sleep(rest);
        }
        Log($"{name}: peak concurrent decodes {peakRunning}");
        Log($"{name}: GC reasons " + string.Join(", ", reasons.Counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        var drain = Stopwatch.StartNew();
        WaitDecodes(window);
        var drainMs = drain.Elapsed.TotalMilliseconds;

        var result = new ScrollResult(name, tile, bucket, decodes, decodedBytes, sourceDecodes,
            TimeSpan.FromTicks(poolTicks).TotalMilliseconds,
            Pct(ui, 0.5), Pct(ui, 0.95), ui.Max(), raster.Count > 0 ? Pct(raster, 0.5) : 0,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
            (GC.GetTotalAllocatedBytes(true) - alloc) / (1024 * 1024), peak / (1024 * 1024), pass2, drainMs);
        window.Close();
        UiScale.SetFactor(1.0);
        Pump(2);
        ArtworkCache.DecoderOverride = null;
        return result;
    }

    private static double Pct(List<double> xs, double p)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)(s.Count * p))];
    }

    private static void Pump(int n)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitDecodes(Window window)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Pump(1);
        } while ((CachedImage.RunningDecodes > 0 || sw.ElapsedMilliseconds < 200) && sw.ElapsedMilliseconds < 60_000);
        Pump(3);
    }
}
