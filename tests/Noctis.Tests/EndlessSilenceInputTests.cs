using System.Runtime.InteropServices;
using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #70: the Linux keep-alive plays <see cref="EndlessSilenceInput"/> instead of
/// looping a 1 s silence.wav (whose ten repeats a second flushed the PulseAudio stream).
/// These pin the byte stream LibVLC reads: a streaming WAV header, then silence that
/// never ends, from any offset it seeks to.
/// </summary>
public class EndlessSilenceInputTests
{
    private static byte[] ReadThrough(long position, int total, int chunk)
    {
        var result = new byte[total];
        var scratch = new byte[chunk];
        for (var done = 0; done < total;)
        {
            var n = Math.Min(chunk, total - done);
            Assert.Equal(n, EndlessSilenceInput.Produce(position + done, scratch, n));
            Array.Copy(scratch, 0, result, done, n);
            done += n;
        }
        return result;
    }

    [Fact]
    public void Header_IsA48kStereo16BitPcmWav_WithUnknownDataLength()
    {
        var h = EndlessSilenceInput.HeaderBytes();

        Assert.Equal(SilentWavFile.HeaderBytes, h.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(h, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(h, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(h, 20));        // PCM
        Assert.Equal(2, BitConverter.ToInt16(h, 22));        // stereo
        Assert.Equal(48000, BitConverter.ToInt32(h, 24));
        Assert.Equal(16, BitConverter.ToInt16(h, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(h, 36, 4));
        // 0 = "unknown length": libavformat reads to EOF and VLC's wav demuxer ignores it
        // for a stream of unknown size — so neither ever reaches an end.
        Assert.Equal(0, BitConverter.ToInt32(h, 40));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(4096)]
    public void Produce_FromTheStart_IsTheHeaderThenSilence_InAnyChunkSize(int chunk)
    {
        var header = EndlessSilenceInput.HeaderBytes();
        var bytes = ReadThrough(0, 20_000, chunk);

        Assert.Equal(header, bytes[..header.Length]);
        Assert.All(bytes[header.Length..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(20L)]               // mid-header: the header's tail, then silence
    [InlineData(44L)]               // first sample
    [InlineData(1_000_003L)]
    [InlineData(1L << 40)]          // far past any real file: still just silence
    public void Produce_AfterASeek_ReturnsTheBytesAtThatOffset(long position)
    {
        var header = EndlessSilenceInput.HeaderBytes();
        var bytes = ReadThrough(position, 4096, 1000);

        for (var i = 0; i < bytes.Length; i++)
        {
            var at = position + i;
            Assert.Equal(at < header.Length ? header[at] : (byte)0, bytes[i]);
        }
    }

    [Fact]
    public void Produce_ClearsWhateverTheReusedBufferHeld()
    {
        var scratch = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        EndlessSilenceInput.Produce(0, scratch, 256);   // header + zeros
        EndlessSilenceInput.Produce(512, scratch, 256); // silence only

        Assert.All(scratch, b => Assert.Equal(0, b));
    }

    [Fact]
    public void MediaInput_IsSeekable_NeverEnds_AndRestartsFromTheHeaderOnReopen()
    {
        using var input = new EndlessSilenceInput();
        // Seekable on purpose: LibVLC's imem access grants pace control only with a seek
        // callback, and without it the input would read as fast as it can.
        Assert.True(input.CanSeek);

        var header = EndlessSilenceInput.HeaderBytes();
        var buf = Marshal.AllocHGlobal(8192);
        try
        {
            Assert.True(input.Open(out var size));
            Assert.Equal(ulong.MaxValue, size); // unknown length

            var first = new byte[8192];
            Assert.Equal(8192, input.Read(buf, 8192));
            Marshal.Copy(buf, first, 0, 8192);
            Assert.Equal(header, first[..header.Length]);

            for (var i = 0; i < 100; i++)
                Assert.True(input.Read(buf, 8192) > 0); // 0 would be end-of-stream

            Assert.True(input.Seek(10));
            Assert.Equal(8, input.Read(buf, 8));
            var afterSeek = new byte[8];
            Marshal.Copy(buf, afterSeek, 0, 8);
            Assert.Equal(header[10..18], afterSeek);

            // Park/resume closes and reopens the same media: it must start over.
            input.Close();
            Assert.True(input.Open(out _));
            Assert.Equal(header.Length, input.Read(buf, (uint)header.Length));
            var reopened = new byte[header.Length];
            Marshal.Copy(buf, reopened, 0, header.Length);
            Assert.Equal(header, reopened);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
