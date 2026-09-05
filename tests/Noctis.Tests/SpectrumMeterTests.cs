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
    private static (SpectrumMeter meter, Func<double> now) Run(Func<double, float> sampleAt, double seconds, int latencyMs = 0)
    {
        double clockMs = 0;
        var meter = new SpectrumMeter(() => clockMs);
        var framesPerRead = Rate / 100;
        var buffer = new float[framesPerRead * Channels];
        var totalFrames = (int)(seconds * Rate);
        for (var start = 0; start < totalFrames; start += framesPerRead)
        {
            for (var f = 0; f < framesPerRead; f++)
            {
                var s = sampleAt((start + f) / (double)Rate);
                buffer[f * Channels] = s;
                buffer[f * Channels + 1] = s;
            }
            meter.Feed(buffer, 0, buffer.Length, Channels, Rate, latencyMs);
            clockMs += 10;
        }
        return (meter, () => clockMs);
    }

    private static int BandOf(double hz)
    {
        var logMin = Math.Log(SpectrumMeter.MinHz);
        var logSpan = Math.Log(SpectrumMeter.MaxHz) - logMin;
        return (int)Math.Floor((Math.Log(hz) - logMin) / logSpan * Bands);
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
        // 300 ms of 200 Hz then 300 ms of 4 kHz; the renderer runs 200 ms ahead of the speaker.
        var (meter, now) = Run(t => (float)Math.Sin(2 * Math.PI * (t < 0.3 ? 200 : 4000) * t), 0.6, latencyMs: 200);

        var bands = new float[Bands];
        // Right after the last feed the speaker is still 200 ms behind: only 100 ms of the
        // 4 kHz tone has been heard, but the FFT window (~43 ms) ending there is all 4 kHz.
        Assert.True(meter.TryRead(now(), bands));
        Assert.Equal(BandOf(4000), ArgMax(bands));

        // Rewind the clock so the heard point sits inside the 200 Hz stretch.
        var (meter2, now2) = Run(t => (float)Math.Sin(2 * Math.PI * (t < 0.3 ? 200 : 4000) * t), 0.35, latencyMs: 200);
        Assert.True(meter2.TryRead(now2(), bands));
        Assert.Equal(BandOf(200), ArgMax(bands));
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
