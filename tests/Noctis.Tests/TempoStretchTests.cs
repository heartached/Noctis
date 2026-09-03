using System;
using System.Linq;
using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Podcast/Audiobook island (Discord, Luwi, 08-26): playback speed under the
// splice engine is a pitch-preserving time stretch in the render path, so the
// segment keeps counting media frames and position stays truthful.
public class TempoStretchTests
{
    private sealed class SineSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }
        public long Consumed;
        private readonly int _total;
        private readonly double _hz;
        public SineSource(int rate, int channels, double hz, int totalFrames)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            _hz = hz;
            _total = totalFrames;
        }
        public int Read(float[] buffer, int offset, int count)
        {
            var ch = WaveFormat.Channels;
            var frames = Math.Min(count / ch, _total - (int)(Consumed / ch));
            for (var i = 0; i < frames; i++)
            {
                var frame = Consumed / ch + i;
                var v = (float)(0.5 * Math.Sin(2 * Math.PI * _hz * frame / WaveFormat.SampleRate));
                for (var c = 0; c < ch; c++) buffer[offset + i * ch + c] = v;
            }
            Consumed += frames * ch;
            return frames * ch;
        }
    }

    [Fact]
    public void RateOne_IsBitExactPassThrough()
    {
        var src = new SineSource(8000, 2, 220, 8000);
        var reference = new SineSource(8000, 2, 220, 8000);
        var stretch = new TempoStretchProvider(src, () => 1.0);
        var a = new float[4000];
        var b = new float[4000];
        Assert.Equal(4000, stretch.Read(a, 0, 4000));
        reference.Read(b, 0, 4000);
        Assert.Equal(b, a);
        Assert.Equal(4000, src.Consumed);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.5)]
    [InlineData(0.75)]
    public void Rate_ConsumesInputInProportion_AndStaysContinuous(double rate)
    {
        var src = new SineSource(8000, 1, 110, 80000);
        var stretch = new TempoStretchProvider(src, () => rate);
        var outBuf = new float[16000];
        var got = 0;
        while (got < outBuf.Length)
        {
            var n = stretch.Read(outBuf, got, Math.Min(512, outBuf.Length - got));
            Assert.True(n > 0);
            got += n;
        }
        // Input consumed ≈ rate × output, within one block of look-ahead.
        var ratio = src.Consumed / (double)got;
        Assert.InRange(ratio, rate * 0.95, rate * 1.05 + 0.05);
        // Audio is present and never steps (a 110 Hz sine at 8 kHz moves < 0.05 per sample).
        Assert.True(outBuf.Skip(200).Max() > 0.4f);
        for (var i = 201; i < outBuf.Length; i++)
            Assert.True(Math.Abs(outBuf[i] - outBuf[i - 1]) < 0.12f, $"step {Math.Abs(outBuf[i] - outBuf[i - 1]):F3} at {i}");
    }

    [Fact]
    public void Underrun_ReturnsShort_WithoutThrowing()
    {
        var src = new SineSource(8000, 1, 110, 300); // shorter than one analysis block
        var stretch = new TempoStretchProvider(src, () => 1.5);
        var buf = new float[1000];
        var n = stretch.Read(buf, 0, 1000);
        Assert.True(n < 1000);
    }

    [Fact]
    public void RateChange_MidStream_KeepsProducing()
    {
        var rate = 1.0;
        var src = new SineSource(8000, 2, 110, 200000);
        var stretch = new TempoStretchProvider(src, () => rate);
        var buf = new float[2000];
        Assert.Equal(2000, stretch.Read(buf, 0, 2000));
        rate = 1.75;
        Assert.Equal(2000, stretch.Read(buf, 0, 2000));
        rate = 1.0;
        Assert.Equal(2000, stretch.Read(buf, 0, 2000));
        Assert.True(buf.Max() > 0.4f);
    }

    [Fact]
    public void SpliceProvider_PlaybackRate_ScalesSegmentConsumption()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var seg = new GaplessTrackSegment(8000, 1, source: 0, capacitySeconds: 10);
        provider.Enqueue(seg);
        var pcm = Enumerable.Range(0, 40000).Select(i => (short)(16000 * Math.Sin(2 * Math.PI * 110 * i / 8000.0))).ToArray();
        Assert.True(seg.Write(pcm));
        seg.MarkEndOfStream();

        provider.PlaybackRate = 2.0;
        var buf = new float[8000];
        provider.Read(buf, 0, 8000);
        // 8000 output samples at 2× ≈ 16000 media samples ≈ 2000 ms of position.
        Assert.InRange(seg.PositionMs, 1800, 2300);
    }
}
