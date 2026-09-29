using Noctis.Services.Waveform;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #93 waveform seek bar: the streaming reduction from decoded samples to
/// normalized buckets, and the bucket → display-bar resampling the control draws from.
/// </summary>
public class WaveformReductionTests
{
    private static float[] Constant(int count, float value)
    {
        var a = new float[count];
        Array.Fill(a, value);
        return a;
    }

    [Fact]
    public void NothingFed_BuildsNothing()
    {
        Assert.Null(new WaveformAccumulator().Build());
    }

    [Fact]
    public void DigitalSilence_IsAFlatZeroLine_NotADivideByZero()
    {
        var acc = new WaveformAccumulator();
        acc.Add(new float[256 * 100]);
        var data = acc.Build(64)!;
        Assert.Equal(64, data.BucketCount);
        Assert.All(data.Peaks.ToArray(), b => Assert.Equal(0, b));
        Assert.All(data.Rms.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ConstantLevel_NormalizesEveryBucketToFullScale()
    {
        var acc = new WaveformAccumulator();
        acc.Add(Constant(256 * 400, -0.25f)); // sign must not matter
        var data = acc.Build(100)!;
        Assert.Equal(100, data.BucketCount);
        Assert.All(data.Peaks.ToArray(), b => Assert.Equal(255, b));
        Assert.All(data.Rms.ToArray(), b => Assert.Equal(255, b));
    }

    [Fact]
    public void QuietThenLoud_LandsInTheRightHalf_AtTheRightRatio()
    {
        var acc = new WaveformAccumulator();
        acc.Add(Constant(256 * 500, 0.1f));
        acc.Add(Constant(256 * 500, 0.4f));
        var data = acc.Build(10)!;

        for (var b = 0; b < 5; b++)
        {
            Assert.InRange(data.Peaks[b], 63, 65);  // 0.1 / 0.4 of 255 = 63.75
            Assert.InRange(data.Rms[b], 63, 65);
        }
        for (var b = 5; b < 10; b++)
        {
            Assert.Equal(255, data.Peaks[b]);
            Assert.Equal(255, data.Rms[b]);
        }
    }

    [Fact]
    public void ShortClip_GetsOneBucketPerBlock_NotMoreBucketsThanAudio()
    {
        var acc = new WaveformAccumulator();
        acc.Add(Constant(256 * 7 + 10, 0.5f)); // 7 full blocks + a partial tail
        var data = acc.Build(1024)!;
        Assert.Equal(8, data.BucketCount);
    }

    [Fact]
    public void SingleSpike_KeepsItsTimePosition_AcrossCompactions()
    {
        // Far more than MaxBlocks × InitialBlockSize samples, so the block table
        // compacts (pairwise merge, block size doubles) at least twice.
        var total = WaveformAccumulator.MaxBlocks * WaveformAccumulator.InitialBlockSize * 3;
        var spikeAt = (int)(total * 0.7);
        var acc = new WaveformAccumulator();
        var chunk = new float[65536];
        for (var start = 0; start < total; start += chunk.Length)
        {
            Array.Fill(chunk, 0.01f);
            if (spikeAt >= start && spikeAt < start + chunk.Length) chunk[spikeAt - start] = 1f;
            acc.Add(chunk.AsSpan(0, Math.Min(chunk.Length, total - start)));
        }

        Assert.True(acc.BlockSize >= WaveformAccumulator.InitialBlockSize * 4, $"block size {acc.BlockSize}");
        var data = acc.Build(1000)!;
        Assert.Equal(1000, data.BucketCount);
        var loudest = 0;
        for (var b = 1; b < data.BucketCount; b++)
            if (data.Peaks[b] > data.Peaks[loudest]) loudest = b;
        Assert.InRange(loudest, 699, 701);
        Assert.Equal(255, data.Peaks[loudest]);
        Assert.InRange(data.Peaks[100], 2, 3); // 0.01 of full scale
    }

    [Fact]
    public void NonFiniteSamples_AreIgnored()
    {
        var acc = new WaveformAccumulator();
        var samples = Constant(256 * 4, 0.5f);
        samples[10] = float.NaN;
        samples[300] = float.PositiveInfinity;
        acc.Add(samples);
        var data = acc.Build(4)!;
        Assert.All(data.Peaks.ToArray(), b => Assert.Equal(255, b));
    }

    [Fact]
    public void ResampleLevels_AveragesTheBucketsUnderEachBar()
    {
        var peaks = new byte[8];
        var rms = new byte[8];
        rms[4] = 255; rms[5] = 255; // bar 2, both buckets loud
        rms[7] = 255;               // bar 3, one of two
        var data = new WaveformData(peaks, rms);

        var bars = new float[4]; // two buckets per bar
        data.ResampleLevels(bars);
        Assert.Equal(new[] { 0f, 0f, 1f, 0.5f }, bars);
    }

    [Fact]
    public void ResampleLevels_MoreBarsThanBuckets_RepeatsTheNearestBucket()
    {
        var data = new WaveformData(new byte[] { 0, 0 }, new byte[] { 0, 255 });
        var bars = new float[4];
        data.ResampleLevels(bars);
        Assert.Equal(new[] { 0f, 0f, 1f, 1f }, bars);
    }

    [Fact]
    public void LevelAt_BlendsRmsWithAShareOfThePeak()
    {
        var data = new WaveformData(new byte[] { 255, 0 }, new byte[] { 0, 128 });
        Assert.Equal(WaveformData.PeakShare, data.LevelAt(0), 3); // sparse transient stays visible
        Assert.Equal(128 / 255f, data.LevelAt(1), 3);              // RMS carries the body
    }
}
