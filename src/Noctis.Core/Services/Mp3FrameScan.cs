using System.Buffers.Binary;

namespace Noctis.Services;

/// <summary>
/// The true length of a VBR MP3 that carries no Xing or VBRI header. Such a file has nothing
/// that states its length, so every reader guesses from a bitrate: TagLib from the first
/// frame, LibVLC/FFmpeg from the first few. On a 65-minute VBR file without a header (some
/// recorders, stream rips and joined DJ mixes write them) the library showed 10:15 or 14:37,
/// the player 60:37 or 62:27, and the last minutes were out of the seek bar's reach (Discord
/// "Trouble Playing Long MP3 File", 10-26). Counting the frames gives the exact length.
///
/// A file with a Xing/Info/VBRI header, or a constant-bitrate file (whose estimate is right),
/// is left to the existing readers: <see cref="TryMeasureHeaderlessVbr(string, out TimeSpan, out int)"/>
/// returns false for it after reading a few KB, so a library scan does not walk every MP3.
/// </summary>
internal static class Mp3FrameScan
{
    // Enough to step over a normal ID3v2 tag without its pictures and find the first frame;
    // the tag's own size skips large artwork by seeking.
    private const int SyncSearchBytes = 64 * 1024;
    // Points sampled through the file to tell VBR from CBR without reading it all.
    private const int BitrateProbes = 12;
    private const int ProbeBytes = 16 * 1024;
    private const int WalkBlockBytes = 1 << 20;

    private static readonly int[] Mpeg1Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Kbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    private readonly record struct Frame(int Version, int SampleRate, int Kbps, int Length, int Samples, int SideInfo);

    /// <summary>The counted length of a local headerless VBR MP3; false for any other file or on error.</summary>
    public static bool TryMeasureHeaderlessVbr(string path, out TimeSpan duration, out int averageKbps)
    {
        duration = TimeSpan.Zero;
        averageKbps = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.SequentialScan);
            return TryMeasureHeaderlessVbr(stream, out duration, out averageKbps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Same, over a seekable stream. Internal for tests.</summary>
    internal static bool TryMeasureHeaderlessVbr(Stream stream, out TimeSpan duration, out int averageKbps)
    {
        duration = TimeSpan.Zero;
        averageKbps = 0;
        var length = stream.Length;

        long start = 0;
        var id3 = new byte[10];
        stream.Position = 0;
        if (stream.ReadAtLeast(id3, id3.Length, throwOnEndOfStream: false) == id3.Length &&
            id3[0] == (byte)'I' && id3[1] == (byte)'D' && id3[2] == (byte)'3')
            start = Id3v2TotalSize(id3);
        if (start >= length) return false;

        // The first frame: a header frame (Xing/Info/VBRI) states the length, so the existing
        // readers are right and nothing is counted.
        var head = ReadAt(stream, start, SyncSearchBytes + 2048);
        var first = FindFrame(head, 0, SyncSearchBytes, out var firstFrame);
        if (first < 0) return false;
        if (HasLengthHeader(head, first, firstFrame)) return false;
        var audioStart = start + first;

        // CBR: every frame has the first one's bitrate and the estimates are exact.
        var vbr = false;
        for (var p = 1; p <= BitrateProbes && !vbr; p++)
        {
            var at = audioStart + (length - audioStart) * p / (BitrateProbes + 1);
            var probe = ReadAt(stream, at, ProbeBytes);
            if (FindFrame(probe, 0, probe.Length, out var f, firstFrame) >= 0 && f.Kbps != firstFrame.Kbps)
                vbr = true;
        }
        if (!vbr) return false;

        // Walk every frame. A gap (junk, a damaged frame) resyncs on the next valid pair of
        // headers; an ID3v1/APE tag at the end ends the walk. The pair sets the format counted
        // from there on, so a file joined from recordings of different sample rates counts
        // every part: matching only the first frame's format stopped the count at the join
        // and the player clamped every seek there.
        double seconds = 0;
        long audioBytes = 0;
        var current = firstFrame;
        var block = new byte[WalkBlockBytes + 4096];
        var blockStart = audioStart;
        var filled = 0;
        var pos = 0;
        stream.Position = audioStart;
        while (true)
        {
            if (filled - pos < 4096)
            {
                // Slide the unread tail to the front and top the block up.
                var keep = filled - pos;
                Buffer.BlockCopy(block, pos, block, 0, keep);
                blockStart += pos;
                pos = 0;
                filled = keep + stream.ReadAtLeast(block.AsSpan(keep), block.Length - keep, throwOnEndOfStream: false);
                if (filled < 4) break;
            }
            var whole = TryReadHeader(block.AsSpan(pos, filled - pos), out var frame) &&
                        blockStart + pos + frame.Length <= length;
            if (whole && frame.SampleRate == current.SampleRate && frame.Version == current.Version)
            {
                seconds += (double)frame.Samples / frame.SampleRate;
                audioBytes += frame.Length;
                pos += frame.Length;
                continue;
            }
            // A whole frame of another format starts a new part if the next header confirms it
            // (FindFrame then returns pos itself); anything else is searched past.
            var next = FindFrame(block.AsSpan(0, filled), whole ? pos : pos + 1, filled - pos - 1, out var found);
            if (next < 0)
            {
                if (filled < block.Length) break; // the end of the file
                // No frame in this block: refill, keeping enough to confirm one straddling the cut.
                pos = Math.Max(pos + 1, filled - 2048);
                continue;
            }
            current = found;
            pos = next;
        }

        if (seconds <= 0) return false;
        duration = TimeSpan.FromSeconds(seconds);
        averageKbps = (int)Math.Round(audioBytes * 8d / seconds / 1000d);
        return true;
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset >= stream.Length) return [];
        var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
        stream.Position = offset;
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Size of an ID3v2 tag from its 10-byte header, footer included. Pure.</summary>
    private static long Id3v2TotalSize(ReadOnlySpan<byte> header)
    {
        var size = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14) | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
        var footer = (header[5] & 0x10) != 0 ? 10 : 0;
        return 10L + size + footer;
    }

    // Xing/Info sits after the side info; VBRI always 32 bytes after the header.
    private static bool HasLengthHeader(ReadOnlySpan<byte> data, int at, Frame frame)
    {
        static bool TagAt(ReadOnlySpan<byte> d, int i, ReadOnlySpan<byte> tag) =>
            i + 4 <= d.Length && d.Slice(i, 4).SequenceEqual(tag);
        var xing = at + 4 + frame.SideInfo;
        return TagAt(data, xing, "Xing"u8) || TagAt(data, xing, "Info"u8) || TagAt(data, at + 4 + 32, "VBRI"u8);
    }

    // A frame header that the next frame confirms (a lone 0xFFE sync inside audio data is common).
    // With a reference frame, the two must also share its version and sample rate.
    private static int FindFrame(ReadOnlySpan<byte> data, int from, int span, out Frame frame, Frame? like = null)
    {
        frame = default;
        var end = Math.Min(data.Length - 4, from + span);
        for (var i = Math.Max(0, from); i <= end; i++)
        {
            if (!TryReadHeader(data[i..], out var f)) continue;
            if (like is { } l && (f.Version != l.Version || f.SampleRate != l.SampleRate)) continue;
            var next = i + f.Length;
            if (next + 4 > data.Length) continue;
            if (!TryReadHeader(data[next..], out var n) || n.Version != f.Version || n.SampleRate != f.SampleRate) continue;
            frame = f;
            return i;
        }
        return -1;
    }

    // MPEG audio Layer III frame header (free-format frames are not counted).
    private static bool TryReadHeader(ReadOnlySpan<byte> h, out Frame frame)
    {
        frame = default;
        if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;
        var version = (h[1] >> 3) & 0x3;   // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5, 1 = reserved
        var layer = (h[1] >> 1) & 0x3;     // 1 = Layer III
        var bitrateIndex = (h[2] >> 4) & 0xF;
        var rateIndex = (h[2] >> 2) & 0x3;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 0xF || rateIndex == 3) return false;
        var baseRate = rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
        var sampleRate = version switch { 3 => baseRate, 2 => baseRate / 2, _ => baseRate / 4 };
        var mpeg1 = version == 3;
        var kbps = (mpeg1 ? Mpeg1Kbps : Mpeg2Kbps)[bitrateIndex];
        var padding = (h[2] >> 1) & 0x1;
        var length = (mpeg1 ? 144000 : 72000) * kbps / sampleRate + padding;
        var mono = ((h[3] >> 6) & 0x3) == 3;
        var sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        frame = new Frame(version, sampleRate, kbps, length, mpeg1 ? 1152 : 576, sideInfo);
        return length > 4;
    }
}
