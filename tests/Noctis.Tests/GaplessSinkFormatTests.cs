using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class GaplessSinkFormatTests
{
    // The engine format is chosen once from the startup device and kept for the session,
    // so a mono or telephone-rate startup endpoint (Bluetooth hands-free) must not leak into it.
    [Theory]
    [InlineData(8000, 48000)]
    [InlineData(16000, 48000)]
    [InlineData(32000, 48000)]
    [InlineData(44100, 44100)]
    [InlineData(48000, 48000)]
    [InlineData(96000, 96000)]
    [InlineData(192000, 192000)]
    [InlineData(176400, 176400)]
    // LibVLC's callbacks deliver the wrong amount of audio from 262144 Hz up (AmemRate):
    // a 384 kHz device ran ~3x fast with gaps (Discord, Ardhito 10-08). Halved into range.
    [InlineData(352800, 176400)]
    [InlineData(384000, 192000)]
    [InlineData(768000, 192000)]
    public void EngineFormat_IsStereo_AtLeastCdRate_AndKeepsNormalDeviceRates(int mixRate, int expectedRate)
    {
        var (rate, channels) = GaplessSink.EngineFormat(mixRate);

        Assert.Equal(expectedRate, rate);
        Assert.Equal(2, channels);
    }

    [Theory]
    [InlineData(44100, 44100)]
    [InlineData(48000, 48000)]
    [InlineData(96000, 96000)]
    [InlineData(192000, 192000)]
    [InlineData(262144, 131072)]
    [InlineData(352800, 176400)]
    [InlineData(384000, 192000)]
    [InlineData(705600, 176400)]
    [InlineData(768000, 192000)]
    public void AmemRate_Fit_HalvesIntoRange_KeepingTheRateFamily(int rate, int expected)
    {
        var fitted = AmemRate.Fit(rate);

        Assert.Equal(expected, fitted);
        Assert.True(fitted <= AmemRate.Max);
        Assert.Equal(0, rate % fitted); // an exact integer ratio to what was asked for
    }
}
