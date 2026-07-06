using System.IO;
using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DsdiffReaderTests
{
    [Fact]
    public void Reads_uncompressed_dsdiff_audio_properties()
    {
        // DSD64 stereo, two seconds of uncompressed DSD data.
        const int sampleRate = 2822400;
        const int channels = 2;
        long twoSecondsOfData = (long)sampleRate * channels / 8 * 2;

        var bytes = new DffBuilder()
            .Prop(sampleRate, channels)
            .DsdData(twoSecondsOfData)
            .Build();

        var info = DsdiffReader.TryRead(new MemoryStream(bytes));

        Assert.NotNull(info);
        Assert.Equal(sampleRate, info!.SampleRate);
        Assert.Equal(channels, info.Channels);
        Assert.Equal(2.0, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Reads_edited_master_title_and_artist()
    {
        var bytes = new DffBuilder()
            .Prop(2822400, 2)
            .Diin(title: "Night Drive", artist: "Tycho")
            .DsdData(705600)
            .Build();

        var info = DsdiffReader.TryRead(new MemoryStream(bytes));

        Assert.NotNull(info);
        Assert.Equal("Night Drive", info!.Title);
        Assert.Equal("Tycho", info.Artist);
    }

    [Fact]
    public void Reads_dst_compressed_duration_from_frame_count()
    {
        // 150 frames at 75 frames/second = two seconds.
        var bytes = new DffBuilder()
            .Prop(2822400, 2)
            .DstData(numFrames: 150, frameRate: 75)
            .Build();

        var info = DsdiffReader.TryRead(new MemoryStream(bytes));

        Assert.NotNull(info);
        Assert.Equal(2.0, info!.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Returns_null_for_non_dsdiff_stream()
    {
        var bytes = Encoding.ASCII.GetBytes("ID3 not a dsdiff file by any measure");
        Assert.Null(DsdiffReader.TryRead(new MemoryStream(bytes)));
    }

    /// <summary>Builds minimal but spec-shaped DSDIFF byte streams for the tests.</summary>
    private sealed class DffBuilder
    {
        private readonly MemoryStream _body = new();

        public DffBuilder Prop(int sampleRate, int channels)
        {
            var prop = new MemoryStream();
            WriteId(prop, "SND ");

            var fs = new MemoryStream();
            WriteU32(fs, (uint)sampleRate);
            WriteChunk(prop, "FS  ", fs.ToArray());

            var chnl = new MemoryStream();
            WriteU16(chnl, (ushort)channels);
            for (int i = 0; i < channels; i++)
                WriteId(chnl, i == 0 ? "SLFT" : "SRGT");
            WriteChunk(prop, "CHNL", chnl.ToArray());

            WriteChunk(_body, "PROP", prop.ToArray());
            return this;
        }

        public DffBuilder Diin(string title, string artist)
        {
            var diin = new MemoryStream();

            var diar = new MemoryStream();
            var artistBytes = Encoding.ASCII.GetBytes(artist);
            WriteU32(diar, (uint)artistBytes.Length);
            diar.Write(artistBytes);
            WriteChunk(diin, "DIAR", diar.ToArray());

            var diti = new MemoryStream();
            var titleBytes = Encoding.ASCII.GetBytes(title);
            WriteU32(diti, (uint)titleBytes.Length);
            diti.Write(titleBytes);
            WriteChunk(diin, "DITI", diti.ToArray());

            WriteChunk(_body, "DIIN", diin.ToArray());
            return this;
        }

        // Header only; the (claimed) sample payload is not materialized.
        public DffBuilder DsdData(long dataBytes)
        {
            WriteId(_body, "DSD ");
            WriteU64(_body, (ulong)dataBytes);
            return this;
        }

        public DffBuilder DstData(int numFrames, int frameRate)
        {
            var frte = new MemoryStream();
            WriteU32(frte, (uint)numFrames);
            WriteU16(frte, (ushort)frameRate);

            var dst = new MemoryStream();
            WriteChunk(dst, "FRTE", frte.ToArray());
            WriteChunk(_body, "DST ", dst.ToArray());
            return this;
        }

        public byte[] Build()
        {
            var frm = new MemoryStream();
            WriteId(frm, "FRM8");
            var body = _body.ToArray();
            WriteU64(frm, (ulong)(4 + body.Length)); // formType + body
            WriteId(frm, "DSD ");
            frm.Write(body);
            return frm.ToArray();
        }

        private static void WriteChunk(Stream s, string id, byte[] data)
        {
            WriteId(s, id);
            WriteU64(s, (ulong)data.Length);
            s.Write(data);
            if ((data.Length & 1) == 1) s.WriteByte(0); // pad to even boundary
        }

        private static void WriteId(Stream s, string id) => s.Write(Encoding.ASCII.GetBytes(id), 0, 4);
        private static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }
        private static void WriteU64(Stream s, ulong v)
        {
            for (int i = 7; i >= 0; i--) s.WriteByte((byte)(v >> (i * 8)));
        }
    }
}
