using System.Diagnostics;
using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Evidence harness for the "crunch" at the damaged spot of a long MP3 (Discord, Tangent,
/// 11-03): plays a file with damaged data at <c>NOCTIS_MP3DMG_AT_S</c> (default 40) through
/// the real player and splice engine with no device behind it (NOCTIS_AOUT=dummy), captures
/// every sample the engine renders across the spot, and reports how rough the output is
/// there. The test files are steady sine tones, so any sample whose second difference
/// exceeds a pure tone's is decoded junk or a hard edge. Plays the file twice: the second
/// play is the "next time" the user plays it. Explicit only: NOCTIS_MP3DMG names the file.
/// </summary>
public class Mp3DamageCrunchHarnessTests
{
    private readonly ITestOutputHelper _o;
    public Mp3DamageCrunchHarnessTests(ITestOutputHelper o) => _o = o;

    // A tone of amplitude A at <= 660 Hz, 48 kHz, has |x''| <= A * (2*pi*660/48000)^2 = 0.0075 A;
    // a sample past 0.05 A is junk or a hard edge. (The junk test file joins two encodes at an
    // arbitrary phase: that seam itself measures 0.033 A on any decoder.)
    private const float RoughD2 = 0.05f;

    [Fact]
    public void DamagedMp3_RendersNoJunk_AcrossTheDamage()
    {
        var path = Environment.GetEnvironmentVariable("NOCTIS_MP3DMG");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path), "set NOCTIS_MP3DMG");
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        var atSec = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_MP3DMG_AT_S"), out var a) ? a : 40;

        var appLines = new List<string>();
        DebugLogger.IsEnabled = true;
        DebugLogger.EntryAdded += en =>
        {
            if (en.Category != DebugLogger.Category.Playback) return;
            if (!en.Action.StartsWith("Decoder.", StringComparison.Ordinal) && !en.Action.StartsWith("Mp3.", StringComparison.Ordinal)
                && !en.Action.StartsWith("GaplessEngine.Underrun", StringComparison.Ordinal)) return;
            lock (appLines) appLines.Add($"{DateTime.Now:HH:mm:ss.fff} {en.Action} | {en.Metadata}");
        };

        var results = new List<(int rough, float maxD2, double silentMs)>();
        try
        {
            using var player = new VlcAudioPlayer();
            for (var run = 1; run <= 2; run++)
                results.Add(PlayAcross(player, path!, atSec, run));
            player.Stop();
        }
        finally { NullWavePlayer.Tap = null; }
        lock (appLines) foreach (var l in appLines) _o.WriteLine("  app " + l);
        Assert.All(results, r => Assert.Equal(0, r.rough));
    }

    private (int rough, float maxD2, double silentMs) PlayAcross(VlcAudioPlayer player, string path, int atSec, int run)
    {
        var capture = new List<float>(48000 * 2 * 20);
        var capturing = false;
        var channels = 2;
        var rate = 48000;
        NullWavePlayer.Tap = (buf, n, fmt) =>
        {
            if (!Volatile.Read(ref capturing) || fmt.Encoding != WaveFormatEncoding.IeeeFloat) return;
            channels = fmt.Channels;
            rate = fmt.SampleRate;
            lock (capture)
                for (var i = 0; i + 3 < n; i += 4)
                    capture.Add(BitConverter.ToSingle(buf, i));
        };

        long heard = 0;
        void OnPos(object? s, TimeSpan p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
        player.PositionChanged += OnPos;
        try
        {
            player.Play(path);
            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref heard) < 500 && sw.ElapsedMilliseconds < 60000) Thread.Sleep(20);
            Assert.True(Interlocked.Read(ref heard) >= 500, "never played");
            player.Seek(TimeSpan.FromSeconds(atSec - 5));
            Thread.Sleep(1500);
            var startHeard = Interlocked.Read(ref heard);
            Volatile.Write(ref capturing, true);
            sw.Restart();
            while (Interlocked.Read(ref heard) < (atSec + 6) * 1000L && sw.ElapsedMilliseconds < 60000) Thread.Sleep(50);
            Volatile.Write(ref capturing, false);
            var endHeard = Interlocked.Read(ref heard);

            float[] s;
            lock (capture) s = capture.ToArray();
            if (Environment.GetEnvironmentVariable("NOCTIS_MP3DMG_DUMP") is { Length: > 0 } dump)
                using (var w = new WaveFileWriter($"{dump}-run{run}.wav", WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)))
                    w.WriteSamples(s, 0, s.Length);
            // Roughness per channel-0 sample; silence = longest run of all-zero frames after audio began.
            var frames = s.Length / channels;
            // The tone's amplitude (the player's volume scales it): the first 500 ms, before the damage.
            var amp = 0f;
            for (var f = 0; f < Math.Min(frames, rate / 2); f++) amp = Math.Max(amp, Math.Abs(s[f * channels]));
            amp = Math.Max(amp, 1e-4f);
            var win = rate / 100;
            var worst = new SortedList<float, int>(Comparer<float>.Create((x, y) => y.CompareTo(x) is 0 ? 1 : y.CompareTo(x)));
            var winMax = 0f;
            int rough = 0, firstRough = -1, lastRough = -1, silentRun = 0, longestSilent = 0;
            float maxD2 = 0, peak = 0;
            var seenAudio = false;
            for (var f = 0; f < frames; f++)
            {
                var x = s[f * channels];
                peak = Math.Max(peak, Math.Abs(x));
                if (Math.Abs(x) < 1e-5f) { if (seenAudio) longestSilent = Math.Max(longestSilent, ++silentRun); }
                else { seenAudio = true; silentRun = 0; }
                if (f < 2) continue;
                var d2 = Math.Abs(x - 2 * s[(f - 1) * channels] + s[(f - 2) * channels]) / amp;
                maxD2 = Math.Max(maxD2, d2);
                winMax = Math.Max(winMax, d2);
                if (f % win == win - 1) { worst.Add(winMax, f / win); winMax = 0; }
                if (d2 > RoughD2)
                {
                    rough++;
                    if (firstRough < 0) firstRough = f;
                    lastRough = f;
                }
            }
            var silentMs = longestSilent * 1000.0 / rate;
            _o.WriteLine($"run {run}: roughest 10 ms windows (ms into capture: maxD2/amp): " +
                         string.Join(", ", worst.Take(8).Select(w => $"{w.Value * 10}: {w.Key:0.000}")));
            _o.WriteLine($"run {run}: heard {startHeard}->{endHeard} ms, captured {frames * 1000.0 / rate:0} ms @ {rate} Hz x{channels}, " +
                         $"amp={amp:0.000}, peak={peak:0.000}, maxD2/amp={maxD2:0.0000}, roughSamples={rough}" +
                         (rough > 0 ? $" ({firstRough * 1000.0 / rate:0.0}..{lastRough * 1000.0 / rate:0.0} ms into capture)" : "") +
                         $", longestSilence={silentMs:0} ms");
            return (rough, maxD2, silentMs);
        }
        finally
        {
            player.PositionChanged -= OnPos;
        }
    }
}
