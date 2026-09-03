using System;
using System.Linq;
using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The gapless engine's post-buffer mute (Discord "Mute button unresponsive on Windows").
/// Unmuted it must be a bit-exact pass-through; muted it must reach silence within one
/// short ramp and stay there; every transition must be click-free (monotonic, bounded).
/// </summary>
public class MuteGateProviderTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    /// <summary>Constant-value stereo source so gain is directly observable.</summary>
    private sealed class ConstantSource : ISampleProvider
    {
        private readonly float _value;
        public ConstantSource(float value) => _value = value;
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = _value;
            return count;
        }
    }

    private static float[] Render(MuteGateProvider gate, int frames)
    {
        var buf = new float[frames * Channels];
        var read = gate.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, read);
        return buf;
    }

    [Fact]
    public void Unmuted_IsABitExactPassThrough()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.123456f), rampMs: 8);

        var out1 = Render(gate, 1024);

        Assert.All(out1, s => Assert.Equal(0.123456f, s));
        Assert.Equal(1f, gate.CurrentGain);
    }

    [Fact]
    public void Muting_RampsDownWithinTheRamp_ThenHoldsSilence()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.5f), rampMs: 8) { IsMuted = true };
        var rampFrames = Rate * 8 / 1000; // 384

        var ramp = Render(gate, rampFrames);
        // Left channel per frame: strictly non-increasing, never below 0.
        var left = Enumerable.Range(0, rampFrames).Select(f => ramp[f * Channels]).ToArray();
        for (var f = 1; f < left.Length; f++)
            Assert.True(left[f] <= left[f - 1] + 1e-6f, $"gain rose at frame {f}");
        Assert.True(left[0] < 0.5f, "first frame already attenuated");
        Assert.All(left, v => Assert.InRange(v, 0f, 0.5f));
        // Both channels move together.
        for (var f = 0; f < rampFrames; f++)
            Assert.Equal(ramp[f * Channels], ramp[f * Channels + 1]);

        var after = Render(gate, 512);
        Assert.All(after, s => Assert.Equal(0f, s));
        Assert.Equal(0f, gate.CurrentGain);
    }

    [Fact]
    public void Unmuting_RampsBackUp_ThenPassesThroughAgain()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.5f), rampMs: 8) { IsMuted = true };
        Render(gate, 1024); // fully muted
        Assert.Equal(0f, gate.CurrentGain);

        gate.IsMuted = false;
        var ramp = Render(gate, Rate * 8 / 1000);
        var left = Enumerable.Range(0, ramp.Length / Channels).Select(f => ramp[f * Channels]).ToArray();
        for (var f = 1; f < left.Length; f++)
            Assert.True(left[f] >= left[f - 1] - 1e-6f, $"gain fell at frame {f}");
        Assert.All(left, v => Assert.InRange(v, 0f, 0.5f));

        var after = Render(gate, 256);
        Assert.All(after, s => Assert.Equal(0.5f, s));
        Assert.Equal(1f, gate.CurrentGain);
    }

    [Fact]
    public void TogglingMidRamp_StaysBounded()
    {
        var gate = new MuteGateProvider(new ConstantSource(1f), rampMs: 8) { IsMuted = true };
        Render(gate, 100);          // part-way down
        gate.IsMuted = false;
        Render(gate, 50);           // part-way back up
        gate.IsMuted = true;
        var tail = Render(gate, 2000);

        Assert.All(tail, s => Assert.InRange(s, 0f, 1f));
        Assert.Equal(0f, tail[^1]);
    }

    [Fact]
    public void Read_PassesTheSourceCountThrough()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.2f));
        var buf = new float[300];

        Assert.Equal(300, gate.Read(buf, 0, 300));
    }
}
