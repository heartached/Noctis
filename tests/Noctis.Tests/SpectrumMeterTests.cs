using System;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The live spectrum behind the audio visualizer: a sine lands in the right log band,
/// silence reads flat, no feed means not live, the read is latency-aligned, and the
/// visualizer's smoothing rises fast and falls slow.
/// </summary>
public class SpectrumMeterTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const int Bands = 48;

    /// <summary>Feeds <paramref name="seconds"/> of stereo audio in 10 ms reads, advancing the fake clock in step.</summary>
    private static (SpectrumMeter meter, Func<double> now) Run(Func<double, float> sampleAt, double seconds, int latencyMs = 0, int rate = Rate)
    {
        double clockMs = 0;
        var meter = new SpectrumMeter(() => clockMs);
        var framesPerRead = rate / 100;
        var buffer = new float[framesPerRead * Channels];
        var totalFrames = (int)(seconds * rate);
        for (var start = 0; start < totalFrames; start += framesPerRead)
        {
            for (var f = 0; f < framesPerRead; f++)
            {
                var s = sampleAt((start + f) / (double)rate);
                buffer[f * Channels] = s;
                buffer[f * Channels + 1] = s;
            }
            meter.Feed(buffer, 0, buffer.Length, Channels, rate, latencyMs);
            clockMs += 10;
        }
        return (meter, () => clockMs);
    }

    private static int BandOf(double hz, int bands = Bands)
    {
        var logMin = Math.Log(SpectrumMeter.MinHz);
        var logSpan = Math.Log(SpectrumMeter.MaxHz) - logMin;
        return (int)Math.Floor((Math.Log(hz) - logMin) / logSpan * bands);
    }

    private static int ArgMax(float[] bands)
    {
        var best = 0;
        for (var i = 1; i < bands.Length; i++) if (bands[i] > bands[best]) best = i;
        return best;
    }

    [Fact]
    public void Sine_PeaksInItsOwnBand_AndReadsNearFullScale()
    {
        const double hz = 1000;
        var (meter, now) = Run(t => (float)(0.9 * Math.Sin(2 * Math.PI * hz * t)), 0.5);

        var bands = new float[Bands];
        Assert.True(meter.TryRead(now(), bands));
        Assert.Equal(BandOf(hz), ArgMax(bands));
        Assert.InRange(bands[BandOf(hz)], 0.85f, 1.0f);
        // Far away from the tone the spectrum is quiet.
        Assert.InRange(bands[BandOf(100)], 0f, 0.15f);
    }

    [Fact]
    public void Silence_ReadsFlat_ButLive()
    {
        var (meter, now) = Run(_ => 0f, 0.3);
        var bands = new float[Bands];
        Assert.True(meter.TryRead(now(), bands));
        Assert.All(bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void NoFeedForLiveWindow_IsNotLive()
    {
        var (meter, now) = Run(t => (float)Math.Sin(2 * Math.PI * 440 * t), 0.3);
        Assert.True(meter.IsLive(now()));
        Assert.False(meter.IsLive(now() + SpectrumMeter.LiveWindowMs + 1));
        var bands = new float[Bands];
        Assert.False(meter.TryRead(now() + SpectrumMeter.LiveWindowMs + 1, bands));
    }

    [Fact]
    public void Read_IsLatencyAligned_ShowsWhatIsBeingHeard()
    {
        // 300 ms of 250 Hz then 300 ms of 4 kHz; the renderer runs 200 ms ahead of the speaker.
        // (250, not 200 Hz: 200 Hz sits exactly on a band edge of the 20 Hz..20 kHz split.)
        var (meter, now) = Run(t => (float)Math.Sin(2 * Math.PI * (t < 0.3 ? 250 : 4000) * t), 0.6, latencyMs: 200);

        var bands = new float[Bands];
        // Right after the last feed the speaker is still 200 ms behind: only 100 ms of the
        // 4 kHz tone has been heard, but the FFT window (~43 ms) ending there is all 4 kHz.
        Assert.True(meter.TryRead(now(), bands));
        Assert.Equal(BandOf(4000), ArgMax(bands));

        // Rewind the clock so the heard point sits inside the 250 Hz stretch.
        var (meter2, now2) = Run(t => (float)Math.Sin(2 * Math.PI * (t < 0.3 ? 250 : 4000) * t), 0.35, latencyMs: 200);
        Assert.True(meter2.TryRead(now2(), bands));
        Assert.Equal(BandOf(250), ArgMax(bands));
    }

    /// <summary>GitHub #196: synthetic FFT whose bin magnitudes all differ (a smooth bass
    /// slope). The bass bands are narrower than one FFT bin; each must still read its own
    /// level (interpolated at its centre), not repeat a neighbour's bin as a flat plateau.</summary>
    [Fact]
    public void MapBands_BassBandsNarrowerThanABin_DoNotRepeatOneBin()
    {
        const int n = SpectrumMeter.WindowFrames;
        var re = new double[n];
        var im = new double[n];
        // Magnitude falls 1 dB per bin from -10 dBFS: every bin distinct, all above the floor.
        for (var k = 1; k < n / 2; k++) re[k] = n / 4.0 * Math.Pow(10, (-10 - Math.Min(k, 40)) / 20.0);

        var bands = new float[64];
        SpectrumMeter.MapBands(re, im, Rate, bands);

        for (var b = 1; b < bands.Length / 2; b++)
            Assert.True(bands[b] != bands[b - 1], $"bands {b - 1} and {b} both read {bands[b]} (plateau)");
    }

    /// <summary>GitHub #196: the bands span the audible range — a 30 Hz tone lands in the
    /// first bands, an 18 kHz tone in the last ones (both were outside 40 Hz..16 kHz).</summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(18000.0)]
    public void Sine_AtTheEdgesOfHearing_IsShown(double hz)
    {
        // -10 dBFS so the levels stay under the full-scale clamp.
        var (meter, now) = Run(t => (float)(0.3 * Math.Sin(2 * Math.PI * hz * t)), 0.5);

        var bands = new float[Bands];
        Assert.True(meter.TryRead(now(), bands));
        Assert.InRange(BandOf(hz), 0, Bands - 1);
        Assert.InRange(bands[BandOf(hz)], 0.6f, 1.0f);
        Assert.InRange(bands[BandOf(1000)], 0f, 0.15f);
    }

    /// <summary>GitHub #205: a tone lights its own band at every output rate, bass included.
    /// The FFT was 2048 frames whatever the rate: 23 Hz bins at 48 kHz smeared every tone under
    /// ~80 Hz across the first dozen bands (a 30 Hz tone peaked in the 20 Hz band), and at 96 and
    /// 192 kHz (47 / 94 Hz bins) everything under the first bin read one flat plateau.</summary>
    [Theory]
    [InlineData(44100, 30.0)]
    [InlineData(48000, 30.0)]
    [InlineData(96000, 30.0)]
    [InlineData(192000, 30.0)]
    [InlineData(44100, 50.0)]
    [InlineData(48000, 50.0)]
    [InlineData(96000, 50.0)]
    [InlineData(192000, 50.0)]
    [InlineData(48000, 80.0)]
    [InlineData(96000, 80.0)]
    [InlineData(192000, 80.0)]
    [InlineData(48000, 12000.0)]
    [InlineData(96000, 12000.0)]
    [InlineData(192000, 12000.0)]
    [InlineData(44100, 18000.0)]
    [InlineData(96000, 18000.0)]
    [InlineData(192000, 18000.0)]
    public void Sine_LightsItsOwnBand_AtEveryRate(int rate, double hz)
    {
        const int bandCount = 64; // the Visualizer page's band count
        var (meter, now) = Run(t => (float)(0.3 * Math.Sin(2 * Math.PI * hz * t)), 0.5, rate: rate);

        var bands = new float[bandCount];
        Assert.True(meter.TryRead(now(), bands));
        var own = BandOf(hz, bandCount);
        Assert.InRange(ArgMax(bands), own - 1, own + 1);
        Assert.InRange(bands[own], 0.6f, 1.0f);
    }

    /// <summary>GitHub #205: bass tones stay apart. A 50 Hz tone must not light the 20 Hz or the
    /// 120 Hz band, so 30, 50 and 80 Hz read as three different shapes at every rate.</summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void BassSine_StaysNarrow_AtEveryRate(int rate)
    {
        const int bandCount = 64;
        var (meter, now) = Run(t => (float)(0.3 * Math.Sin(2 * Math.PI * 50 * t)), 0.5, rate: rate);

        var bands = new float[bandCount];
        Assert.True(meter.TryRead(now(), bands));
        Assert.InRange(bands[BandOf(21, bandCount)], 0f, 0.4f);
        Assert.InRange(bands[BandOf(120, bandCount)], 0f, 0.4f);
    }

    /// <summary>Pink noise (equal energy per octave, roughly music's long-term balance), averaged
    /// over several reads: mean band level per decade (20-200 Hz, 200 Hz-2 kHz, 2-20 kHz).</summary>
    private static double[] PinkDecadeMeans(int rate, bool fullSpectrum)
    {
        const int bandCount = 64;
        double clockMs = 0;
        var meter = new SpectrumMeter(() => clockMs);
        var rnd = new Random(7);
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        var framesPerRead = rate / 100;
        var buffer = new float[framesPerRead];
        var sums = new double[bandCount];
        var reads = 0;
        for (var block = 0; block < 300; block++)
        {
            for (var f = 0; f < framesPerRead; f++)
            {
                // Paul Kellet's pink filter on white noise.
                var w = rnd.NextDouble() * 2 - 1;
                b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759;
                b2 = 0.96900 * b2 + w * 0.1538520; b3 = 0.86650 * b3 + w * 0.3104856;
                b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
                buffer[f] = (float)((b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.05);
                b6 = w * 0.115926;
            }
            meter.Feed(buffer, 0, buffer.Length, 1, rate, 0);
            clockMs += 10;
            if (block >= 50 && block % 5 == 0)
            {
                var bands = new float[bandCount];
                meter.TryRead(clockMs, bands, fullSpectrum);
                for (var i = 0; i < bandCount; i++) sums[i] += bands[i];
                reads++;
            }
        }
        var means = new double[3];
        for (var d = 0; d < 3; d++)
        {
            var from = d * bandCount / 3;
            var to = (d + 1) * bandCount / 3;
            for (var i = from; i < to; i++) means[d] += sums[i] / reads;
            means[d] /= to - from;
        }
        return means;
    }

    /// <summary>GitHub #205: Full spectrum weights the bands like a studio analyser (+1.5 dB per
    /// octave around 1 kHz, see the constant), so pink noise reads level from 20 Hz to 20 kHz. The default look is
    /// unweighted: pink noise leans to the bass there.</summary>
    [Theory]
    [InlineData(48000)]
    [InlineData(96000)]
    public void FullSpectrum_ReadsPinkNoiseLevel(int rate)
    {
        var full = PinkDecadeMeans(rate, fullSpectrum: true);
        Assert.InRange(full[0] - full[2], -0.06, 0.06);
        Assert.InRange(full[1] - full[2], -0.06, 0.06);

        var plain = PinkDecadeMeans(rate, fullSpectrum: false);
        Assert.True(plain[0] - plain[2] > 0.1, $"default look: bass {plain[0]:0.00} vs treble {plain[2]:0.00}");
    }

    /// <summary>GitHub #205: a quiet top octave (a -66 dBFS 16 kHz component, the level of a
    /// typical master's air band) sits under the default look's -60 dB floor; Full spectrum
    /// shows it.</summary>
    [Fact]
    public void FullSpectrum_ShowsAQuietTopOctave()
    {
        const int bandCount = 64;
        var (meter, now) = Run(t => (float)(0.0005 * Math.Sin(2 * Math.PI * 16000 * t)), 0.5);

        var plain = new float[bandCount];
        Assert.True(meter.TryRead(now(), plain));
        Assert.Equal(0f, plain[BandOf(16000, bandCount)]);

        var full = new float[bandCount];
        Assert.True(meter.TryRead(now(), full, fullSpectrum: true));
        Assert.InRange(full[BandOf(16000, bandCount)], 0.2f, 1f);
    }

    [Fact]
    public void Smoothing_RisesFastAndFallsSlow()
    {
        var shown = new float[] { 0f };
        SpectrumVisualizer.Smooth(shown, new[] { 1f }, dtMs: SpectrumVisualizer.AttackMs);
        Assert.InRange(shown[0], 0.6f, 0.7f); // one attack time constant ≈ 63%

        shown[0] = 1f;
        SpectrumVisualizer.Smooth(shown, new[] { 0f }, dtMs: SpectrumVisualizer.AttackMs);
        Assert.InRange(shown[0], 0.8f, 0.9f); // release is much slower than attack
    }

    [Fact]
    public void EqVisualizer_LevelMapsOntoOscillationRange()
    {
        // The row EQ now follows the live spectrum; 0..1 must land inside the same
        // heights the free-running fallback uses, so the hand-off is seamless.
        Assert.Equal(EqVisualizer.HeightForLevel(0), EqVisualizer.HeightForLevel(-1));
        Assert.Equal(EqVisualizer.HeightForLevel(1), EqVisualizer.HeightForLevel(2));
        Assert.True(EqVisualizer.HeightForLevel(1) > EqVisualizer.HeightForLevel(0.5));
        Assert.True(EqVisualizer.HeightForLevel(0.5) > EqVisualizer.HeightForLevel(0));
    }

    [Fact]
    public void EqVisualizer_BeatLiftsEveryBar_AndBarsStillDifferByTone()
    {
        var flat = new float[] { 0.8f, 0.8f, 0.8f, 0.8f, 0.8f }; // always-loud wide bands
        var quiet = new float[5];
        var offBeat = new float[5];
        var onBeat = new float[5];

        EqVisualizer.LiveLevels(0.1, pulse: 0, flat, offBeat);
        EqVisualizer.LiveLevels(0.1, pulse: 1, flat, onBeat);
        // The bounce is what the eye reads: a beat lifts every bar a lot.
        for (var i = 0; i < 5; i++) Assert.True(onBeat[i] - offBeat[i] >= 0.4f, $"bar {i} did not bounce");

        // Uniformly loud bands must NOT pin the bars: equal bands add no tone, so the
        // off-beat level is just rest + sway (well below the top).
        Assert.All(offBeat, l => Assert.InRange(l, 0f, 0.5f));

        // Tone: with a bass-heavy spectrum the left bar sits above the right one.
        var bassy = new float[] { 1f, 0.7f, 0.5f, 0.3f, 0.2f };
        EqVisualizer.LiveLevels(0.1, pulse: 0.3, bassy, quiet);
        Assert.True(quiet[0] > quiet[4]);
    }

    [Theory]
    [InlineData(null, VisualizerStyle.Bars)]
    [InlineData("", VisualizerStyle.Bars)]
    [InlineData("2", VisualizerStyle.Bars)]
    [InlineData("mirror", VisualizerStyle.Mirror)]
    [InlineData("Wave", VisualizerStyle.Wave)]
    [InlineData("Nope", VisualizerStyle.Bars)]
    public void VisualizerStyles_ParseByNameOnly(string? setting, VisualizerStyle expected)
        => Assert.Equal(expected, VisualizerStyles.Parse(setting));
}
