using Noctis.Helpers;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The live beat pulse behind the flowing-artwork background: onsets from a
/// synthetic kick pattern, decay between them, no re-triggering on sustained tone,
/// latency-aligned reads, and the BPM-grid fallback when no audio is flowing.
/// </summary>
public class BeatMeterTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    /// <summary>Feeds <paramref name="seconds"/> of stereo audio in 10 ms reads, advancing the fake clock in step.</summary>
    private static (BeatMeter meter, Func<double> now) Run(Func<double, float> sampleAt, double seconds, int latencyMs = 0)
    {
        double clockMs = 0;
        var meter = new BeatMeter(() => clockMs);
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

    // 60 Hz kick: 80 ms decaying burst every beat.
    private static Func<double, float> Kicks(double bpm) => t =>
    {
        var period = 60.0 / bpm;
        var inBeat = t % period;
        if (inBeat > 0.08) return 0f;
        return (float)(0.8 * Math.Exp(-inBeat / 0.03) * Math.Sin(2 * Math.PI * 60 * t));
    };

    [Fact]
    public void KickPattern_FiresOneOnsetPerBeat()
    {
        var (meter, _) = Run(Kicks(120), seconds: 4.0);
        // 8 beats in 4 s at 120 BPM; allow the last one to fall on the buffer edge.
        Assert.InRange(meter.OnsetCount, 7, 9);
    }

    [Fact]
    public void SustainedTone_FiresOnceThenHolds()
    {
        var (meter, _) = Run(t => (float)(0.5 * Math.Sin(2 * Math.PI * 55 * t)), seconds: 3.0);
        Assert.InRange(meter.OnsetCount, 1, 2);
    }

    [Fact]
    public void Silence_NeverFires()
    {
        var (meter, now) = Run(_ => 0f, seconds: 2.0);
        Assert.Equal(0, meter.OnsetCount);
        Assert.True(meter.TryRead(now(), out var pulse));
        Assert.Equal(0, pulse, 6);
    }

    [Fact]
    public void Pulse_DecaysBetweenBeats()
    {
        // One kick at t=0 then silence: the pulse read 300 ms later is well below 1.
        var (meter, now) = Run(t => t < 0.08 ? (float)(0.8 * Math.Sin(2 * Math.PI * 60 * t)) : 0f, seconds: 0.5);
        Assert.Equal(1, meter.OnsetCount);
        Assert.True(meter.TryRead(now(), out var pulse));
        Assert.InRange(pulse, 0.0, 0.2);
    }

    [Fact]
    public void Read_IsDelayedByOutputLatency()
    {
        // Kick rendered at t=0 with 100 ms of output latency: at 50 ms nothing is due
        // yet (silence), at 125 ms the kick has reached the ear.
        double clockMs = 0;
        var meter = new BeatMeter(() => clockMs);
        var frames = Rate / 50; // one 20 ms block
        var buffer = new float[frames * Channels];
        for (var f = 0; f < frames; f++)
        {
            var s = (float)(0.8 * Math.Sin(2 * Math.PI * 60 * f / Rate));
            buffer[f * Channels] = s;
            buffer[f * Channels + 1] = s;
        }
        meter.Feed(buffer, 0, buffer.Length, Channels, Rate, latencyMs: 100);
        Assert.Equal(1, meter.OnsetCount);

        Assert.True(meter.TryRead(50, out var early));
        Assert.Equal(0, early, 6);
        Assert.True(meter.TryRead(125, out var onTime));
        Assert.InRange(onTime, 0.9, 1.0);
    }

    [Fact]
    public void NoFeed_IsNotLive_AndFallsBackToBpmGrid()
    {
        var meter = new BeatMeter(() => 10_000);
        Assert.False(meter.TryRead(10_000, out _));

        // 120 BPM = 500 ms period: on the beat the grid pulse is 1, mid-beat it has decayed.
        var onBeat = BeatPulseSource.Evaluate(meter, 10_000, new BeatContext(120, 4000, true));
        var midBeat = BeatPulseSource.Evaluate(meter, 10_000, new BeatContext(120, 4250, true));
        Assert.Equal(1.0, onBeat, 3);
        Assert.InRange(midBeat, 0.1, 0.3);

        Assert.Equal(0, BeatPulseSource.Evaluate(meter, 10_000, new BeatContext(120, 4000, false)));
        Assert.Equal(0, BeatPulseSource.Evaluate(meter, 10_000, new BeatContext(0, 4000, true)));
    }

    [Fact]
    public void LiveMeter_WinsOverBpmGrid()
    {
        var (meter, now) = Run(_ => 0f, seconds: 1.0);
        // Silence is live audio: the grid must NOT take over just because the meter reads 0.
        var pulse = BeatPulseSource.Evaluate(meter, now(), new BeatContext(120, 0, true));
        Assert.Equal(0, pulse, 6);
    }

    [Fact]
    public void Stale_AfterLiveWindow_ReportsNotLive()
    {
        var (meter, now) = Run(Kicks(120), seconds: 1.0);
        Assert.True(meter.IsLive(now()));
        Assert.False(meter.IsLive(now() + BeatMeter.LiveWindowMs + 1));
    }

    [Fact]
    public void TapProvider_PassesAudioThroughUntouched()
    {
        var src = new ConstantProvider(0.25f, Rate, Channels);
        var tap = new BeatTapProvider(src, latencyMs: 100, new BeatMeter(() => 0));
        var buf = new float[960];
        var read = tap.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, read);
        Assert.All(buf, s => Assert.Equal(0.25f, s));
    }

    private sealed class ConstantProvider : NAudio.Wave.ISampleProvider
    {
        private readonly float _value;
        public NAudio.Wave.WaveFormat WaveFormat { get; }
        public ConstantProvider(float value, int rate, int channels)
        {
            _value = value;
            WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = _value;
            return count;
        }
    }
}
