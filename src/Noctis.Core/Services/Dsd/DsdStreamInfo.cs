using System.Buffers.Binary;
using System.Text;

namespace Noctis.Services.Dsd;

/// <summary>
/// Where the raw 1-bit samples of a DSD file live and how they are laid out: what
/// <see cref="DsdPcmDecoder"/> needs to turn any stretch of the file into PCM.
///
/// DSF (Sony "DSD Stream File", spec v1.01): little-endian chunks "DSD ", "fmt ", "data";
/// the data interleaves one block (4096 bytes) per channel, and each byte holds 8 samples,
/// the first in the least significant bit (bits-per-sample 1) or, rarely, the most
/// significant one (bits-per-sample 8).
/// DSDIFF (.dff, Philips spec v1.5): big-endian FRM8 form; the "DSD " chunk interleaves one
/// byte per channel, first sample in the most significant bit. DST-compressed DSDIFF has no
/// "DSD " chunk and is not playable (<see cref="Probe(string)"/> returns null).
/// </summary>
internal sealed class DsdStreamInfo
{
    /// <summary>1-bit sample rate per channel: 2822400 for DSD64, 5644800 for DSD128…</summary>
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    /// <summary>File offset of the first byte of sound data.</summary>
    public required long DataOffset { get; init; }
    /// <summary>Bytes of real (unpadded) sound data per channel.</summary>
    public required long BytesPerChannel { get; init; }
    /// <summary>Bytes of one channel before the next channel's run: 4096 for DSF, 1 for DSDIFF.</summary>
    public required int BlockSize { get; init; }
    /// <summary>First sample of a byte in bit 0 (DSF) rather than bit 7 (DSDIFF).</summary>
    public required bool LsbFirst { get; init; }
    /// <summary>For each output channel in WAV order, the file channel it comes from.</summary>
    public required int[] ChannelOrder { get; init; }
    /// <summary>WAVE_FORMAT_EXTENSIBLE speaker mask for the output, 0 when unknown.</summary>
    public required uint ChannelMask { get; init; }

    /// <summary>Probes <paramref name="path"/>; null when it isn't a playable DSF/DSDIFF file.</summary>
    public static DsdStreamInfo? Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Probe(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static DsdStreamInfo? Probe(Stream stream)
    {
        try
        {
            Span<byte> id = stackalloc byte[4];
            stream.Position = 0;
            if (stream.Read(id) != 4) return null;
            var tag = Encoding.ASCII.GetString(id);
            stream.Position = 0;
            return tag switch
            {
                "DSD " => ProbeDsf(stream),
                "FRM8" => ProbeDff(stream),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True for the DSD extensions the library scans (.dsf, .dff).</summary>
    public static bool IsDsdPath(string? path)
    {
        var ext = Path.GetExtension(path);
        return ext != null && (ext.Equals(".dsf", StringComparison.OrdinalIgnoreCase)
                               || ext.Equals(".dff", StringComparison.OrdinalIgnoreCase));
    }

    private static DsdStreamInfo? ProbeDsf(Stream stream)
    {
        var head = new byte[28 + 52];
        if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
        var dsdChunkSize = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(4));
        if (dsdChunkSize != 28) return null;
        var fmt = head.AsSpan(28);
        if (Encoding.ASCII.GetString(fmt[..4]) != "fmt ") return null;
        var fmtSize = BinaryPrimitives.ReadInt64LittleEndian(fmt[4..]);
        var formatId = BinaryPrimitives.ReadInt32LittleEndian(fmt[16..]);
        var channelType = BinaryPrimitives.ReadInt32LittleEndian(fmt[20..]);
        var channels = BinaryPrimitives.ReadInt32LittleEndian(fmt[24..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[28..]);
        var bits = BinaryPrimitives.ReadInt32LittleEndian(fmt[32..]);
        var samples = BinaryPrimitives.ReadInt64LittleEndian(fmt[36..]);
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(fmt[44..]);
        if (formatId != 0 || channels is < 1 or > 6 || rate <= 0 || bits is not (1 or 8)
            || blockSize <= 0 || samples < 0 || fmtSize < 52)
            return null;

        // The data chunk follows the fmt chunk.
        var dataChunk = 28 + fmtSize;
        stream.Position = dataChunk;
        var dataHead = new byte[12];
        if (stream.ReadAtLeast(dataHead, 12, throwOnEndOfStream: false) < 12) return null;
        if (Encoding.ASCII.GetString(dataHead, 0, 4) != "data") return null;
        var dataSize = BinaryPrimitives.ReadInt64LittleEndian(dataHead.AsSpan(4)) - 12;
        var dataOffset = dataChunk + 12;
        // A truncated file holds fewer whole block groups than the header promises.
        var available = Math.Min(dataSize, stream.Length - dataOffset);
        var wholeGroups = Math.Max(0, available) / ((long)blockSize * channels);
        var bytesPerChannel = Math.Min((samples + 7) / 8, wholeGroups * blockSize);

        return new DsdStreamInfo
        {
            SampleRate = rate,
            Channels = channels,
            DataOffset = dataOffset,
            BytesPerChannel = bytesPerChannel,
            BlockSize = blockSize,
            LsbFirst = bits == 1,
            // DSF's channel orders (spec table "Channel Type") already match WAV's.
            ChannelOrder = Enumerable.Range(0, channels).ToArray(),
            ChannelMask = DsfChannelMask(channelType, channels),
        };
    }

    /// <summary>Speaker mask for a DSF channel type (1 mono … 7 5.1), 0 when it doesn't fit the count.</summary>
    internal static uint DsfChannelMask(int channelType, int channels)
    {
        var (mask, count) = channelType switch
        {
            1 => (0x4u, 1),     // C
            2 => (0x3u, 2),     // FL FR
            3 => (0x7u, 3),     // FL FR C
            4 => (0x33u, 4),    // FL FR BL BR
            5 => (0xFu, 4),     // FL FR C LFE
            6 => (0x37u, 5),    // FL FR C BL BR
            7 => (0x3Fu, 6),    // FL FR C LFE BL BR
            _ => (0u, 0),
        };
        return count == channels ? mask : 0u;
    }

    private static DsdStreamInfo? ProbeDff(Stream stream)
    {
        var info = DsdiffReader.TryRead(stream);
        if (info == null || info.DataOffset < 0 || info.Channels is < 1 or > 6 || info.SampleRate <= 0)
            return null;

        // Put the channels in WAV order by speaker; ids we don't know keep file order.
        var ids = info.ChannelIds ?? Array.Empty<string>();
        var order = Enumerable.Range(0, info.Channels).ToArray();
        uint mask = 0;
        if (ids.Count == info.Channels)
        {
            var bits = ids.Select(DffSpeakerBit).ToArray();
            if (bits.All(b => b != 0) && bits.Distinct().Count() == bits.Length)
            {
                order = order.OrderBy(i => bits[i]).ToArray();
                foreach (var b in bits) mask |= b;
            }
        }
        if (mask == 0 && info.Channels == 2) mask = 0x3;

        return new DsdStreamInfo
        {
            SampleRate = info.SampleRate,
            Channels = info.Channels,
            DataOffset = info.DataOffset,
            BytesPerChannel = info.DataBytes / info.Channels,
            BlockSize = 1,
            LsbFirst = false,
            ChannelOrder = order,
            ChannelMask = mask,
        };
    }

    private static uint DffSpeakerBit(string id) => id switch
    {
        "SLFT" or "MLFT" => 0x1,
        "SRGT" or "MRGT" => 0x2,
        "C   " => 0x4,
        "LFE " => 0x8,
        "LS  " => 0x10,
        "RS  " => 0x20,
        _ => 0,
    };
}
