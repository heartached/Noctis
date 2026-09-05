using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class UpmixSampleProviderTests
{
    /// <summary>Stereo float source that repeats a fixed (L, R) frame.</summary>
    private sealed class ConstantStereo : ISampleProvider
    {
        private readonly float _l, _r;
        public ConstantStereo(float l, float r, int rate = 48000) { _l = l; _r = r; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i += 2) { buffer[offset + i] = _l; buffer[offset + i + 1] = _r; }
            return count;
        }
    }

    private static float[] OneFrame(UpmixSampleProvider up, int warmupFrames = 0)
    {
        var buf = new float[up.WaveFormat.Channels * (warmupFrames + 1)];
        var n = up.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, n);
        return buf[(warmupFrames * up.WaveFormat.Channels)..];
    }

    [Fact]
    public void Duplicate_FiveOne_FrontsUnchanged_RearsCopied_CentreMinus3dB()
    {
        var up = new UpmixSampleProvider(new ConstantStereo(0.8f, -0.4f), 6, UpmixMode.Duplicate);
        var frame = OneFrame(up);

        Assert.Equal(0.8f, frame[UpmixSampleProvider.SlotOf(6, "FL")], 4);
        Assert.Equal(-0.4f, frame[UpmixSampleProvider.SlotOf(6, "FR")], 4);
        Assert.Equal(0.5f * 0.4f * 0.70710678f, frame[UpmixSampleProvider.SlotOf(6, "FC")], 4);
        Assert.Equal(0.8f * 0.70710678f, frame[UpmixSampleProvider.SlotOf(6, "BL")], 4);
        Assert.Equal(-0.4f * 0.70710678f, frame[UpmixSampleProvider.SlotOf(6, "BR")], 4);
    }

    [Fact]
    public void Surround_FiveOne_RearsCarryLeftMinusRightAmbience()
    {
        var up = new UpmixSampleProvider(new ConstantStereo(0.6f, 0.2f), 6, UpmixMode.Surround);
        var frame = OneFrame(up);

        var ambience = 0.5f * (0.6f - 0.2f) * 0.70710678f;
        Assert.Equal(ambience, frame[UpmixSampleProvider.SlotOf(6, "BL")], 4);
        Assert.Equal(-ambience, frame[UpmixSampleProvider.SlotOf(6, "BR")], 4);
        Assert.Equal(0.6f, frame[UpmixSampleProvider.SlotOf(6, "FL")], 4);
    }

    [Fact]
    public void Lfe_IsLowPassed_DcPassesAfterSettling()
    {
        // A constant (0 Hz) input must reach the LFE at full mid level once the filter settles.
        var up = new UpmixSampleProvider(new ConstantStereo(0.5f, 0.5f), 6, UpmixMode.Duplicate);
        var frame = OneFrame(up, warmupFrames: 48000);
        Assert.Equal(0.5f, frame[UpmixSampleProvider.SlotOf(6, "LFE")], 2);
    }

    [Fact]
    public void Lfe_RejectsHighFrequencies()
    {
        // 8 kHz mono tone at 48 kHz: 6 samples per cycle, peak after settling must be tiny.
        var rate = 48000;
        var src = new ToneStereo(rate, 8000, 0.9f);
        var up = new UpmixSampleProvider(src, 6, UpmixMode.Duplicate);
        var buf = new float[6 * rate];
        up.Read(buf, 0, buf.Length);
        var lfe = UpmixSampleProvider.SlotOf(6, "LFE");
        float peak = 0;
        for (var f = rate / 2; f < rate; f++) peak = Math.Max(peak, Math.Abs(buf[f * 6 + lfe]));
        Assert.True(peak < 0.01f, $"LFE leaked 8 kHz at {peak}");
    }

    [Fact]
    public void Off_OrTwoChannelDevice_IsAPlainCopy()
    {
        var off = new UpmixSampleProvider(new ConstantStereo(0.3f, 0.7f), 6, UpmixMode.Off);
        var frame = OneFrame(off);
        Assert.Equal(new[] { 0.3f, 0.7f, 0f, 0f, 0f, 0f }, frame);

        var stereoDevice = new UpmixSampleProvider(new ConstantStereo(0.3f, 0.7f), 2, UpmixMode.Surround);
        Assert.Equal(new[] { 0.3f, 0.7f }, OneFrame(stereoDevice));
    }

    [Fact]
    public void SevenOne_LayoutHasSidesAndRears()
    {
        Assert.Equal(6, UpmixSampleProvider.SlotOf(8, "SL"));
        Assert.Equal(7, UpmixSampleProvider.SlotOf(8, "SR"));
        Assert.Equal(4, UpmixSampleProvider.SlotOf(8, "BL"));
        Assert.Equal(3, UpmixSampleProvider.SlotOf(8, "LFE"));
        Assert.Equal(-1, UpmixSampleProvider.SlotOf(4, "FC"));
    }

    [Fact]
    public void Read_ReturnsWholeFrames_AndReportsOutputSampleCount()
    {
        var up = new UpmixSampleProvider(new ConstantStereo(0.1f, 0.1f), 6, UpmixMode.Duplicate);
        var buf = new float[6 * 10 + 3]; // not a multiple of 6
        var n = up.Read(buf, 0, buf.Length);
        Assert.Equal(60, n);
    }

    private sealed class ToneStereo : ISampleProvider
    {
        private readonly double _step;
        private readonly float _amp;
        private long _n;
        public ToneStereo(int rate, double hz, float amp) { _step = 2 * Math.PI * hz / rate; _amp = amp; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i += 2)
            {
                var v = (float)(Math.Sin(_step * _n++) * _amp);
                buffer[offset + i] = v; buffer[offset + i + 1] = v;
            }
            return count;
        }
    }
}
