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

    // A rebuilt output (device switch) opens a new audio session at that session's own
    // level; the gate holds it silent until the user volume is on it (GaplessSink.Rebuilt).

    [Fact]
    public void Hold_SilencesFromTheFirstRead_WithoutARampDown()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.5f), rampMs: 8);
        Render(gate, 256); // open, playing
        Assert.Equal(1f, gate.CurrentGain);

        gate.Hold(60_000);
        var held = Render(gate, 1024);

        Assert.All(held, s => Assert.Equal(0f, s));
        Assert.Equal(0f, gate.CurrentGain);
    }

    [Fact]
    public void ReleaseHold_RampsBackUp_ThenPassesThroughAgain()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.5f), rampMs: 8);
        gate.Hold(60_000);
        Render(gate, 1024);

        Assert.True(gate.ReleaseHold());
        var ramp = Render(gate, Rate * 8 / 1000);
        var left = Enumerable.Range(0, ramp.Length / Channels).Select(f => ramp[f * Channels]).ToArray();
        Assert.True(left[0] < 0.5f, "first frame after the hold starts from silence");
        for (var f = 1; f < left.Length; f++)
            Assert.True(left[f] >= left[f - 1] - 1e-6f, $"gain fell at frame {f}");

        var after = Render(gate, 256);
        Assert.All(after, s => Assert.Equal(0.5f, s));
        Assert.Equal(1f, gate.CurrentGain);
    }

    [Fact]
    public void ReleaseHold_WhileMuted_StaysSilent()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.5f), rampMs: 8) { IsMuted = true };
        gate.Hold(60_000);
        Render(gate, 256);
        gate.ReleaseHold();

        Assert.All(Render(gate, 1024), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Hold_ExpiresOnItsOwn_WhenNeverReleased()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.5f), rampMs: 8);
        gate.Hold(1);
        System.Threading.Thread.Sleep(50); // past the deadline at TickCount64's ~16 ms resolution

        Render(gate, 1024); // ramp up (1024 frames > 384-frame ramp)
        Assert.All(Render(gate, 256), s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void ReleaseHold_WithoutAHold_ReturnsFalse()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.5f));

        Assert.False(gate.ReleaseHold());
        Assert.All(Render(gate, 64), s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void Read_PassesTheSourceCountThrough()
    {
        var gate = new MuteGateProvider(new ConstantSource(0.2f));
        var buf = new float[300];

        Assert.Equal(300, gate.Read(buf, 0, 300));
    }
}
