using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class SilentWavFileTests
{
    [Fact]
    public void Write_ProducesWellFormedAllZeroPcmWav()
    {
        const int seconds = 1, rate = 48000, channels = 2;
        var dataSize = seconds * rate * channels * 2;

        using var ms = new MemoryStream();
        SilentWavFile.Write(ms, seconds, rate, channels);
        var b = ms.ToArray();

        Assert.Equal(SilentWavFile.HeaderBytes + dataSize, b.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal(36 + dataSize, BitConverter.ToInt32(b, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(b, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(b, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(b, 16));            // fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(b, 20));             // PCM
        Assert.Equal(channels, BitConverter.ToInt16(b, 22));
        Assert.Equal(rate, BitConverter.ToInt32(b, 24));
        Assert.Equal(rate * channels * 2, BitConverter.ToInt32(b, 28)); // byte rate
        Assert.Equal(channels * 2, BitConverter.ToInt16(b, 32));  // block align
        Assert.Equal(16, BitConverter.ToInt16(b, 34));            // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(b, 36, 4));
        Assert.Equal(dataSize, BitConverter.ToInt32(b, 40));
        for (var i = SilentWavFile.HeaderBytes; i < b.Length; i++)
            Assert.Equal(0, b[i]);
    }

    [Fact]
    public void Write_ZeroSeconds_IsAHeaderWithUnknownDataLength()
    {
        using var ms = new MemoryStream();
        SilentWavFile.Write(ms, seconds: 0, sampleRate: 48000, channels: 2);
        var b = ms.ToArray();

        Assert.Equal(SilentWavFile.HeaderBytes, b.Length);
        Assert.Equal(36, BitConverter.ToInt32(b, 4));  // RIFF size of a header-only file
        Assert.Equal("data", Encoding.ASCII.GetString(b, 36, 4));
        Assert.Equal(0, BitConverter.ToInt32(b, 40));  // 0 = length unknown, read to EOF
    }
}
