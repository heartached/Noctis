using Noctis.Services.AudioAnalysis;
using Xunit;

namespace Noctis.Tests.AudioAnalysis;

public class BpmDetectorTests
{
    private const int SampleRate = 22050;

    // Builds a mono click track: a short percussive burst every (60/bpm) seconds.
    private static float[] ClickTrack(int bpm, double seconds)
    {
        int n = (int)(SampleRate * seconds);
        var buf = new float[n];
        int period = (int)(SampleRate * 60.0 / bpm);
        for (int i = 0; i < n; i += period)
            for (int k = 0; k < 400 && i + k < n; k++)
                buf[i + k] = (float)(Math.Exp(-k / 60.0) * Math.Sin(2 * Math.PI * 1000 * k / SampleRate));
        return buf;
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(140)]
    public void DetectsClickTrackTempoWithinTolerance(int bpm)
    {
        var (detected, confidence) = BpmDetector.Detect(ClickTrack(bpm, 12.0), SampleRate);
        Assert.InRange(detected, bpm - 2, bpm + 2);
        Assert.True(confidence > 0.0);
    }

    [Fact]
    public void SilenceReturnsZero()
    {
        var (detected, _) = BpmDetector.Detect(new float[SampleRate * 4], SampleRate);
        Assert.Equal(0, detected);
    }
}
