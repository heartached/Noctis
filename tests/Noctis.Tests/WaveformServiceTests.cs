using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class WaveformServiceTests
{
    [Fact]
    public void BuildPeaks_NormalizesLoudestBucketToOne()
    {
        // Two halves: quiet then loud.
        var samples = new float[1000];
        for (int i = 0; i < 500; i++) samples[i] = 0.25f;
        for (int i = 500; i < 1000; i++) samples[i] = 0.5f;

        var peaks = WaveformService.BuildPeaks(samples, 2);

        Assert.Equal(2, peaks.Length);
        Assert.Equal(1f, peaks[1], 3);
        Assert.Equal(0.5f, peaks[0], 3);
    }

    [Fact]
    public void BuildPeaks_UsesAbsoluteValues()
    {
        var samples = new float[] { -0.8f, 0.1f, 0.2f, -0.1f };
        var peaks = WaveformService.BuildPeaks(samples, 2);

        Assert.Equal(1f, peaks[0], 3);      // |-0.8| dominates
        Assert.Equal(0.25f, peaks[1], 3);   // 0.2 / 0.8
    }

    [Fact]
    public void BuildPeaks_LiftsSilentBucketsToVisibleFloor()
    {
        var samples = new float[100];
        samples[0] = 1f; // one loud sample, rest silence

        var peaks = WaveformService.BuildPeaks(samples, 4);

        Assert.Equal(1f, peaks[0], 3);
        for (int i = 1; i < 4; i++)
            Assert.True(peaks[i] >= 0.04f);
    }

    [Fact]
    public void BuildPeaks_EmptyInput_ReturnsZeros()
    {
        var peaks = WaveformService.BuildPeaks(ReadOnlySpan<float>.Empty, 4);
        Assert.All(peaks, p => Assert.Equal(0f, p));
    }
}
