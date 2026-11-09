using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Mono Audio, Balance and the playback fade gain in the output's sample stage.
/// </summary>
public class ChannelMixProviderTests
{
    private const int Rate = 48000;
    private static readonly int RampFrames = Rate * ChannelMixProvider.RampMs / 1000;

    /// <summary>Repeats a fixed frame; never allocates in Read.</summary>
    private sealed class ConstantSource : ISampleProvider
    {
        private readonly float[] _frame;
        public ConstantSource(params float[] frame) { _frame = frame; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, frame.Length); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = _frame[i % _frame.Length];
            return count;
        }
    }

    /// <summary>Deterministic pseudo-random stereo noise in [-1, 1].</summary>
    private sealed class NoiseSource : ISampleProvider
    {
        private uint _state = 12345;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _state = _state * 1664525u + 1013904223u;
                buffer[offset + i] = (_state >> 8) / (float)(1 << 23) - 1f;
            }
            return count;
        }
    }

    private static float[] ReadFrames(ISampleProvider p, int frames)
    {
        var ch = p.WaveFormat.Channels;
        var buf = new float[frames * ch];
        Assert.Equal(buf.Length, p.Read(buf, 0, buf.Length));
        return buf;
    }

    /// <summary>The last frame after the ramp has finished.</summary>
    private static float[] Settled(ISampleProvider p)
    {
        var buf = ReadFrames(p, RampFrames * 2);
        return buf[^p.WaveFormat.Channels..];
    }

    [Fact]
    public void Mono_AveragesLeftAndRight_OntoBothChannels()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: true, balance: 0);
        var p = new ChannelMixProvider(new ConstantSource(0.8f, -0.4f), s);

        var f = Settled(p);
        Assert.Equal(0.2f, f[0], 6);
        Assert.Equal(0.2f, f[1], 6);
    }

    [Fact]
    public void Mono_NeverExceedsTheLouderInput_NoClipping()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: true, balance: 0);
        var src = new NoiseSource();
        var reference = new NoiseSource();
        var p = new ChannelMixProvider(src, s);

        var outBuf = ReadFrames(p, Rate);
        var inBuf = ReadFrames(reference, Rate);
        for (var i = 0; i < outBuf.Length; i += 2)
        {
            var peak = Math.Max(Math.Abs(inBuf[i]), Math.Abs(inBuf[i + 1]));
            Assert.True(Math.Abs(outBuf[i]) <= peak + 1e-6f);
            Assert.True(Math.Abs(outBuf[i + 1]) <= peak + 1e-6f);
            Assert.True(Math.Abs(outBuf[i]) <= 1f && Math.Abs(outBuf[i + 1]) <= 1f);
        }

        // Full-scale same-sign input stays at full scale (average, not sum).
        var full = new ChannelMixProvider(new ConstantSource(1f, 1f), s);
        var ff = Settled(full);
        Assert.Equal(1f, ff[0]);
        Assert.Equal(1f, ff[1]);
        // Opposite phase cancels.
        var anti = Settled(new ChannelMixProvider(new ConstantSource(1f, -1f), s));
        Assert.Equal(0f, anti[0]);
        Assert.Equal(0f, anti[1]);
    }

    [Theory]
    [InlineData(0.0, 1f, 1f)]
    [InlineData(0.4, 0.6f, 1f)]
    [InlineData(-0.4, 1f, 0.6f)]
    [InlineData(1.0, 0f, 1f)]
    [InlineData(-1.0, 1f, 0f)]
    [InlineData(5.0, 0f, 1f)] // clamped
    public void Balance_LinearLaw_DominantSideAtUnity(double balance, float left, float right)
    {
        var (l, r) = ChannelMixProvider.BalanceGains(balance);
        Assert.Equal(left, l, 6);
        Assert.Equal(right, r, 6);
    }

    [Fact]
    public void Balance_AppliesPerChannel_AfterMono()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: false, balance: 0.5);
        var f = Settled(new ChannelMixProvider(new ConstantSource(0.6f, 0.6f), s));
        Assert.Equal(0.3f, f[0], 6);
        Assert.Equal(0.6f, f[1], 6);

        s.SetChannelMix(mono: true, balance: -0.25);
        var g = Settled(new ChannelMixProvider(new ConstantSource(0.8f, 0.0f), s));
        Assert.Equal(0.4f, g[0], 6);
        Assert.Equal(0.4f * 0.75f, g[1], 6);
    }

    [Fact]
    public void Toggling_RampsOver20ms_WithoutADiscontinuity()
    {
        var s = new ChannelMixSettings();
        var p = new ChannelMixProvider(new ConstantSource(0.8f, -0.4f), s);
        ReadFrames(p, 256); // steady stereo
        Assert.True(p.IsBypassed);

        s.SetChannelMix(mono: true, balance: 0);
        var buf = ReadFrames(p, RampFrames * 2);

        // Left goes 0.8 → 0.2 in RampFrames even steps: no step bigger than one ramp increment.
        var maxStep = (0.8f - 0.2f) / RampFrames * 1.01f;
        var prev = 0.8f;
        for (var i = 0; i < buf.Length; i += 2)
        {
            Assert.True(Math.Abs(buf[i] - prev) <= maxStep + 1e-6f, $"jump at frame {i / 2}: {prev} -> {buf[i]}");
            prev = buf[i];
        }
        Assert.Equal(0.5f, buf[(RampFrames / 2 - 1) * 2], 2); // half way at half the ramp
        Assert.Equal(0.2f, buf[(RampFrames - 1) * 2], 6);     // there at 20 ms
        Assert.Equal(0.2f, buf[^2], 6);

        // And back, mid-ramp reversal included: still continuous.
        s.SetChannelMix(mono: false, balance: 0);
        var back = ReadFrames(p, RampFrames / 4);
        s.SetChannelMix(mono: true, balance: 0);
        var again = ReadFrames(p, RampFrames * 2);
        prev = 0.2f;
        foreach (var chunk in new[] { back, again })
            for (var i = 0; i < chunk.Length; i += 2)
            {
                Assert.True(Math.Abs(chunk[i] - prev) <= maxStep + 1e-6f);
                prev = chunk[i];
            }
    }

    [Fact]
    public void Identity_IsBitExactPassThrough()
    {
        var s = new ChannelMixSettings();
        var p = new ChannelMixProvider(new NoiseSource(), s);
        var outBuf = ReadFrames(p, 4096);
        var inBuf = ReadFrames(new NoiseSource(), 4096);
        Assert.True(p.IsBypassed);
        Assert.Equal(inBuf, outBuf);
    }

    [Fact]
    public void ExclusiveBypass_IgnoresMonoAndBalance_BitExact()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: true, balance: -0.7);
        var p = new ChannelMixProvider(new NoiseSource(), s, allowChannelMix: false);
        var outBuf = ReadFrames(p, 4096);
        var inBuf = ReadFrames(new NoiseSource(), 4096);
        Assert.True(p.IsBypassed);
        Assert.Equal(inBuf, outBuf);
    }

    [Fact]
    public void ExclusiveBypass_StillAppliesThePlaybackFade()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: true, balance: 1);
        s.SetFadeGain(0.5, ChannelMixProvider.RampMs);
        var f = Settled(new ChannelMixProvider(new ConstantSource(0.8f, -0.4f), s, allowChannelMix: false));
        Assert.Equal(0.4f, f[0], 6);
        Assert.Equal(-0.2f, f[1], 6);
    }

    [Fact]
    public void FadeGain_RampsLinearlyOverItsLength_AndBackToBypass()
    {
        var s = new ChannelMixSettings();
        var p = new ChannelMixProvider(new ConstantSource(1f, 1f), s);
        ReadFrames(p, 64);

        s.SetFadeGain(0, 250);
        var frames = Rate / 4; // 250 ms
        var buf = ReadFrames(p, frames + 100);
        Assert.Equal(0.5f, buf[(frames / 2 - 1) * 2], 3);
        Assert.Equal(0f, buf[(frames - 1) * 2]);
        Assert.Equal(0f, buf[^1]);
        Assert.False(p.IsBypassed);

        // Restore: a short ramp, then a bit-exact pass-through again.
        s.SetFadeGain(1, 0); // clamped up to the 20 ms minimum
        var up = ReadFrames(p, RampFrames);
        Assert.True(up[0] < 0.01f);
        Assert.Equal(1f, up[^1]);
        ReadFrames(p, 8);
        Assert.True(p.IsBypassed);
    }

    [Fact]
    public void Mono_GetsTheFadeOnly()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: true, balance: 1);
        var p = new ChannelMixProvider(new ConstantSource(0.5f), s);
        Assert.Equal(new[] { 0.5f }, Settled(p));
    }

    // Multichannel output (GitHub #200), Windows' 5.1 order FL FR FC LFE BL BR: mono and balance
    // act on each left/right pair, centre and LFE stay as they are.
    [Fact]
    public void FiveOne_MonoAndBalance_ActOnEachSpeakerPair_CentreAndLfeUntouched()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: true, balance: 1);
        var p = new ChannelMixProvider(new ConstantSource(0.5f, 0.25f, -0.5f, 0.1f, 0.2f, 0.3f), s);
        var f = Settled(p);
        var expected = new[] { 0f, 0.375f, -0.5f, 0.1f, 0f, 0.25f };
        for (var c = 0; c < 6; c++) Assert.Equal(expected[c], f[c], 5);
    }

    [Fact]
    public void SevenOne_FadeReachesEveryChannel()
    {
        var s = new ChannelMixSettings();
        s.SetChannelMix(mono: false, balance: -0.5);
        s.SetFadeGain(0.5, ChannelMixProvider.RampMs);
        var p = new ChannelMixProvider(new ConstantSource(0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f), s);
        var f = Settled(p);
        // FL FR FC LFE BL BR SL SR: right side ×0.5 from the balance, everything ×0.5 from the fade.
        var expected = new[] { 0.4f, 0.2f, 0.4f, 0.4f, 0.4f, 0.2f, 0.4f, 0.2f };
        for (var c = 0; c < 8; c++) Assert.Equal(expected[c], f[c], 5);
    }

    [Fact]
    public void Read_DoesNotAllocate_WhileRampingOrMixing()
    {
        var s = new ChannelMixSettings();
        var p = new ChannelMixProvider(new ConstantSource(0.8f, -0.4f), s);
        var buf = new float[960];
        p.Read(buf, 0, buf.Length); // JIT warm-up
        s.SetChannelMix(true, 0.3);
        s.SetFadeGain(0.5, 1000);
        p.Read(buf, 0, buf.Length);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++) p.Read(buf, 0, buf.Length);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
