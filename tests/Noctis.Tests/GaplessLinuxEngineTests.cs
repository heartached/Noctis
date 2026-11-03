using System;
using System.Linq;
using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// GitHub #127: Linux played every track through its own LibVLC output, so each track
// change opened a new PulseAudio stream (harness on a pulse null sink: 20-108 ms of
// silence or 155-199 ms of audio skipped per boundary). The splice engine now runs there
// too, rendering into one pulse stream with the user's volume applied in its own chain.
public class GaplessLinuxEngineTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)] // macOS keeps the per-track LibVLC output
    public void EngineRunsOnWindowsAndLinux(bool isWindows, bool isLinux, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.IsGaplessEnginePlatform(isWindows, isLinux));
    }

    // GitHub #139: without these args Linux VLC resampled 48/96/192 kHz files into the
    // 44.1 kHz engine with "ugly" (harness on a pulse null sink: 48k/24 at 11 dB vs a
    // soxr reference; speex q10 73.5 dB multitone SINAD, the 16-bit floor).
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)] // macOS keeps VLC's default resampler
    public void SpeexResampler_ForcedWhereverTheEngineRuns(bool isWindows, bool isLinux, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.ShouldForceSpeexResampler(isWindows, isLinux));
        Assert.Equal(VlcAudioPlayer.IsGaplessEnginePlatform(isWindows, isLinux),
            VlcAudioPlayer.ShouldForceSpeexResampler(isWindows, isLinux));
    }

    [Fact]
    public void LinuxEngineRate_IsCdRate()
    {
        // LibVLC resamples each input on its own: at the rate of the music itself the
        // boundary carries no resampler restart.
        Assert.Equal((44100, 2), GaplessSink.EngineFormat(GaplessSink.LinuxEngineSampleRate));
    }

    [Fact]
    public void PulseBuffer_TargetsTheOutputLatency_RefilledPerPeriod()
    {
        var attr = PulseSimpleWavePlayer.BufferAttrFor(44100, 8, GaplessSink.LinuxOutputLatencyMs);
        Assert.Equal((uint)(8820 * 8), attr.TLength);
        Assert.Equal((uint)(441 * 8), attr.MinReq);
        Assert.Equal(uint.MaxValue, attr.MaxLength);
        Assert.Equal(uint.MaxValue, attr.PreBuf);
        Assert.Equal(uint.MaxValue, attr.FragSize);
    }

    // GitHub #139 follow-up ("skips a beat"): with ADJUST_LATENCY PipeWire's pulse server
    // keeps (tlength - 2·minreq)/2 + 2·minreq queued. At 100 ms that was 60 ms and 50-60 ms
    // render stalls (the user's GC pauses) cut out on the harness; at 200 ms it is 110 ms.
    [Fact]
    public void PulseBuffer_ServerKeepsMoreThanTheFieldStalls()
    {
        var attr = PulseSimpleWavePlayer.BufferAttrFor(44100, 8, GaplessSink.LinuxOutputLatencyMs);
        var tlengthMs = attr.TLength / 8 * 1000.0 / 44100;
        var minreqMs = attr.MinReq / 8 * 1000.0 / 44100;
        var queuedMs = (tlengthMs - 2 * minreqMs) / 2 + 2 * minreqMs;
        Assert.True(queuedMs >= 100, $"server keeps {queuedMs:F0} ms");
        Assert.True(GaplessSink.LinuxOutputLatencyMs > GaplessSink.OutputLatencyMs);
    }

    [Fact]
    public void StallThreshold_ClearsThePulseQuantum_KeepsWasapiAt25()
    {
        Assert.Equal(25, GaplessSink.StallThresholdMs(GaplessSink.OutputLatencyMs));
        var pulse = GaplessSink.StallThresholdMs(GaplessSink.LinuxOutputLatencyMs);
        // A 2048-frame graph quantum at 44.1 kHz is a write that waits 46 ms every cycle.
        Assert.True(pulse > 2048 * 1000.0 / 44100, $"threshold {pulse} ms");
        Assert.True(pulse < GaplessSink.LinuxOutputLatencyMs / 2);
    }

    private sealed class ConstantProvider(float value, int rate = 1000, int channels = 2) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, value, offset, count);
            return count;
        }
    }

    [Fact]
    public void OutputLevel_AtUnity_IsBitExactPassThrough()
    {
        var level = new OutputLevelProvider(new ConstantProvider(0.123456f));
        var buffer = new float[200];
        level.Read(buffer, 0, 200);
        Assert.All(buffer, s => Assert.Equal(0.123456f, s));
    }

    [Fact]
    public void OutputLevel_SlewsToANewLevel_WithoutAStep()
    {
        // 1 kHz stereo, 15 ms slew: 15 frames per unit of gain.
        var level = new OutputLevelProvider(new ConstantProvider(1f), slewMs: 15);
        level.Level = 0.25f;
        var buffer = new float[100];
        level.Read(buffer, 0, 100);

        var maxStep = 1f / 15 + 1e-6f;
        Assert.True(buffer[0] < 1f && buffer[0] >= 1f - maxStep, $"first frame {buffer[0]}");
        for (var i = 2; i < buffer.Length; i += 2)
        {
            Assert.True(Math.Abs(buffer[i] - buffer[i - 2]) <= maxStep, $"step at {i}");
            Assert.Equal(buffer[i], buffer[i + 1]); // channels move together
        }
        Assert.Equal(0.25f, buffer[^1], 5);
        Assert.Equal(0.25f, level.CurrentGain, 5);

        // Settled: a plain multiply.
        level.Read(buffer, 0, 100);
        Assert.All(buffer, s => Assert.Equal(0.25f, s, 5));
    }

    [Fact]
    public void OutputLevel_ClampsToTheAmplitudeRange()
    {
        var level = new OutputLevelProvider(new ConstantProvider(1f));
        level.Level = 3f;
        Assert.Equal(1f, level.Level);
        level.Level = -1f;
        Assert.Equal(0f, level.Level);
    }
}
