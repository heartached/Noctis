using System.Collections.Concurrent;
using Noctis.Models;
using Noctis.Services.Waveform;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #93 precompute scheduling: what gets planned (current + next two queued local
/// files), the order and concurrency of decodes, cancellation when a file leaves the plan,
/// the debounce that keeps a fast skip from decoding anything, cache hits served without
/// a decode, and failures not retried.
/// </summary>
public sealed class WaveformServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "waveform-svc-" + Guid.NewGuid().ToString("N"));
    private readonly List<WaveformService> _services = new();

    public WaveformServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string File(string name, int bytes = 64)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private sealed class FakeDecoder : IWaveformDecoder
    {
        public readonly ConcurrentQueue<string> Started = new();
        public readonly ConcurrentQueue<string> Cancelled = new();
        public readonly HashSet<string> Block = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Fail = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, ManualResetEventSlim> Hold = new(StringComparer.OrdinalIgnoreCase);
        private int _running;
        public int MaxConcurrent;

        public WaveformData? Decode(string path, CancellationToken ct)
        {
            Started.Enqueue(Path.GetFileName(path));
            var now = Interlocked.Increment(ref _running);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                if (Block.Contains(Path.GetFileName(path)))
                {
                    // Held until the service cancels it.
                    if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)))
                    {
                        Cancelled.Enqueue(Path.GetFileName(path));
                        ct.ThrowIfCancellationRequested();
                    }
                }
                if (Hold.TryGetValue(Path.GetFileName(path), out var gate) &&
                    WaitHandle.WaitAny(new[] { gate.WaitHandle, ct.WaitHandle }, TimeSpan.FromSeconds(20)) == 1)
                {
                    Cancelled.Enqueue(Path.GetFileName(path));
                    ct.ThrowIfCancellationRequested();
                }
                Thread.Sleep(20);
                if (Fail.Contains(Path.GetFileName(path))) return null;
                return new WaveformData(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private (WaveformService Service, FakeDecoder Decoder, BlockingCollection<string> Ready) Make(TimeSpan? delay = null)
    {
        var decoder = new FakeDecoder();
        var service = new WaveformService(decoder, new WaveformCache(Path.Combine(_dir, "cache")),
            new WaveformService.Options(delay ?? TimeSpan.Zero, LowPriorityThread: false));
        _services.Add(service);
        var ready = new BlockingCollection<string>();
        service.WaveformReady += (path, _) => ready.Add(Path.GetFileName(path));
        return (service, decoder, ready);
    }

    private static List<string> Take(BlockingCollection<string> ready, int count)
    {
        var got = new List<string>();
        for (var i = 0; i < count; i++)
        {
            Assert.True(ready.TryTake(out var item, Wait), $"only {got.Count}/{count} ready: {string.Join(",", got)}");
            got.Add(item!);
        }
        return got;
    }

    // ── Planner ──────────────────────────────────────────────

    private static Track Local(string path) => new() { FilePath = path };

    [Fact]
    public void Plan_IsCurrentThenTheNextTwoQueuedFiles()
    {
        var plan = WaveformPlanner.Plan(Local(@"C:\m\cur.flac"),
            new[] { Local(@"C:\m\n1.flac"), Local(@"C:\m\n2.mp3"), Local(@"C:\m\n3.m4a") });
        Assert.Equal(new[] { @"C:\m\cur.flac", @"C:\m\n1.flac", @"C:\m\n2.mp3" }, plan);
    }

    [Fact]
    public void Plan_SkipsStreamsCdTracksAndUris_AndDuplicates()
    {
        var stream = new Track { FilePath = "https://server/rest/stream?id=1", SourceType = SourceType.Navidrome };
        var cd = new Track { FilePath = "cdda:///D:/track01", SourceType = SourceType.AudioCd };
        var radio = new Track { FilePath = "http://radio.example/stream" };

        Assert.Empty(WaveformPlanner.Plan(stream, new[] { cd }));
        // The same file twice (case-folded where the file system is case-insensitive) plans once.
        var twice = OperatingSystem.IsLinux() ? @"C:\m\a.flac" : @"C:\M\A.FLAC";
        Assert.Equal(new[] { @"C:\m\a.flac" },
            WaveformPlanner.Plan(radio, new[] { Local(@"C:\m\a.flac"), Local(twice) }));
        Assert.Empty(WaveformPlanner.Plan(null, Array.Empty<Track>()));
    }

    [Fact]
    public void Plan_SkipsNetworkSources()
    {
        var smb = new Track { FilePath = @"C:\m\smb.flac", SourceType = SourceType.Smb };
        var webDav = new Track { FilePath = @"C:\m\dav.flac", SourceType = SourceType.WebDav };
        Assert.Empty(WaveformPlanner.Plan(smb, new[] { webDav }));

        Assert.True(WaveformPlanner.IsNetworkLocation(@"\\nas\music\a.flac"));
        Assert.True(WaveformPlanner.IsNetworkLocation(@"\\?\UNC\nas\music\a.flac"));
        Assert.True(WaveformPlanner.IsNetworkLocation("//nas/music/a.flac"));
        Assert.False(WaveformPlanner.IsNetworkLocation(Path.Combine(Path.GetTempPath(), "a.flac")));
    }

    // ── Scheduling ───────────────────────────────────────────

    [Fact]
    public void Decodes_InPlanOrder_OneAtATime_AndCachesTheResults()
    {
        var (service, decoder, ready) = Make();
        var a = File("a.flac"); var b = File("b.flac"); var c = File("c.flac");

        service.SetPlan(new[] { a, b, c });

        Assert.Equal(new[] { "a.flac", "b.flac", "c.flac" }, Take(ready, 3));
        Assert.Equal(new[] { "a.flac", "b.flac", "c.flac" }, decoder.Started.ToArray());
        Assert.Equal(1, decoder.MaxConcurrent);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_dir, "cache"), "*" + WaveformCache.Extension).Length);
    }

    [Fact]
    public void CachedFiles_AreServedWithoutDecodingAgain()
    {
        var (service, decoder, ready) = Make();
        var a = File("a.flac"); var b = File("b.flac");
        service.SetPlan(new[] { a, b });
        Take(ready, 2);

        // Skip to b: the new plan is b first (then nothing) — b comes straight from the cache.
        service.SetPlan(new[] { b });
        Assert.Equal(new[] { "b.flac" }, Take(ready, 1));
        Assert.Equal(2, decoder.Started.Count);

        // A new service over the same folder (next app session): a disk hit, still no decode.
        var (service2, decoder2, ready2) = Make();
        service2.SetPlan(new[] { a });
        Assert.Equal(new[] { "a.flac" }, Take(ready2, 1));
        Assert.Empty(decoder2.Started);
    }

    [Fact]
    public void AFileThatLeavesThePlan_IsCancelledMidDecode()
    {
        var (service, decoder, ready) = Make();
        var slow = File("slow.flac"); var next = File("next.flac");
        decoder.Block.Add("slow.flac");

        service.SetPlan(new[] { slow });
        SpinWait.SpinUntil(() => decoder.Started.Contains("slow.flac"), Wait);

        service.SetPlan(new[] { next }); // user skipped: slow is no longer wanted
        Assert.Equal(new[] { "next.flac" }, Take(ready, 1));
        Assert.Contains("slow.flac", decoder.Cancelled);
        // Nothing cached for the cancelled file: only next's entry exists.
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "cache"), "*" + WaveformCache.Extension));
    }

    [Fact]
    public void ADecodeStillInThePlan_IsNotCancelled()
    {
        var (service, decoder, ready) = Make();
        var next = File("next.flac"); var after = File("after.flac");
        using var gate = new ManualResetEventSlim(false);
        decoder.Hold["next.flac"] = gate;

        service.SetPlan(new[] { next });
        SpinWait.SpinUntil(() => decoder.Started.Contains("next.flac"), Wait);
        // The user skips to the track being precomputed: it stays planned (now first).
        service.SetPlan(new[] { next, after });
        gate.Set();

        // next (decoded), then after — plus next again from the cache when the pass
        // restarts on the new plan; order past the first is not the point here.
        var got = Take(ready, 3);
        Assert.Equal("next.flac", got[0]);
        Assert.Contains("after.flac", got);
        Assert.Empty(decoder.Cancelled);
        Assert.Equal(1, decoder.Started.Count(n => n == "next.flac"));
    }

    [Fact]
    public void FastSkipping_DecodesNothing_UntilThePlanSettles()
    {
        var (service, decoder, ready) = Make(delay: TimeSpan.FromMilliseconds(400));
        var a = File("a.flac"); var b = File("b.flac"); var c = File("c.flac");

        service.SetPlan(new[] { a });
        Thread.Sleep(100);
        service.SetPlan(new[] { b });
        Thread.Sleep(100);
        service.SetPlan(new[] { c });

        Assert.Equal(new[] { "c.flac" }, Take(ready, 1));
        Assert.Equal(new[] { "c.flac" }, decoder.Started.ToArray());
    }

    [Fact]
    public void AFailedDecode_IsNotRetriedThisSession()
    {
        var (service, decoder, ready) = Make();
        var bad = File("bad.flac"); var good = File("good.flac");
        decoder.Fail.Add("bad.flac");

        service.SetPlan(new[] { bad, good });
        Assert.Equal(new[] { "good.flac" }, Take(ready, 1));

        service.SetPlan(new[] { good, bad });
        Assert.Equal(new[] { "good.flac" }, Take(ready, 1)); // served from cache
        Thread.Sleep(100);
        Assert.Equal(1, decoder.Started.Count(n => n == "bad.flac"));
    }

    [Fact]
    public void AnEmptyPlan_StartsNoWorker_AndMissingFilesAreSkipped()
    {
        var (service, decoder, ready) = Make();
        service.SetPlan(Array.Empty<string>());
        service.SetPlan(new[] { Path.Combine(_dir, "gone.flac") });
        Assert.False(ready.TryTake(out _, TimeSpan.FromMilliseconds(200)));
        Assert.Empty(decoder.Started);
    }

    [Fact]
    public void AnEditedFile_IsDecodedAgain()
    {
        var (service, decoder, ready) = Make();
        var a = File("a.flac", bytes: 64);
        service.SetPlan(new[] { a });
        Take(ready, 1);

        System.IO.File.WriteAllBytes(a, new byte[128]); // re-encoded / retagged: new size
        service.SetPlan(Array.Empty<string>());
        service.SetPlan(new[] { a });
        Take(ready, 1);
        Assert.Equal(2, decoder.Started.Count);
    }
}
