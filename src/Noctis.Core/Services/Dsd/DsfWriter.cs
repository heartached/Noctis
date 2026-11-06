using System.Buffers.Binary;
using System.Text;

namespace Noctis.Services.Dsd;

/// <summary>
/// Streams a DSF file (Sony "DSD Stream File" v1.01): the 28-byte "DSD " chunk, the
/// 52-byte "fmt " chunk (format version 1, raw DSD, 1 bit per sample = LSB first, block
/// size 4096 per channel), then "data" with one 4096-byte block of each channel in turn.
/// Sizes and the sample count are patched in by <see cref="Finish"/>; the metadata pointer
/// stays 0 — TagLib# appends the ID3v2 chunk (and sets the pointer) when tags are written.
/// </summary>
internal sealed class DsfWriter
{
    public const int BlockSize = 4096;
    private const int HeaderBytes = 28 + 52 + 12;

    private readonly Stream _out;
    private readonly int _channels;
    private readonly int _rate;
    private readonly byte[][] _blocks;
    private int _fill;
    private long _groups;
    private long _bytesPerChannel;

    public DsfWriter(Stream output, int dsdRate, int channels)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels), "mono or stereo");
        _out = output;
        _rate = dsdRate;
        _channels = channels;
        _blocks = new byte[channels][];
        for (var c = 0; c < channels; c++) _blocks[c] = new byte[BlockSize];
        _out.Write(BuildHeader(0, 0, 0));
    }

    /// <summary>Appends <paramref name="count"/> bytes of every channel (8 samples each, LSB first).</summary>
    public void Write(byte[][] perChannel, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var n = Math.Min(BlockSize - _fill, count - offset);
            for (var c = 0; c < _channels; c++)
                perChannel[c].AsSpan(offset, n).CopyTo(_blocks[c].AsSpan(_fill));
            _fill += n;
            offset += n;
            _bytesPerChannel += n;
            if (_fill == BlockSize) FlushGroup();
        }
    }

    /// <summary>Writes the last (zero-padded, per the spec) block and the final header.</summary>
    public void Finish()
    {
        if (_fill > 0)
        {
            for (var c = 0; c < _channels; c++) _blocks[c].AsSpan(_fill).Clear();
            FlushGroup();
        }
        var dataSize = 12 + _groups * BlockSize * _channels;
        var total = 28 + 52 + dataSize;
        _out.Position = 0;
        _out.Write(BuildHeader(total, _bytesPerChannel * 8, dataSize));
        _out.Position = total;
        _out.Flush();
    }

    private void FlushGroup()
    {
        for (var c = 0; c < _channels; c++) _out.Write(_blocks[c]);
        _fill = 0;
        _groups++;
    }

    private byte[] BuildHeader(long totalSize, long samplesPerChannel, long dataSize)
    {
        var h = new byte[HeaderBytes];
        var s = h.AsSpan();
        Encoding.ASCII.GetBytes("DSD ", s);
        BinaryPrimitives.WriteInt64LittleEndian(s[4..], 28);
        BinaryPrimitives.WriteInt64LittleEndian(s[12..], totalSize);
        BinaryPrimitives.WriteInt64LittleEndian(s[20..], 0);              // metadata pointer
        var f = s[28..];
        Encoding.ASCII.GetBytes("fmt ", f);
        BinaryPrimitives.WriteInt64LittleEndian(f[4..], 52);
        BinaryPrimitives.WriteInt32LittleEndian(f[12..], 1);              // format version
        BinaryPrimitives.WriteInt32LittleEndian(f[16..], 0);              // DSD raw
        BinaryPrimitives.WriteInt32LittleEndian(f[20..], _channels);      // channel type 1 mono / 2 stereo
        BinaryPrimitives.WriteInt32LittleEndian(f[24..], _channels);
        BinaryPrimitives.WriteInt32LittleEndian(f[28..], _rate);
        BinaryPrimitives.WriteInt32LittleEndian(f[32..], 1);              // bits per sample: LSB first
        BinaryPrimitives.WriteInt64LittleEndian(f[36..], samplesPerChannel);
        BinaryPrimitives.WriteInt32LittleEndian(f[44..], BlockSize);
        var d = s[80..];
        Encoding.ASCII.GetBytes("data", d);
        BinaryPrimitives.WriteInt64LittleEndian(d[4..], dataSize);
        return h;
    }
}
