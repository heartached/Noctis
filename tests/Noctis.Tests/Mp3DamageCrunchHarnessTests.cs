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

        var (results, _) = PlayTwice(path!, atSec);
        Assert.All(results, r => Assert.Equal(0, r.rough));
    }

    /// <summary>
    /// A file made here with Tangent's kind of damage (see MakeDamagedMp3) 20:30 into a
    /// 21-minute tone. The first play finds the damage while playing and switches decoders
    /// before it; the second opens on FFmpeg's decoder from the start and plays through it:
    /// no switch, no junk or hard edge, no gap. (The first play's switch leaves a click where
    /// the reopened input's fade-in starts on the decoder's near-silent priming instead of on
    /// the tone: 4 samples at 0.09-0.11 A, the same on 1.6.8; reported, not asserted.)
    /// </summary>
    [Fact]
    public void SyntheticDamagedMp3_SecondPlayOpensOnFfmpeg_AndPlaysThroughWithoutASwitch()
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on PATH");
        var dir = Path.Combine(Path.GetTempPath(), "noctis-mp3dmg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = MakeDamagedMp3(ffmpeg!, dir, seconds: 1260, damageAtS: 1230, out _);
            var (results, lines) = PlayTwice(path, 1230);
            Assert.All(results, r => Assert.InRange(r.silentMs, 0, 30));
            Assert.Equal(0, results[1].rough);
            // One switch, on the first play; the reopen there and the second play's open are on FFmpeg's decoder.
            Assert.Single(lines, l => l.Contains("Decoder.DamageAhead", StringComparison.Ordinal));
            Assert.Equal(2, lines.Count(l => l.Contains("Mp3.DecoderFallback", StringComparison.Ordinal)));
            Assert.DoesNotContain(lines, l => l.Contains("Decoder.Wedged", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private (List<(int rough, float maxD2, double silentMs)> Results, List<string> Lines) PlayTwice(string path, int atSec)
    {
        var appLines = new List<string>();
        void OnEntry(DebugLogger.LogEntry en)
        {
            if (en.Category != DebugLogger.Category.Playback) return;
            if (!en.Action.StartsWith("Decoder.", StringComparison.Ordinal) && !en.Action.StartsWith("Mp3.", StringComparison.Ordinal)
                && !en.Action.StartsWith("GaplessEngine.Underrun", StringComparison.Ordinal)) return;
            lock (appLines) appLines.Add($"{DateTime.Now:HH:mm:ss.fff} {en.Action} | {en.Metadata}");
        }
        DebugLogger.IsEnabled = true;
        DebugLogger.EntryAdded += OnEntry;

        var results = new List<(int rough, float maxD2, double silentMs)>();
        try
        {
            using var player = new VlcAudioPlayer();
            for (var run = 1; run <= 2; run++)
                results.Add(PlayAcross(player, path, atSec, run));
            player.Stop();
        }
        finally
        {
            NullWavePlayer.Tap = null;
            DebugLogger.EntryAdded -= OnEntry;
        }
        lock (appLines)
        {
            foreach (var l in appLines) _o.WriteLine("  app " + l);
            return (results, appLines.ToList());
        }
    }

    internal static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(dir.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    /// <summary>
    /// A CBR tone encoded like Tangent's recording (MPEG-2 Layer III, 22.05 kHz, 64 kbps,
    /// joint stereo, no Xing frame), damaged the way it is at 31:14: junk bytes after one frame,
    /// the next two frames' audio data overwritten (headers and side info kept), more junk after
    /// those. The ffmpeg tool decodes it to ~40 ms of full-scale noise; LibVLC drops those frames
    /// (a dip, no junk). Returns the path; <paramref name="frameIndex"/> is the frame the damage
    /// follows (0-based from the first frame).
    /// </summary>
    internal static string MakeDamagedMp3(string ffmpeg, string dir, int seconds, double damageAtS, out int frameIndex)
    {
        var clean = Path.Combine(dir, "clean.mp3");
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi",
                     "-i", $"aevalsrc=0.5*sin(2*PI*440*t):s=22050:d={seconds}", "-ac", "2",
                     "-c:a", "libmp3lame", "-b:a", "64k", "-write_xing", "0", clean })
            psi.ArgumentList.Add(arg);
        using (var p = Process.Start(psi)!)
        {
            p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
        }
        Assert.True(File.Exists(clean), "ffmpeg made no test file");
        var d = File.ReadAllBytes(clean);

        static int FrameLength(byte[] b, int i)
        {
            if (i + 4 > b.Length || b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) return 0;
            int[] kbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
            return 72000 * kbps[(b[i + 2] >> 4) & 0xF] / 22050 + ((b[i + 2] >> 1) & 1);
        }
        var at = 0;
        while (FrameLength(d, at) == 0) at++;
        frameIndex = (int)(damageAtS * 22050 / 576);
        for (var n = 0; n < frameIndex; n++) at += FrameLength(d, at);
        var a = at + FrameLength(d, at); // after the frame the damage follows
        var b = a + FrameLength(d, a);
        var c = b + FrameLength(d, b);
        var rnd = new Random(7);
        // 4 header bytes + 17 side-info bytes (MPEG-2 stereo) kept; the audio data is noise.
        for (var j = a + 21; j < b; j++) d[j] = (byte)rnd.Next(256);
        for (var j = b + 21; j < c; j++) d[j] = (byte)rnd.Next(256);
        var junk1 = new byte[52];
        var junk2 = new byte[157];
        rnd.NextBytes(junk1);
        rnd.NextBytes(junk2);
        var path = Path.Combine(dir, "damaged.mp3");
        using (var f = File.Create(path))
        {
            f.Write(d, 0, a);
            f.Write(junk1);
            f.Write(d, a, c - a);
            f.Write(junk2);
            f.Write(d, c, d.Length - c);
        }
        return path;
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
