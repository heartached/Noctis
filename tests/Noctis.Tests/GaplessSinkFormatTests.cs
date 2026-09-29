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
    [InlineData(768000, 384000)]
    public void EngineFormat_IsStereo_AtLeastCdRate_AndKeepsNormalDeviceRates(int mixRate, int expectedRate)
    {
        var (rate, channels) = GaplessSink.EngineFormat(mixRate);

        Assert.Equal(expectedRate, rate);
        Assert.Equal(2, channels);
    }
}
