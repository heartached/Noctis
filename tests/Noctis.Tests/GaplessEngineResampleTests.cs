using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// The splice engine converts every input to the device rate itself (LibVLC hands it over at
// the file's own rate): one resampler runs on across a same-rate natural seam, the end it
// holds is played out at a natural end or a rate change, and a cut starts a fresh one.
// Before, VLC's per-input resampler kept the last 2.7-2.9 ms of every track (harness 11-19).
public class GaplessEngineResampleTests
{
    private readonly ITestOutputHelper _out;
    public GaplessEngineResampleTests(ITestOutputHelper output) => _out = output;

    private const double Freq = 1000;
    private const double Amp = 0.5;

    private static short[] Sine(int rate, double startSeconds, int frames) =>
        Enumerable.Range(0, frames)
            .Select(n => (short)Math.Round(Amp * 32767 * Math.Sin(2 * Math.PI * Freq * (startSeconds + (double)n / rate))))
            .ToArray();

    // Renders in 10 ms reads, as the device does.
    private static float[] Render(GaplessSpliceProvider provider, int frames)
    {
        var outBuf = new float[frames];
        var chunk = provider.WaveFormat.SampleRate / 100;
        for (var at = 0; at < frames; at += chunk)
            provider.Read(outBuf, at, Math.Min(chunk, frames - at));
        return outBuf;
    }

    // Least-squares a·sin + b·cos at Freq over [from, to): the sine the output carries there.
    private static (double A, double B) Fit(float[] y, int rate, int from, int to)
    {
        double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
        for (var k = from; k < to; k++)
        {
            var w = 2 * Math.PI * Freq * k / rate;
            double s = Math.Sin(w), c = Math.Cos(w);
            ss += s * s; cc += c * c; sc += s * c; ys += y[k] * s; yc += y[k] * c;
        }
        var det = ss * cc - sc * sc;
        return ((ys * cc - yc * sc) / det, (yc * ss - ys * sc) / det);
    }

    private static double MaxResidual(float[] y, int rate, (double A, double B) fit, int from, int to)
    {
        var max = 0.0;
        for (var k = from; k < to; k++)
        {
            var w = 2 * Math.PI * Freq * k / rate;
            max = Math.Max(max, Math.Abs(y[k] - (fit.A * Math.Sin(w) + fit.B * Math.Cos(w))));
        }
        return max;
    }

    [Fact]
    public void SameRateSeam_ResamplerRunsOn_OneContinuousSine()
    {
        // One sine cut into two 44.1 kHz tracks, rendered at 48 kHz: the seam must not show.
        var provider = new GaplessSpliceProvider(48000, 1);
        const int split = 22087, total = 44100;
        var a = new GaplessTrackSegment(44100, 1, source: 0);
        var b = new GaplessTrackSegment(44100, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(Sine(44100, 0, split)));
        a.MarkEndOfStream();
        Assert.True(b.Write(Sine(44100, (double)split / 44100, total - split)));
        b.MarkEndOfStream();

        var y = Render(provider, 48000);
        var fit = Fit(y, 48000, 1000, 20000);
        var seam = (int)(split * 48000L / 44100);
        var atSeam = MaxResidual(y, 48000, fit, seam - 300, seam + 300);
        var after = MaxResidual(y, 48000, fit, seam + 300, 46000);
        _out.WriteLine($"amp {Math.Sqrt(fit.A * fit.A + fit.B * fit.B):F4}, residual at seam {atSeam:E2}, after {after:E2}");
        Assert.True(atSeam < 2e-3, $"seam residual {atSeam}");
        Assert.True(after < 2e-3, $"timeline moved: residual after the seam {after}");
    }

    // Across a rate change the incoming track's samples cannot sit on the outgoing track's
    // output grid: the timeline holds to within half an output frame (a 1 kHz sine of 0.5 is
    // off by at most 2π·1000·0.5·0.5/48000 = 0.033 after the seam), and the two band-limited
    // edges meet at the seam frame.
    private const double HalfFrameResidual = 0.035;

    [Fact]
    public void RateChangeSeam_EndPlayedOut_NextStartsOnTheFollowingFrame()
    {
        // 44.1 kHz track, then a 48 kHz one continuing the same sine: the outgoing stage plays
        // out its end, the incoming track starts on the frame after it.
        var provider = new GaplessSpliceProvider(48000, 1);
        const int split = 22087;
        var a = new GaplessTrackSegment(44100, 1, source: 0);
        var b = new GaplessTrackSegment(48000, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(Sine(44100, 0, split)));
        a.MarkEndOfStream();
        Assert.True(b.Write(Sine(48000, (double)split / 44100, 24000)));
        b.MarkEndOfStream();

        var y = Render(provider, 46000);
        var fit = Fit(y, 48000, 1000, 20000);
        var seam = (int)Math.Round(split * 48000.0 / 44100);
        var before = MaxResidual(y, 48000, fit, 1000, seam - 48);
        var atSeam = MaxResidual(y, 48000, fit, seam - 300, seam + 300);
        var after = MaxResidual(y, 48000, fit, seam + 300, 44000);
        _out.WriteLine($"residual before {before:E2}, at seam {atSeam:E2}, after {after:E2}");
        Assert.True(before < 2e-3, $"the outgoing track's end is not intact: {before}");
        Assert.True(atSeam < 0.05, $"seam residual {atSeam}");
        Assert.True(after < HalfFrameResidual, $"timeline moved: residual after the seam {after}");
    }

    [Fact]
    public void RateChangeSeam_FromNativeRateIntoResampled_TimelineHolds()
    {
        var provider = new GaplessSpliceProvider(48000, 1);
        const int split = 24011;
        var a = new GaplessTrackSegment(48000, 1, source: 0);
        var b = new GaplessTrackSegment(44100, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(Sine(48000, 0, split)));
        a.MarkEndOfStream();
        Assert.True(b.Write(Sine(44100, (double)split / 48000, 22050)));
        b.MarkEndOfStream();

        var y = Render(provider, 44000);
        var fit = Fit(y, 48000, 1000, 20000);
        var atSeam = MaxResidual(y, 48000, fit, split - 300, split + 300);
        var after = MaxResidual(y, 48000, fit, split + 300, 43000);
        _out.WriteLine($"residual at seam {atSeam:E2}, after {after:E2}");
        Assert.True(atSeam < 0.05, $"seam residual {atSeam}");
        Assert.True(after < HalfFrameResidual, $"timeline moved: residual after the seam {after}");
    }

    [Fact]
    public void NaturalEnd_NothingAfter_PlaysTheResampledEndOut()
    {
        var provider = new GaplessSpliceProvider(48000, 1);
        const int frames = 22050;
        var a = new GaplessTrackSegment(44100, 1, source: 0);
        provider.Enqueue(a);
        Assert.True(a.Write(Sine(44100, 0, frames)));
        a.MarkEndOfStream();

        var y = Render(provider, 30000);
        var end = (int)Math.Round(frames * 48000.0 / 44100); // 24000
        var lastAudible = Array.FindLastIndex(y, v => Math.Abs(v) > 1e-3);
        var fit = Fit(y, 48000, 1000, 20000);
        // Up to 1 ms before the end the sine is intact; the last millisecond is there too
        // (only the filter's own edge, a few frames, is shaped by the silence after it).
        var beforeEnd = MaxResidual(y, 48000, fit, 20000, end - 48);
        double lastMsRms = Math.Sqrt(y.Skip(end - 48).Take(40).Average(v => (double)v * v));
        _out.WriteLine($"end {end}, last audible {lastAudible}, residual {beforeEnd:E2}, last-ms rms {lastMsRms:F3} (sine {Amp / Math.Sqrt(2):F3})");
        Assert.True(beforeEnd < 2e-3, $"residual before the end {beforeEnd}");
        Assert.True(lastMsRms > 0.8 * Amp / Math.Sqrt(2), $"last ms rms {lastMsRms}");
        Assert.InRange(lastAudible, end - 4, end + 1);
        Assert.Null(provider.ActiveSegment);
    }

    [Fact]
    public void SeekCut_StartsAFreshStage_NoStaleAudioFromBeforeTheCut()
    {
        var provider = new GaplessSpliceProvider(48000, 1);
        var a = new GaplessTrackSegment(44100, 1, source: 0);
        provider.Enqueue(a);
        Assert.True(a.Write(Enumerable.Repeat((short)16384, 4410).ToArray()));
        Render(provider, 2400);
        a.Flush(30_000);
        Assert.True(a.Write(Enumerable.Repeat((short)-16384, 8820).ToArray()));
        var y = Render(provider, 4800);
        var firstAudio = Array.FindIndex(y, v => Math.Abs(v) > 1e-3);
        Assert.True(firstAudio >= 0);
        var stale = y.Skip(firstAudio).Max();
        _out.WriteLine($"first audio at {firstAudio}, max after the cut {stale:F4}");
        Assert.True(stale < 0.01f, $"pre-cut audio after the cut: {stale}");
        Assert.True(y[^1] < -0.45f);
    }

    [Fact]
    public void FreshStage_FirstInputFrameIsTheFirstOutputFrame()
    {
        // An impulse at input frame 441 (10 ms at 44.1 kHz) must come out at 480 (10 ms at 48 kHz).
        var provider = new GaplessSpliceProvider(48000, 1);
        var a = new GaplessTrackSegment(44100, 1, source: 0);
        provider.Enqueue(a);
        var block = new short[4410];
        block[441] = 30000;
        Assert.True(a.Write(block));
        a.MarkEndOfStream();
        var y = Render(provider, 4800);
        var peak = Array.IndexOf(y, y.Max());
        _out.WriteLine($"peak at {peak}, latency {EngineResampler.LatencyFrames(44100, 48000):F3}");
        Assert.InRange(peak, 479, 481);
    }

    [Fact]
    public void TryReformat_BeforeTheFirstWrite_TakesTheNewRate_AfterItRefuses()
    {
        var seg = new GaplessTrackSegment(48000, 2, source: null);
        Assert.True(seg.TryReformat(96000));
        Assert.Equal(96000, seg.SampleRate);
        Assert.True(seg.Write(new short[960 * 2]));
        Assert.False(seg.TryReformat(44100));
        Assert.Equal(96000, seg.SampleRate);
        Assert.True(seg.TryReformat(96000));
    }

    [Fact]
    public void SegmentReformattedAfterItsStageWasBuilt_IsReadAtItsNewRate()
    {
        // The stage for a fresh segment may be built before LibVLC reports the real rate.
        var provider = new GaplessSpliceProvider(48000, 1);
        var a = new GaplessTrackSegment(48000, 1, source: 0);
        provider.Enqueue(a);
        Render(provider, 480); // builds the stage at 48 kHz, nothing to play yet
        Assert.True(a.TryReformat(44100));
        Assert.True(a.Write(Sine(44100, 0, 22050)));
        a.MarkEndOfStream();
        var y = Render(provider, 24000);
        var fit = Fit(y, 48000, 2000, 20000);
        _out.WriteLine($"amp {Math.Sqrt(fit.A * fit.A + fit.B * fit.B):F4}");
        Assert.InRange(Math.Sqrt(fit.A * fit.A + fit.B * fit.B), 0.49, 0.51);
        Assert.True(MaxResidual(y, 48000, fit, 2000, 23000) < 2e-3);
    }

    [Fact]
    public void Multichannel_EachChannelKeepsItsOwnSignal()
    {
        // A 5.1 engine: channel c carries a sine at (c+1) kHz; after 44.1 → 48 kHz each channel
        // must still carry its own, at full level, with no bleed of a neighbour's.
        const int ch = 6, inRate = 44100, outRate = 48000, frames = 22050;
        var provider = new GaplessSpliceProvider(outRate, ch);
        var a = new GaplessTrackSegment(inRate, ch, source: 0);
        provider.Enqueue(a);
        var block = new short[frames * ch];
        for (var n = 0; n < frames; n++)
            for (var c = 0; c < ch; c++)
                block[n * ch + c] = (short)Math.Round(0.4 * 32767 * Math.Sin(2 * Math.PI * 1000 * (c + 1) * n / inRate));
        Assert.True(a.Write(block));
        a.MarkEndOfStream();
        var y = new float[20000 * ch];
        for (var at = 0; at < y.Length; at += 480 * ch)
            provider.Read(y, at, Math.Min(480 * ch, y.Length - at));
        for (var c = 0; c < ch; c++)
        {
            double err = 0;
            for (var k = 2000; k < 19000; k++)
            {
                var want = 0.4 * Math.Sin(2 * Math.PI * 1000 * (c + 1) * k / outRate);
                err = Math.Max(err, Math.Abs(y[k * ch + c] - want));
            }
            _out.WriteLine($"channel {c}: max error {err:E2}");
            Assert.True(err < 3e-3, $"channel {c} error {err}");
        }
    }

    // CPU of the engine's conversion, stereo, per second of audio (render-thread work).
    [Theory]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void Converter_CpuPerSecondOfAudio(int inRate)
    {
        const int outRate = 48000, seconds = 10;
        var provider = new GaplessSpliceProvider(outRate, 2);
        var a = new GaplessTrackSegment(inRate, 2, source: 0, capacitySeconds: seconds + 2);
        provider.Enqueue(a);
        var block = new short[inRate * 2];
        for (var n = 0; n < inRate; n++)
            block[2 * n] = block[2 * n + 1] = (short)(12000 * Math.Sin(2 * Math.PI * 441 * n / inRate));
        for (var s = 0; s < seconds; s++) Assert.True(a.Write(block));
        a.MarkEndOfStream();
        var buf = new float[480 * 2];
        for (var i = 0; i < 100; i++) provider.Read(buf, 0, buf.Length); // warm-up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reads = (seconds - 1) * 100;
        for (var i = 0; i < reads; i++) provider.Read(buf, 0, buf.Length);
        var msPerSecond = sw.Elapsed.TotalMilliseconds / (reads / 100.0);
        _out.WriteLine($"{inRate} -> {outRate} stereo: {msPerSecond:F2} ms per second of audio ({msPerSecond / 10:F2}% of one core)");
        Assert.True(msPerSecond < 100, $"{msPerSecond} ms per second");
    }

    // Converter quality on this PC's WDL settings: pass band and alias rejection.
    [Theory]
    [InlineData(44100, 48000, 1000, true)]
    [InlineData(44100, 48000, 18000, true)]
    [InlineData(96000, 48000, 1000, true)]
    [InlineData(96000, 48000, 30000, false)]   // above the output's Nyquist: must vanish
    [InlineData(192000, 48000, 70000, false)]
    [InlineData(88200, 48000, 40000, false)]
    public void Converter_PassBandAndStopBand(int inRate, int outRate, double freq, bool passes)
    {
        var provider = new GaplessSpliceProvider(outRate, 1);
        var a = new GaplessTrackSegment(inRate, 1, source: 0, capacitySeconds: 4);
        provider.Enqueue(a);
        var n = inRate;
        var block = Enumerable.Range(0, n).Select(i => (short)Math.Round(0.5 * 32767 * Math.Sin(2 * Math.PI * freq * i / inRate))).ToArray();
        Assert.True(a.Write(block));
        a.MarkEndOfStream();
        var y = Render(provider, outRate);
        var from = outRate / 10;
        var to = outRate * 8 / 10;
        double rms = Math.Sqrt(y.Skip(from).Take(to - from).Average(v => (double)v * v));
        var db = 20 * Math.Log10(rms / (0.5 / Math.Sqrt(2)) + 1e-12);
        _out.WriteLine($"{inRate}->{outRate} {freq} Hz: {db:F2} dB");
        if (passes) Assert.InRange(db, -0.1, 0.1);
        else Assert.True(db < -75, $"alias at {db:F1} dB");
    }
}
