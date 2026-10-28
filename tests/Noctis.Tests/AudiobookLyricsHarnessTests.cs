using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Evidence harness for the choppy-audiobook report (Discord, nutf!xx, 10-28): an
/// audiobook-sized word-synced TTML on the lyrics page. Reports the parse cost, the cost of
/// putting the lines on the page, and the per-frame cost of the word clock (UI time, managed
/// allocation, GC count and GC pause) — the pauses stop every managed thread, the render
/// thread too. Explicit only: NOCTIS_AUDIOBOOK_TTML names the file.
/// </summary>
public class AudiobookLyricsHarnessTests
{
    private readonly ITestOutputHelper _o;
    public AudiobookLyricsHarnessTests(ITestOutputHelper o) => _o = o;

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private readonly record struct Cost(double Ms, long Bytes, int Gc0, int Gc1, int Gc2, double PauseMs);

    private static Cost Time(Action a)
    {
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        a();
        return new Cost(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
            (GC.GetTotalPauseDuration() - pause).TotalMilliseconds);
    }

    private void Report(string label, Cost c) =>
        _o.WriteLine($"{label}: {c.Ms:0.0} ms, alloc {c.Bytes / 1048576.0:0.0} MB, GC {c.Gc0}/{c.Gc1}/{c.Gc2}, pause {c.PauseMs:0.0} ms");

    /// <summary>A stand-in for the engine's managed render thread: wakes every 10 ms (the WASAPI
    /// period) and records how late each wake was. A gap past the 100 ms output buffer is an
    /// audible cut on the real sink.</summary>
    private sealed class RenderProbe : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly List<double> _gaps = new();

        public RenderProbe()
        {
            _thread = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                var last = sw.Elapsed.TotalMilliseconds;
                while (!_stop)
                {
                    Thread.Sleep(10);
                    var now = sw.Elapsed.TotalMilliseconds;
                    lock (_gaps) _gaps.Add(now - last);
                    last = now;
                }
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public string Take()
        {
            double[] g;
            lock (_gaps) { g = _gaps.ToArray(); _gaps.Clear(); }
            return g.Length == 0 ? "probe: no wakes" :
                $"probe: max gap {g.Max():0} ms, gaps >50 ms {g.Count(x => x > 50)}, >100 ms {g.Count(x => x > 100)}, >200 ms {g.Count(x => x > 200)}";
        }

        public void Dispose() { _stop = true; _thread.Join(); }
    }

    [AvaloniaFact]
    public void AudiobookTtml_PageAndWordClockCost()
    {
        var path = Environment.GetEnvironmentVariable("NOCTIS_AUDIOBOOK_TTML");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path), "set NOCTIS_AUDIOBOOK_TTML");
        var text = File.ReadAllText(path!);

        List<LyricLine>? lines = null;
        Report("parse", Time(() => lines = TtmlParser.Parse(text).Lines));
        Assert.NotNull(lines);
        _o.WriteLine($"lines={lines!.Count}, words={lines.Sum(l => l.Words?.Count ?? 0)}");

        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800 };
        using var probe = new RenderProbe();
        try
        {
            win.Show();
            win.Content = view;
            Pump();
            probe.Take();
            Report("page load", Time(() => { vm.LyricLines.ReplaceAll(lines); Pump(); }));
            _o.WriteLine("  " + probe.Take());
            _o.WriteLine($"visuals={view.GetVisualDescendants().Count()}, heap={GC.GetTotalMemory(false) / 1048576.0:0} MB");

            var update = typeof(LyricsViewModel).GetMethod("UpdateActiveLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var flag = typeof(LyricsViewModel).GetField("_hasSyncedLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!;
            flag.SetValue(vm, true);
            foreach (var minute in new[] { 1, 47, 90 })
            {
                var t0 = TimeSpan.FromMinutes(minute);
                probe.Take();
                Report($"jump to {minute} min", Time(() => { update.Invoke(vm, [t0]); Pump(); }));
                var frames = new List<Cost>();
                for (var i = 1; i <= 300; i++) // 5 s at 60 fps
                {
                    var t = t0 + TimeSpan.FromMilliseconds(i * 1000.0 / 60);
                    frames.Add(Time(() => { update.Invoke(vm, [t]); Pump(); }));
                }
                var sorted = frames.Select(f => f.Ms).OrderBy(x => x).ToList();
                _o.WriteLine($"  300 frames @ {minute} min: mean {sorted.Average():0.00} ms, p95 {sorted[284]:0.00}, max {sorted[^1]:0.0}; " +
                             $"alloc {frames.Sum(f => f.Bytes) / 300.0 / 1024:0} KB/frame; GC {frames.Sum(f => f.Gc0)}/{frames.Sum(f => f.Gc1)}/{frames.Sum(f => f.Gc2)}; " +
                             $"pause total {frames.Sum(f => f.PauseMs):0.0} ms, worst frame pause {frames.Max(f => f.PauseMs):0.0} ms");
                _o.WriteLine("  " + probe.Take());
            }
        }
        finally
        {
            win.Close();
        }
    }
}
