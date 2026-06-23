using Noctis.Services.AudioAnalysis;
using Xunit;

namespace Noctis.Tests.AudioAnalysis;

public class KeyDetectorTests
{
    private const int SampleRate = 22050;

    // Sum of sine tones (equal-tempered) for the given MIDI notes, held for the duration.
    private static float[] Chord(int[] midi, double seconds)
    {
        int n = (int)(SampleRate * seconds);
        var buf = new float[n];
        foreach (int m in midi)
        {
            double freq = 440.0 * Math.Pow(2, (m - 69) / 12.0);
            for (int i = 0; i < n; i++) buf[i] += (float)(0.2 * Math.Sin(2 * Math.PI * freq * i / SampleRate));
        }
        return buf;
    }

    [Fact]
    public void DetectsCMajorTriad()
    {
        // C4 E4 G4 = 60 64 67
        var (key, conf) = KeyDetector.Detect(Chord(new[] { 60, 64, 67 }, 6.0), SampleRate);
        Assert.Equal("C major", key);
        Assert.True(conf > 0);
    }

    [Fact]
    public void DetectsAMinorTriad()
    {
        // A3 C4 E4 = 57 60 64
        var (key, _) = KeyDetector.Detect(Chord(new[] { 57, 60, 64 }, 6.0), SampleRate);
        Assert.Equal("A minor", key);
    }
}
