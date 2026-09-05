using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class PitchShiftProviderTests
{
    private sealed class ArraySource : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;
        public ArraySource(float[] data, int channels, int rate = 48000) { _data = data; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    private static float[] Sine(int frames, double hz, int rate = 48000, int channels = 1)
    {
        var data = new float[frames * channels];
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++)
                data[f * channels + c] = (float)Math.Sin(2 * Math.PI * hz * f / rate);
        return data;
    }

    private static float[] ReadAll(ISampleProvider p, int chunk = 1000)
    {
        var all = new List<float>();
        var buf = new float[chunk];
        int n;
        while ((n = p.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
        return all.ToArray();
    }

    private static int ZeroCrossings(float[] x)
    {
        var z = 0;
        for (var i = 1; i < x.Length; i++)
            if ((x[i - 1] < 0 && x[i] >= 0) || (x[i - 1] >= 0 && x[i] < 0)) z++;
        return z;
    }

    [Fact]
    public void Unity_IsBitExactPassThrough()
    {
        var data = Sine(4800, 440, channels: 2);
        var p = new PitchShiftProvider(new ArraySource(data, 2), () => 1.0);
        var outp = ReadAll(p, 1234);
        Assert.Equal(data, outp);
    }

    [Fact]
    public void OneOctaveUp_DoublesFrequency_AndHalvesLength()
    {
        var data = Sine(48000, 440);
        var p = new PitchShiftProvider(new ArraySource(data, 1), () => 2.0);
        var outp = ReadAll(p, 4096);

        Assert.InRange(outp.Length, 23900, 24000);
        // 440 Hz over 1 s ≈ 880 crossings; doubled pitch in 0.5 s of output ≈ 880 crossings too,
        // i.e. the output frequency is 880 Hz.
        var crossingsPerSecond = ZeroCrossings(outp) * (48000.0 / outp.Length);
        Assert.InRange(crossingsPerSecond, 1700, 1820);
    }

    [Fact]
    public void FifthDown_LowersFrequency_ByTheRatio()
    {
        var ratio = PitchShiftProvider.RatioFromSemitones(-7); // 0.6674
        var data = Sine(48000, 1000);
        var p = new PitchShiftProvider(new ArraySource(data, 1), () => ratio);
        var outp = ReadAll(p, 3000);

        var hz = ZeroCrossings(outp) * (48000.0 / outp.Length) / 2.0;
        Assert.InRange(hz, 1000 * ratio * 0.97, 1000 * ratio * 1.03);
    }

    [Fact]
    public void Stereo_ChannelsStayIndependent()
    {
        var frames = 24000;
        var data = new float[frames * 2];
        for (var f = 0; f < frames; f++) { data[f * 2] = 0.5f; data[f * 2 + 1] = -0.25f; }
        var p = new PitchShiftProvider(new ArraySource(data, 2), () => 1.5);
        var outp = ReadAll(p, 2048);

        Assert.True(outp.Length % 2 == 0);
        for (var f = 2; f < outp.Length / 2 - 2; f++)
        {
            Assert.Equal(0.5f, outp[f * 2], 3);
            Assert.Equal(-0.25f, outp[f * 2 + 1], 3);
        }
    }

    [Fact]
    public void RatioCanChangeBetweenReads_WithoutDroppingSamples()
    {
        var data = Sine(48000, 200);
        var ratio = 1.0;
        var p = new PitchShiftProvider(new ArraySource(data, 1), () => ratio);
        var first = new float[8000];
        Assert.Equal(8000, p.Read(first, 0, 8000));
        ratio = 1.2;
        var second = new float[8000];
        Assert.Equal(8000, p.Read(second, 0, 8000));
        ratio = 1.0;
        var rest = ReadAll(p, 8000);
        // 8000 @1.0 + 8000 @1.2 (=9600 input) + remainder @1.0 (48000-17600) ≈ 30400
        Assert.InRange(first.Length + second.Length + rest.Length, 46300, 46420);
        Assert.All(rest, v => Assert.InRange(v, -1.0001f, 1.0001f));
    }

    [Fact]
    public void RatioFromSemitones_ClampsAndMaps()
    {
        Assert.Equal(1.0, PitchShiftProvider.RatioFromSemitones(0), 9);
        Assert.Equal(2.0, PitchShiftProvider.RatioFromSemitones(12), 9);
        Assert.Equal(0.5, PitchShiftProvider.RatioFromSemitones(-12), 9);
        Assert.Equal(2.0, PitchShiftProvider.RatioFromSemitones(40), 9);
        Assert.Equal(1.0, PitchShiftProvider.RatioFromSemitones(double.NaN), 9);
    }
}
