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
    public void EnsureCached_CreatesFileAndIsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisKeepAliveTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path1 = SilentWavFile.EnsureCached(dir);
            Assert.True(File.Exists(path1));
            var len1 = new FileInfo(path1).Length;
            Assert.Equal(SilentWavFile.HeaderBytes + 48000 * 2 * 2, len1);

            var path2 = SilentWavFile.EnsureCached(dir); // reuse, no throw
            Assert.Equal(path1, path2);
            Assert.Equal(len1, new FileInfo(path2).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
