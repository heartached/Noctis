using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// AAC (MP4) and Opus seams under the gapless engine (harness 11-19): LibVLC never decodes
/// the priming packet these containers mark with a negative timestamp, so the file is served
/// without its trims and the engine cuts the priming and padding itself.
/// </summary>
public class ContainerGaplessTests
{
    // The first page of an ffmpeg/libopus file (pre-skip 312), as written.
    private static readonly byte[] OpusPage0 =
    [
        0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x88, 0x89,
        0x84, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xB7, 0xC9, 0x8C, 0x8F, 0x01, 0x13, 0x4F, 0x70, 0x75, 0x73,
        0x48, 0x65, 0x61, 0x64, 0x01, 0x02, 0x38, 0x01, 0x80, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    [Fact]
    public void OggCrc_MatchesTheChecksumAnEncoderWrote()
    {
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(OpusPage0.AsSpan(22)),
            ContainerGaplessInfo.OggCrc(OpusPage0, crcOffset: 22));
    }

    [Fact]
    public void OggOpus_PreSkipIsTheLeadTrim_AndThePatchZeroesItWithAValidChecksum()
    {
        var file = OpusPage0.Concat(new byte[200]).ToArray();
        Assert.True(ContainerGaplessInfo.TryParseOggOpus(new MemoryStream(file), out var info));
        Assert.Equal(312, info!.LeadSamples);
        Assert.Equal(0, info.TailSamples);
        Assert.Equal(2, info.Channels);
        Assert.Equal(312, info.LeadFrames(48000));
        Assert.Equal(287, info.LeadFrames(44100)); // 312 * 44100 / 48000 = 286.65

        var patched = (byte[])file.Clone();
        ContainerTrimInput.Overlay(info.Patches, 0, patched);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(28 + 10))); // OpusHead at 28, pre-skip at +10
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(22)),
            ContainerGaplessInfo.OggCrc(patched.AsSpan(0, OpusPage0.Length), crcOffset: 22));
        Assert.True(patched.AsSpan(OpusPage0.Length).SequenceEqual(file.AsSpan(OpusPage0.Length)));
    }

    [Fact]
    public void OggOpus_RejectsAVorbisStreamAndACorruptPage()
    {
        var vorbis = (byte[])OpusPage0.Clone();
        Encoding.ASCII.GetBytes("VorbisHe").CopyTo(vorbis, 28);
        Assert.False(ContainerGaplessInfo.TryParseOggOpus(new MemoryStream(vorbis), out _));

        var corrupt = (byte[])OpusPage0.Clone();
        corrupt[40] ^= 0xFF;
        Assert.False(ContainerGaplessInfo.TryParseOggOpus(new MemoryStream(corrupt), out _));
    }

    [Fact]
    public void Overlay_LaysPatchesOverAnyReadWindow()
    {
        var patches = new List<(long, byte[])> { (10, new byte[] { 1, 2, 3, 4 }) };
        var a = new byte[8];
        ContainerTrimInput.Overlay(patches, 8, a);       // covers file bytes 8..15
        Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 4, 0, 0 }, a);
        var b = new byte[2];
        ContainerTrimInput.Overlay(patches, 12, b);      // starts inside the patch
        Assert.Equal(new byte[] { 3, 4 }, b);
        var c = new byte[4];
        ContainerTrimInput.Overlay(patches, 20, c);      // after it
        Assert.Equal(new byte[4], c);
    }

    [Fact]
    public void Mp4_EditListGivesPrimingAndPadding_AndThePatchRenamesEdts()
    {
        // ffmpeg's native AAC encoder: 1024 priming, 12 s at 44.1 kHz = 529200 samples in
        // 518 packets, the last stored with its valid 816 of 1024 samples.
        var file = BuildMp4(edit: (12000, 1024), stts: [(517, 1024), (1, 816)], smpb: null);
        Assert.True(ContainerGaplessInfo.TryParseMp4(new MemoryStream(file), out var info));
        Assert.Equal(1024, info!.LeadSamples);
        Assert.Equal(208, info.TailSamples);
        Assert.Equal(1115, info.LeadFrames(48000)); // 1024 * 48000 / 44100 = 1114.6
        Assert.Equal(226, info.TailFrames(48000));  // 208 * 48000 / 44100 = 226.4

        var patched = (byte[])file.Clone();
        ContainerTrimInput.Overlay(info.Patches, 0, patched);
        var at = IndexOf(file, "edts");
        Assert.Equal("free", Encoding.ASCII.GetString(patched, at, 4));
        Assert.Equal(1, Diff(file, patched).Count(d => d) / 4); // only those 4 bytes changed
    }

    [Fact]
    public void Mp4_ITunSmpbWithoutEditList_GivesTheTrims_AndHidesTheTagFromFfmpeg()
    {
        var file = BuildMp4(edit: null, stts: [(517, 1024), (1, 816)],
            smpb: " 00000000 00000400 000000D0 0000000000081330 00000000 00000000");
        Assert.True(ContainerGaplessInfo.TryParseMp4(new MemoryStream(file), out var info));
        Assert.Equal(1024, info!.LeadSamples);
        Assert.Equal(208, info.TailSamples);
        var patched = (byte[])file.Clone();
        ContainerTrimInput.Overlay(info.Patches, 0, patched);
        Assert.Equal(-1, IndexOf(patched, "iTunSMPB"));
        Assert.True(IndexOf(patched, "iTunSMPb") > 0);
        Assert.Equal(1, Diff(file, patched).Count(d => d));
    }

    [Fact]
    public void Mp4_EditListAndITunSmpb_TheEditListWins_AndBothAreHidden()
    {
        // Apple's encoders write both.
        var file = BuildMp4(edit: (12000, 1024), stts: [(517, 1024), (1, 816)],
            smpb: " 00000000 00000400 000000D0 0000000000081330 00000000 00000000");
        Assert.True(ContainerGaplessInfo.TryParseMp4(new MemoryStream(file), out var info));
        Assert.Equal(1024, info!.LeadSamples);
        Assert.Equal(208, info.TailSamples);
        var patched = (byte[])file.Clone();
        ContainerTrimInput.Overlay(info.Patches, 0, patched);
        Assert.Equal(-1, IndexOf(patched, "edts"));
        Assert.Equal(-1, IndexOf(patched, "iTunSMPB"));
        Assert.Equal(5, Diff(file, patched).Count(d => d));
    }

    [Fact]
    public void Mp4_WithoutGaplessInformation_OrWithVideo_IsLeftAlone()
    {
        Assert.False(ContainerGaplessInfo.TryParseMp4(
            new MemoryStream(BuildMp4(edit: null, stts: [(518, 1024)], smpb: null)), out _));
        Assert.False(ContainerGaplessInfo.TryParseMp4(
            new MemoryStream(BuildMp4(edit: (12000, 1024), stts: [(518, 1024)], smpb: null, extraVideo: true)), out _));
        Assert.False(ContainerGaplessInfo.TryParseMp4(
            new MemoryStream(BuildMp4(edit: (12000, 1024), stts: [(518, 1024)], smpb: null, format: "alac")), out _));
    }

    [Theory]
    [InlineData(1000, ":sub-delay=-7")]
    [InlineData(1050, ":sub-delay=-8")]
    [InlineData(5000, ":sub-delay=-47")]
    [InlineData(300, null)]
    [InlineData(0, null)]
    public void CallbackReadAhead_MatchesAFilesCaching(int cachingMs, string? expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.CallbackReadAheadOption(cachingMs));
    }

    [Fact]
    public void LeadTrim_DropsThePrimingUncounted_AndAgainAfterASeekFlush()
    {
        var seg = new GaplessTrackSegment(1000, 1, source: null, capacitySeconds: 2);
        seg.LeadTrimFrames = 3;
        seg.Flush(0); // the input-open flush, before any audio: the trim stays armed
        Assert.True(seg.Write(new short[] { 1, 2, 3, 4, 5 }));
        var buf = new float[8];
        Assert.Equal(2, seg.Read(buf, 0, 8));
        Assert.Equal(4 / 32768f, buf[0]);
        Assert.Equal(2, seg.PositionMs); // 2 frames at 1 kHz played; the priming is not track time

        seg.Flush(500); // a seek to 500 ms: the input's timeline runs 3 frames ahead again
        Assert.True(seg.Write(new short[] { 7, 8, 9, 10 }));
        Assert.Equal(1, seg.Read(buf, 0, 8));
        Assert.Equal(10 / 32768f, buf[0]);
        Assert.Equal(501, seg.PositionMs);
    }

    [Fact]
    public void OggVorbis_RaisesTheFirstPageGranuleByWhatFfmpegCountsForThePrimingPacket_AndTrimsThePadding()
    {
        var (file, pageC) = BuildVorbis(out var total);
        Assert.True(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(file), out var info));
        Assert.Equal("vorbis", info!.Kind);
        Assert.Equal(44100, info.Timescale);
        Assert.Equal(2, info.Channels);
        Assert.Equal(0, info.LeadSamples); // every decoded sample is the track's
        Assert.Equal(80, info.TailSamples);
        Assert.Equal(87, info.TailFrames(48000)); // 80 * 48000 / 44100 = 87.07

        var patched = (byte[])file.Clone();
        ContainerTrimInput.Overlay(info.Patches, 0, patched);
        // FFmpeg dates the first packet granule - (128 + 704): raised by 128 it is 0.
        Assert.Equal(704 + 128, BinaryPrimitives.ReadInt64LittleEndian(patched.AsSpan(pageC.Offset + 6)));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(pageC.Offset + 22)),
            ContainerGaplessInfo.OggCrc(patched.AsSpan(pageC.Offset, pageC.Length), crcOffset: 22));
        Assert.InRange(Diff(file, patched).Count(d => d), 1, 12); // granule + checksum only
        Assert.True(patched.AsSpan(pageC.Offset + pageC.Length).SequenceEqual(file.AsSpan(pageC.Offset + pageC.Length)));
        Assert.Equal(total - 80, BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(LastPageOffset(file) + 6)));
    }

    [Fact]
    public void OggVorbis_TailFromTheLastPagesOfALongFile_IsTheSame()
    {
        var (file, _) = BuildVorbis(out _);
        // A window that starts inside the middle pages: found by checksum, counted from there.
        Assert.True(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(file), out var info, tailWindowBytes: 1500));
        Assert.Equal(80, info!.TailSamples);
        // Only the last page in the window: nothing to count from, so no end trim.
        Assert.True(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(file), out info,
            tailWindowBytes: file.Length - LastPageOffset(file)));
        Assert.Equal(0, info!.TailSamples);
        Assert.Equal(2, info.Patches.Count); // the head patch is still there
    }

    [Fact]
    public void OggVorbis_AnOpusStream_AndACutFile_AreTellApart()
    {
        var opus = OpusPage0.Concat(new byte[200]).ToArray();
        Assert.False(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(opus), out _));
        var (vorbis, _) = BuildVorbis(out _);
        Assert.False(ContainerGaplessInfo.TryParseOggOpus(new MemoryStream(vorbis), out _));

        // Cut before its last page: no end-of-stream granule, only the head patch.
        var cut = vorbis.AsSpan(0, LastPageOffset(vorbis)).ToArray();
        Assert.True(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(cut), out var info));
        Assert.Equal(0, info!.TailSamples);
        Assert.Equal(2, info.Patches.Count);
    }

    // Every audio page's granule in a patched file, in order.
    private static long[] AudioGranules(byte[] file, IReadOnlyList<(long Offset, byte[] Bytes)> patches)
    {
        var patched = (byte[])file.Clone();
        ContainerTrimInput.Overlay(patches, 0, patched);
        var list = new List<long>();
        for (var pos = 0; pos < patched.Length;)
        {
            int segs = patched[pos + 26], len = 27 + segs;
            for (var i = 0; i < segs; i++) len += patched[pos + 27 + i];
            Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(pos + 22)),
                ContainerGaplessInfo.OggCrc(patched.AsSpan(pos, len), crcOffset: 22));
            var g = BinaryPrimitives.ReadInt64LittleEndian(patched.AsSpan(pos + 6));
            if (pos > 0 && (patched[pos + 5] & 0x02) == 0 && g != 0) list.Add(g);
            pos += len;
        }
        return list.ToArray();
    }

    [Fact]
    public void OggVorbis_StartTrimmedStream_IsServedFromZero_AndTheEngineDropsTheTrim()
    {
        // The first page's granule is 104 short of what its packets decode: the first 104
        // samples are not the track. LibVLC dropped every packet FFmpeg dated below zero and
        // primed on the next (harness 11-19: 2531 samples gone, not 1500). Served with every
        // granule 104 higher (the first page 128 more on top), all is decoded and the engine
        // drops the 104.
        var (file, _) = BuildVorbis(out var total, shift: -104);
        Assert.True(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(file), out var info));
        Assert.Equal(104, info!.LeadSamples);
        Assert.Equal(80, info.TailSamples);
        Assert.Equal(new long[] { 704 + 128, 704 + 10240, 704 + 20480, total - 80 }, AudioGranules(file, info.Patches));
    }

    [Fact]
    public void OggVorbis_StreamCutFromTheMiddle_IsServedFromZero()
    {
        // Granules 50000 ahead of what the packets decode: a stream copied out of a longer
        // one. LibVLC lost its first 803 samples (harness 11-19, -18.2 ms); re-dated to start
        // at 0 every sample plays, nothing to drop.
        var (file, _) = BuildVorbis(out var total, shift: 50000);
        Assert.True(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(file), out var info));
        Assert.Equal(0, info!.LeadSamples);
        Assert.Equal(80, info.TailSamples);
        Assert.Equal(new long[] { 704 + 128, 704 + 10240, 704 + 20480, total - 80 }, AudioGranules(file, info.Patches));
    }

    [Fact]
    public void OggVorbis_SingleAudioPage_TrimsItsPadding_WithoutAHeadPatch()
    {
        // One page, also the last: FFmpeg dates its first packet 0 (no first-page rule on an
        // end-of-stream page), so LibVLC decodes all of it (harness: head 0.000 ms), but the
        // padding stayed (+1.19 ms). Decoded 0 + 128 + 128, granule 50 short of that.
        var ident = Concat([1], Encoding.ASCII.GetBytes("vorbis"), new byte[4], [2], LeU32(44100), new byte[12], [0xB8, 1]);
        var comment = Concat([3], Encoding.ASCII.GetBytes("vorbis"), new byte[8], [1]);
        var setup = Concat([5], Encoding.ASCII.GetBytes("vorbis"), Enumerable.Repeat((byte)0xFF, 8).ToArray(), ModeBits());
        byte[] Audio(byte first) => Concat([first], Enumerable.Repeat((byte)0x55, 99).ToArray());
        var file = Concat(OggPage(0x02, 0, 0, ident), OggPage(0x00, 0, 1, comment, setup),
            OggPage(0x04, 256 - 50, 2, Audio(Short), Audio(Short), Audio(Short)));
        Assert.True(ContainerGaplessInfo.TryParseOggVorbis(new MemoryStream(file), out var info));
        Assert.Equal(0, info!.LeadSamples);
        Assert.Equal(50, info.TailSamples);
        Assert.Empty(info.Patches);
    }

    // ── synthetic Ogg Vorbis ───────────────────────────────────────────────

    // Blocks of 256 and 2048 (mode 0 short, mode 1 long). Audio packets' first byte: bit 0
    // type (0), bit 1 mode, then for a long block the previous and next window flags.
    private const byte Short = 0x00, LongAfterShortBeforeLong = 0x0A, LongLong = 0x0E, LongBeforeShort = 0x06;

    /// <summary>
    /// ident | comment + setup | C: p0 p1 p2 (decoded 0 + 128 + 576, granule 704) |
    /// D1, D2: 10 long blocks each | E (end): long, short, short (1024 + 576 + 128), the
    /// granule 80 short of that. Total decoded is returned.
    /// </summary>
    private static (byte[] File, (int Offset, int Length) PageC) BuildVorbis(out long total, long firstGranule = 704, long shift = 0)
    {
        var ident = Concat([1], Encoding.ASCII.GetBytes("vorbis"), new byte[4], [2], LeU32(44100), new byte[12], [0xB8, 1]);
        var comment = Concat([3], Encoding.ASCII.GetBytes("vorbis"), new byte[8], [1]);
        var setup = Concat([5], Encoding.ASCII.GetBytes("vorbis"), Enumerable.Repeat((byte)0xFF, 8).ToArray(), ModeBits());
        byte[] Audio(byte first) => Concat([first], Enumerable.Repeat((byte)0x55, 99).ToArray());

        var pages = new List<byte[]>();
        var seq = 0u;
        pages.Add(OggPage(0x02, 0, seq++, ident));
        pages.Add(OggPage(0x00, 0, seq++, comment, setup));
        var pageCOffset = pages.Sum(p => p.Length);
        pages.Add(OggPage(0x00, firstGranule + shift, seq++, Audio(Short), Audio(Short), Audio(LongAfterShortBeforeLong)));
        var pageC = (pageCOffset, pages[^1].Length);
        total = 704;
        for (var d = 0; d < 2; d++)
        {
            total += 10 * 1024;
            pages.Add(OggPage(0x00, total + shift, seq++, Enumerable.Range(0, 10).Select(_ => Audio(LongLong)).ToArray()));
        }
        total += 1024 + 576 + 128;
        pages.Add(OggPage(0x04, total - 80 + shift, seq++, Audio(LongBeforeShort), Audio(Short), Audio(Short)));
        return (Concat(pages.ToArray()), pageC);
    }

    // The setup header's end: mode count - 1 (6 bits), then per mode block flag (1), window
    // type (16), transform type (16), mapping (8), then the framing bit; packed LSB first.
    private static byte[] ModeBits()
    {
        var bits = new List<int>();
        void Put(int value, int count) { for (var i = 0; i < count; i++) bits.Add((value >> i) & 1); }
        Put(1, 6);
        Put(0, 1); Put(0, 16); Put(0, 16); Put(0, 8);
        Put(1, 1); Put(0, 16); Put(0, 16); Put(1, 8);
        Put(1, 1);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++) bytes[i / 8] |= (byte)(bits[i] << (i % 8));
        return bytes;
    }

    private static byte[] OggPage(byte flags, long granule, uint seq, params byte[][] packets)
    {
        var lacing = new List<byte>();
        foreach (var p in packets)
        {
            for (var n = p.Length; ; n -= 255)
            {
                lacing.Add((byte)Math.Min(n, 255));
                if (n < 255) break;
            }
        }
        var page = Concat("OggS"u8.ToArray(), [0, flags], new byte[8], LeU32(7), LeU32(seq), new byte[4],
            [(byte)lacing.Count], lacing.ToArray(), Concat(packets));
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), ContainerGaplessInfo.OggCrc(page, crcOffset: 22));
        return page;
    }

    private static int LastPageOffset(byte[] file)
    {
        for (var i = file.Length - 4; i >= 0; i--)
            if (file.AsSpan(i, 4).SequenceEqual("OggS"u8)) return i;
        return -1;
    }

    private static byte[] LeU32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    // ── synthetic MP4 ──────────────────────────────────────────────────────

    private static byte[] BuildMp4((uint SegmentMs, int MediaTime)? edit, (uint Count, uint Delta)[] stts, string? smpb,
        bool extraVideo = false, string format = "mp4a")
    {
        var mvhd = FullBox("mvhd", U32(0), U32(0), U32(1000), U32(12000), new byte[80]);
        var trak = AudioTrak(edit, stts, format);
        var parts = new List<byte[]> { mvhd, trak };
        if (extraVideo) parts.Add(Box("trak", Box("mdia", FullBox("hdlr", U32(0), Encoding.ASCII.GetBytes("vide"), new byte[12]))));
        if (smpb != null)
        {
            var item = Box("----",
                FullBox("mean", Encoding.ASCII.GetBytes("com.apple.iTunes")),
                FullBox("name", Encoding.ASCII.GetBytes("iTunSMPB")),
                FullBox("data", U32(0), Encoding.ASCII.GetBytes(smpb)));
            parts.Add(Box("udta", FullBox("meta", FullBox("hdlr", U32(0), Encoding.ASCII.GetBytes("mdir"), new byte[12]),
                Box("ilst", item))));
        }
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4A "), U32(0));
        var mdat = Box("mdat", new byte[64]);
        return Concat(ftyp, mdat, Box("moov", parts.ToArray()));
    }

    private static byte[] AudioTrak((uint SegmentMs, int MediaTime)? edit, (uint Count, uint Delta)[] stts, string format)
    {
        var entry = Box(format, new byte[6], U16(1), new byte[8], U16(2), U16(16), new byte[4],
            U32(44100u << 16));
        var stsd = FullBox("stsd", U32(1), entry);
        var sttsBox = FullBox("stts", U32((uint)stts.Length), Concat(stts.Select(e => Concat(U32(e.Count), U32(e.Delta))).ToArray()));
        var minf = Box("minf", Box("stbl", stsd, sttsBox));
        var mdhd = FullBox("mdhd", U32(0), U32(0), U32(44100), U32(530432), new byte[4]);
        var hdlr = FullBox("hdlr", U32(0), Encoding.ASCII.GetBytes("soun"), new byte[12]);
        var mdia = Box("mdia", mdhd, hdlr, minf);
        var tkhd = FullBox("tkhd", new byte[80]);
        if (edit is not { } e) return Box("trak", tkhd, mdia);
        var elst = FullBox("elst", U32(1), U32(e.SegmentMs), U32((uint)e.MediaTime), U32(0x00010000));
        return Box("trak", tkhd, Box("edts", elst), mdia);
    }

    private static byte[] Box(string type, params byte[][] body)
    {
        var content = Concat(body);
        var b = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        content.CopyTo(b, 8);
        return b;
    }

    private static byte[] FullBox(string type, params byte[][] body) => Box(type, new[] { new byte[4] }.Concat(body).ToArray());

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static int IndexOf(byte[] data, string type)
    {
        var t = Encoding.ASCII.GetBytes(type);
        for (var i = 0; i + t.Length <= data.Length; i++)
            if (data.AsSpan(i, t.Length).SequenceEqual(t)) return i;
        return -1;
    }

    private static IEnumerable<bool> Diff(byte[] a, byte[] b) => a.Zip(b, (x, y) => x != y);
}
