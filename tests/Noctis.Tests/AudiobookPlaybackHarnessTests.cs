using System.Diagnostics;
using System.Reflection;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Evidence harness for the choppy-audiobook report (Discord, nutf!xx, 10-28): plays a long
/// AAC file through the real player and splice engine with no device behind it
/// (NOCTIS_AOUT=dummy) and reports, per stretch, how far the audible position moved against
/// the wall clock and how many render underruns the engine counted. Explicit only:
/// NOCTIS_AUDIOBOOK_M4A names the file.
/// </summary>
public class AudiobookPlaybackHarnessTests
{
    private readonly ITestOutputHelper _o;
    public AudiobookPlaybackHarnessTests(ITestOutputHelper o) => _o = o;

    private static int Underruns(VlcAudioPlayer player)
    {
        var sink = typeof(VlcAudioPlayer).GetField("_gaplessSink", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(player) as GaplessSink;
        if (sink == null) return -1;
        return (int)typeof(GaplessSpliceProvider).GetField("_underrunCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sink.Provider)!;
    }

    [Fact]
    public void LongAac_PlaysAtRealTime_AfterSeeks()
    {
        var path = Environment.GetEnvironmentVariable("NOCTIS_AUDIOBOOK_M4A");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path), "set NOCTIS_AUDIOBOOK_M4A");
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");

        using var player = new VlcAudioPlayer();
        long heard = 0;
        string? error = null;
        player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
        player.PlaybackError += (_, e) => error = e;
        player.Play(path!);
        var start = Stopwatch.StartNew();
        while (Interlocked.Read(ref heard) < 1000 && start.ElapsedMilliseconds < 20000) Thread.Sleep(20);
        Assert.True(Interlocked.Read(ref heard) >= 1000, $"never played (error={error})");
        _o.WriteLine($"started after {start.ElapsedMilliseconds} ms, length={player.Duration}");

        var ratios = new List<double>();
        void Measure(string label, int ms)
        {
            var u0 = Underruns(player);
            var p0 = Interlocked.Read(ref heard);
            var sw = Stopwatch.StartNew();
            Thread.Sleep(ms);
            var advanced = Interlocked.Read(ref heard) - p0;
            var ratio = advanced / (double)sw.ElapsedMilliseconds;
            ratios.Add(ratio);
            _o.WriteLine($"{label}: wall={sw.ElapsedMilliseconds} ms, position +{advanced} ms (x{ratio:0.00}), underruns +{Underruns(player) - u0}");
        }

        Measure("head", 5000);
        foreach (var target in new[] { 45 * 60, 80 * 60, 93 * 60, 10 * 60 })
        {
            player.Seek(TimeSpan.FromSeconds(target));
            Thread.Sleep(1500);
            Measure($"after seek {target / 60} min", 5000);
        }
        // Lyric-line clicks: a seek every second to scattered places.
        var rnd = new Random(3);
        for (var i = 0; i < 8; i++)
        {
            player.Seek(TimeSpan.FromSeconds(rnd.Next(60, 5600)));
            Thread.Sleep(1000);
        }
        Thread.Sleep(1000);
        Measure("after 8 click seeks", 5000);
        player.Stop();
        _o.WriteLine($"error={error ?? "none"}");
        Assert.All(ratios, r => Assert.InRange(r, 0.9, 1.1));
    }
}
