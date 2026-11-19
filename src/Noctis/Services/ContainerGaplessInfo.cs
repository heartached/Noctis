using System.Buffers.Binary;
using System.Text;

namespace Noctis.Services;

/// <summary>
/// The encoder priming and padding an AAC-in-MP4 or an Ogg Opus file declares, and the
/// container bytes that make LibVLC decode every packet so the gapless engine can trim
/// them itself.
///
/// Why: under the forced avformat demuxer FFmpeg hands these trims to LibVLC as a negative
/// timestamp on the first packet plus "skip samples" side data. LibVLC 3's avformat demuxer
/// drops the side data (modules/demux/avformat/demux.c builds the block from the packet
/// alone) and its audio decoders discard every packet until one carries a positive
/// timestamp (codec/avcodec/audio.c "wait for the first PTS", codec/opus.c the same), so
/// the priming packet is never decoded. Harness 11-19, white noise cut in three:
/// AAC: the first real frame decodes without its MDCT overlap (13 ms of near-silence at
/// each seam) and the 208-sample padding stays (+4.73 ms); Opus: the whole first packet
/// goes, 648 real samples after the 312 pre-skip (-13.5 ms). LibVLC's own MP4 demuxer
/// starts at the edit's media time and drops the same packet; its Ogg demuxer lost 73.5 ms.
///
/// The fix serves the file with the trims removed (an MP4's edit list box renamed to
/// 'free', an Opus head's pre-skip set to 0), so every packet carries a valid timestamp and
/// is decoded; the engine then drops <see cref="LeadSamples"/> from the head (also after
/// every seek: LibVLC's timeline then runs that much ahead of the track's) and
/// <see cref="TailSamples"/> from the end. Measured on the stripped files before the engine
/// trim: exactly +1232 samples per AAC seam (1024 priming + 208 padding) and +312 per Opus
/// seam, i.e. nothing else is lost or added.
/// </summary>
internal sealed record ContainerGaplessInfo(
    string Kind, int Timescale, int Channels, long LeadSamples, long TailSamples,
    IReadOnlyList<(long Offset, byte[] Bytes)> Patches)
{
    // A moov larger than this is not an audio file this handles (long audiobooks stay
    // well below: their sample tables are a few MB).
    private const int MaxMoovBytes = 64 << 20;

    /// <summary>Frames to drop from the head at the engine's rate (LibVLC resamples to it).</summary>
    public int LeadFrames(int outputSampleRate) => Scale(LeadSamples, outputSampleRate);

    /// <summary>Frames to cut from the end at the engine's rate.</summary>
    public int TailFrames(int outputSampleRate) => Scale(TailSamples, outputSampleRate);

    private int Scale(long samples, int outputSampleRate) =>
        Timescale <= 0 || outputSampleRate <= 0 || samples <= 0
            ? 0
            : (int)Math.Round((double)samples * outputSampleRate / Timescale);

    internal static bool IsCandidatePath(string path)
    {
        var ext = Path.GetExtension(path);
        return IsMp4Extension(ext) || IsOggExtension(ext);
    }

    private static bool IsMp4Extension(string ext) =>
        ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    private static bool IsOggExtension(string ext) =>
        ext.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".oga", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a local file's gapless information; false when it has none this handles or cannot be read.</summary>
    public static bool TryRead(string path, out ContainerGaplessInfo? info)
    {
        info = null;
        try
        {
            if (!IsCandidatePath(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.RandomAccess);
            return IsMp4Extension(Path.GetExtension(path))
                ? TryParseMp4(stream, out info)
                : TryParseOggOpus(stream, out info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ── Ogg Opus ────────────────────────────────────────────────────────────

    /// <summary>
    /// The pre-skip of an Ogg Opus stream, from the OpusHead in its first page. The patch
    /// sets it to 0 and re-signs the page. The end trim needs nothing: LibVLC's Opus decoder
    /// cuts the last packet to the duration FFmpeg gives it ("truncating 648 off 960").
    /// Pure over the stream; internal for tests.
    /// </summary>
    internal static bool TryParseOggOpus(Stream stream, out ContainerGaplessInfo? info)
    {
        info = null;
        var head = new byte[27 + 255];
        stream.Position = 0;
        if (stream.ReadAtLeast(head, 27, throwOnEndOfStream: false) < 27) return false;
        if (!head.AsSpan(0, 4).SequenceEqual("OggS"u8) || head[4] != 0 || (head[5] & 0x02) == 0) return false;
        int segments = head[26];
        var headerLen = 27 + segments;
        stream.Position = 0;
        if (stream.ReadAtLeast(head, headerLen, throwOnEndOfStream: false) < headerLen) return false;
        var payloadLen = 0;
        for (var i = 0; i < segments; i++) payloadLen += head[27 + i];
        if (payloadLen < 19) return false;
        var page = new byte[headerLen + payloadLen];
        stream.Position = 0;
        if (stream.ReadAtLeast(page, page.Length, throwOnEndOfStream: false) < page.Length) return false;
        var p = page.AsSpan(headerLen);
        if (!p[..8].SequenceEqual("OpusHead"u8) || (p[8] & 0xF0) != 0) return false;
        int channels = p[9];
        int preSkip = BinaryPrimitives.ReadUInt16LittleEndian(p[10..]);
        if (channels == 0 || preSkip == 0) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22)) != OggCrc(page, crcOffset: 22)) return false;

        var patched = (byte[])page.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(headerLen + 10), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(22), OggCrc(patched, crcOffset: 22));
        // Only the bytes that change: the pre-skip and the page checksum.
        info = new ContainerGaplessInfo("opus", 48000, channels, preSkip, 0,
            [(22, patched[22..26]), (headerLen + 10, patched[(headerLen + 10)..(headerLen + 12)])]);
        return true;
    }

    private static readonly uint[] OggCrcTable = BuildOggCrcTable();

    private static uint[] BuildOggCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var b = 0; b < 8; b++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    /// <summary>The Ogg page checksum, computed with the 4 checksum bytes as zero. Pure.</summary>
    internal static uint OggCrc(ReadOnlySpan<byte> page, int crcOffset)
    {
        uint crc = 0;
        for (var i = 0; i < page.Length; i++)
        {
            var b = i >= crcOffset && i < crcOffset + 4 ? (byte)0 : page[i];
            crc = (crc << 8) ^ OggCrcTable[((crc >> 24) & 0xFF) ^ b];
        }
        return crc;
    }

    // ── MP4 / AAC ───────────────────────────────────────────────────────────

    /// <summary>
    /// The priming and padding of the AAC track of an MP4 with exactly one audio track and
    /// no video: from its edit list (what FFmpeg and Apple's encoders write), else from an
    /// iTunes iTunSMPB tag. The patches rename the track's 'edts' box to 'free', which both
    /// LibVLC demuxers skip, and the tag to "iTunSMPb", which FFmpeg ignores. Pure over the
    /// stream; internal for tests.
    /// </summary>
    internal static bool TryParseMp4(Stream stream, out ContainerGaplessInfo? info)
    {
        info = null;
        long moovStart = -1, moovSize = 0;
        long pos = 0;
        var len = stream.Length;
        var hdr = new byte[16];
        while (pos + 8 <= len)
        {
            stream.Position = pos;
            if (stream.ReadAtLeast(hdr, 16, throwOnEndOfStream: false) < 8) return false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var headerLen = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8)); headerLen = 16; }
            else if (size == 0) size = len - pos;
            if (size < headerLen || pos + size > len) return false;
            if (hdr.AsSpan(4, 4).SequenceEqual("moov"u8)) { moovStart = pos; moovSize = size; break; }
            pos += size;
        }
        if (moovStart < 0 || moovSize > MaxMoovBytes) return false;
        var moov = new byte[moovSize];
        stream.Position = moovStart;
        if (stream.ReadAtLeast(moov, moov.Length, throwOnEndOfStream: false) < moov.Length) return false;
        return TryParseMoov(moov, moovStart, out info);
    }

    /// <summary>Parses a whole 'moov' box (header included) found at <paramref name="fileOffset"/>. Pure.</summary>
    internal static bool TryParseMoov(ReadOnlySpan<byte> moov, long fileOffset, out ContainerGaplessInfo? info)
    {
        info = null;
        if (!TryBox(moov, 0, out var moovBody, out _, out var type) || type != "moov") return false;
        long movieTimescale = 0;
        Smpb? smpb = null;
        var audioTraks = 0;
        Trak? audio = null;
        foreach (var (start, end, t) in Children(moov, moovBody, moov.Length))
        {
            switch (t)
            {
                case "mvhd":
                    movieTimescale = ReadHeaderTimescale(moov[start..end]);
                    break;
                case "trak":
                    var trak = ParseTrak(moov, start, end);
                    if (trak == null) return false;
                    if (trak.Handler == "vide") return false;
                    if (trak.Handler == "soun") { audioTraks++; audio = trak; }
                    smpb ??= trak.Smpb;
                    break;
                case "udta":
                    smpb ??= FindSmpb(moov, start, end);
                    break;
            }
        }
        if (audioTraks != 1 || audio is not { Format: "mp4a" } a || a.Timescale <= 0 || a.Decoded <= 0)
            return false;

        long lead, tail;
        var patches = new List<(long, byte[])>();
        if (a.EdtsOffset >= 0)
        {
            // One edit that plays the media from MediaTime for SegmentDuration (in movie
            // time). Empty edits (a delay) and multi-edit timelines are not encoder trims.
            if (a.Edits is not [var e] || e.MediaTime < 0 || movieTimescale <= 0) return false;
            lead = e.MediaTime;
            var played = (long)Math.Round((double)e.SegmentDuration * a.Timescale / movieTimescale);
            tail = Math.Max(0, a.Decoded - lead - played);
            if (lead == 0 && tail == 0) return false;
            patches.Add((fileOffset + a.EdtsOffset + 4, "free"u8.ToArray()));
        }
        else if (smpb != null && TryParseSmpb(smpb.Value, out var priming, out var padding, out var valid))
        {
            lead = priming;
            tail = valid > 0 ? a.Decoded - priming - valid : padding;
            if (tail < 0 || lead + tail >= a.Decoded || (lead == 0 && tail == 0)) return false;
        }
        else
        {
            return false;
        }
        if (lead + tail >= a.Decoded) return false;
        // FFmpeg also reads the iTunSMPB tag: it moves the stream's start time to the priming,
        // and LibVLC then stamps the first packet below zero and drops it just the same
        // (harness: a tagged file still lost 41 ms per seam with its edit list gone). One
        // letter of the tag's name changed makes FFmpeg pass over it.
        if (smpb != null)
            patches.Add((fileOffset + smpb.NameOffset + 7, "b"u8.ToArray()));
        info = new ContainerGaplessInfo("aac", (int)a.Timescale, a.Channels, lead, tail, patches);
        return true;
    }

    /// <summary>iTunSMPB: " 00000000 PPPPPPPP QQQQQQQQ VVVVVVVVVVVVVVVV ..." (hex: priming, padding, valid samples). Pure.</summary>
    internal static bool TryParseSmpb(string value, out long priming, out long padding, out long valid)
    {
        priming = padding = valid = 0;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 &&
               long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out priming) &&
               long.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out padding) &&
               long.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out valid) &&
               priming >= 0 && padding >= 0 && valid >= 0;
    }

    private sealed record Trak(string? Handler, string? Format, long Timescale, long Decoded, int Channels,
        int EdtsOffset, (long SegmentDuration, long MediaTime)[] Edits, Smpb? Smpb);

    /// <summary>An iTunSMPB tag's value and where its name ("iTunSMPB") starts in the moov.</summary>
    private sealed record Smpb(string Value, int NameOffset);

    private static Trak? ParseTrak(ReadOnlySpan<byte> buf, int start, int end)
    {
        string? handler = null, format = null;
        Smpb? smpb = null;
        long timescale = 0, total = 0;
        int channels = 0, edtsOffset = -1;
        var edits = Array.Empty<(long, long)>();
        if (!TryBox(buf, start, out var body, out _, out _)) return null;
        foreach (var (s, e, t) in Children(buf, body, end))
        {
            if (t == "edts")
            {
                edtsOffset = s;
                foreach (var (es, ee, et) in Children(buf, BodyOf(buf, s), e))
                    if (et == "elst") edits = ParseElst(buf[es..ee]);
            }
            else if (t == "udta")
            {
                smpb ??= FindSmpb(buf, s, e);
            }
            else if (t == "mdia")
            {
                foreach (var (ms, me, mt) in Children(buf, BodyOf(buf, s), e))
                {
                    if (mt == "mdhd") timescale = ReadHeaderTimescale(buf[ms..me]);
                    else if (mt == "hdlr" && me - ms >= 20) handler = Encoding.ASCII.GetString(buf.Slice(ms + 16, 4));
                    else if (mt == "minf")
                        foreach (var (ns, ne, nt) in Children(buf, BodyOf(buf, ms), me))
                            if (nt == "stbl")
                                foreach (var (ts, te, tt) in Children(buf, BodyOf(buf, ns), ne))
                                {
                                    if (tt == "stsd") (format, channels) = ParseStsd(buf[ts..te]);
                                    else if (tt == "stts") total = DecodedDuration(buf[ts..te]);
                                }
                }
            }
        }
        return new Trak(handler, format, timescale, total, channels, edtsOffset, edits, smpb);
    }

    private static int BodyOf(ReadOnlySpan<byte> buf, int start) => TryBox(buf, start, out var body, out _, out _) ? body : start + 8;

    // mvhd / mdhd: version(1) flags(3) then created/modified (4 or 8 each), then the timescale.
    private static long ReadHeaderTimescale(ReadOnlySpan<byte> box)
    {
        if (box.Length < 8 + 4 + 20) return 0;
        var v = box[8];
        var at = 8 + 4 + (v == 1 ? 16 : 8);
        return box.Length >= at + 4 ? BinaryPrimitives.ReadUInt32BigEndian(box[at..]) : 0;
    }

    private static (long, long)[] ParseElst(ReadOnlySpan<byte> box)
    {
        if (box.Length < 16) return [];
        var v = box[8];
        var count = BinaryPrimitives.ReadUInt32BigEndian(box[12..]);
        var entry = v == 1 ? 20 : 12;
        if (count > 1024 || 16 + count * entry > box.Length) return [];
        var list = new (long, long)[count];
        for (var i = 0; i < count; i++)
        {
            var e = box[(16 + i * entry)..];
            list[i] = v == 1
                ? ((long)BinaryPrimitives.ReadUInt64BigEndian(e), BinaryPrimitives.ReadInt64BigEndian(e[8..]))
                : (BinaryPrimitives.ReadUInt32BigEndian(e), BinaryPrimitives.ReadInt32BigEndian(e[4..]));
        }
        return list;
    }

    // stsd: version/flags, entry count, then the first sample entry: size, format, 6 reserved,
    // data ref index (2), then the audio sample entry's version (2), revision (2), vendor (4),
    // channel count (2).
    private static (string?, int) ParseStsd(ReadOnlySpan<byte> box)
    {
        if (box.Length < 16 + 8 + 8 + 10) return (null, 0);
        var entry = box[16..];
        var format = Encoding.ASCII.GetString(entry.Slice(4, 4));
        int channels = BinaryPrimitives.ReadUInt16BigEndian(entry[24..]);
        return (format, channels);
    }

    /// <summary>
    /// What the decoder outputs for the track: every AAC packet decodes to a full frame,
    /// so packets × the frame duration (the largest delta), in the media timescale. Not the
    /// stts sum: FFmpeg's muxer gives the last packet only its valid part (816 of 1024).
    /// </summary>
    private static long DecodedDuration(ReadOnlySpan<byte> box)
    {
        if (box.Length < 16) return 0;
        var count = BinaryPrimitives.ReadUInt32BigEndian(box[12..]);
        if (16 + (long)count * 8 > box.Length) return 0;
        long packets = 0, frame = 0;
        for (var i = 0; i < count; i++)
        {
            var e = box[(16 + i * 8)..];
            packets += BinaryPrimitives.ReadUInt32BigEndian(e);
            frame = Math.Max(frame, BinaryPrimitives.ReadUInt32BigEndian(e[4..]));
        }
        return packets * frame;
    }

    // udta > meta > ilst > "----" (mean "com.apple.iTunes", name "iTunSMPB", data).
    private static Smpb? FindSmpb(ReadOnlySpan<byte> buf, int start, int end)
    {
        foreach (var (ms, me, mt) in Children(buf, BodyOf(buf, start), end))
        {
            if (mt != "meta") continue;
            // ISO 'meta' is a full box (4 bytes of version/flags before its children);
            // QuickTime's is not.
            var body = BodyOf(buf, ms);
            if (body + 8 <= me && !IsBoxType(buf.Slice(body + 4, 4))) body += 4;
            foreach (var (ls, le, lt) in Children(buf, body, me))
            {
                if (lt != "ilst") continue;
                foreach (var (is_, ie, it) in Children(buf, BodyOf(buf, ls), le))
                {
                    if (it != "----") continue;
                    string? name = null, data = null;
                    var nameAt = -1;
                    foreach (var (fs, fe, ft) in Children(buf, BodyOf(buf, is_), ie))
                    {
                        var fb = BodyOf(buf, fs);
                        if (ft == "name" && fe - fb >= 4)
                        {
                            nameAt = fb + 4;
                            name = Encoding.ASCII.GetString(buf[nameAt..fe]);
                        }
                        else if (ft == "data" && fe - fb >= 8) data = Encoding.ASCII.GetString(buf[(fb + 8)..fe]);
                    }
                    if (name == "iTunSMPB" && data != null) return new Smpb(data, nameAt);
                }
            }
        }
        return null;
    }

    private static bool IsBoxType(ReadOnlySpan<byte> t)
    {
        foreach (var c in t)
            if (c < 0x20 || c > 0x7E) return false;
        return true;
    }

    private static bool TryBox(ReadOnlySpan<byte> buf, int start, out int body, out int end, out string type)
    {
        body = end = 0;
        type = "";
        if (start < 0 || start + 8 > buf.Length) return false;
        long size = BinaryPrimitives.ReadUInt32BigEndian(buf[start..]);
        var headerLen = 8;
        if (size == 1)
        {
            if (start + 16 > buf.Length) return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf[(start + 8)..]);
            headerLen = 16;
        }
        else if (size == 0)
        {
            size = buf.Length - start;
        }
        if (size < headerLen || start + size > buf.Length) return false;
        type = Encoding.ASCII.GetString(buf.Slice(start + 4, 4));
        body = start + headerLen;
        end = (int)(start + size);
        return true;
    }

    private static List<(int Start, int End, string Type)> Children(ReadOnlySpan<byte> buf, int from, int to)
    {
        var list = new List<(int, int, string)>();
        var pos = from;
        while (pos + 8 <= to)
        {
            if (!TryBox(buf[..to], pos, out _, out var end, out var type)) break;
            list.Add((pos, end, type));
            pos = end;
        }
        return list;
    }
}
