using System.Text;

namespace Noctis.Services.Loon;

/// <summary>
/// Minimal protobuf wire-format reader/writer.
/// Supports only the types used by the loon protocol: varint, bytes, string, sub-message.
/// </summary>
internal static class LoonProtobuf
{
    // Wire types
    private const int WireVarint = 0;
    private const int WireLengthDelimited = 2;

    #region Reader

    internal ref struct ProtoReader
    {
        private ReadOnlySpan<byte> _buf;
        private int _pos;

        public ProtoReader(ReadOnlySpan<byte> buffer)
        {
            _buf = buffer;
            _pos = 0;
        }

        public bool HasMore => _pos < _buf.Length;

        /// <summary>Reads the next field tag. Returns (fieldNumber, wireType).</summary>
        public (int fieldNumber, int wireType) ReadTag()
        {
            var v = ReadRawVarint();
            return ((int)(v >> 3), (int)(v & 0x7));
        }

        public ulong ReadUInt64() => ReadRawVarint();
        public uint ReadUInt32() => (uint)ReadRawVarint();
        public int ReadInt32() => (int)ReadRawVarint();
        public bool ReadBool() => ReadRawVarint() != 0;

        /// <summary>
        /// Validates a server-supplied length before it is used to slice the buffer.
        /// The raw varint is attacker-controlled: cast straight to int, a value above
        /// int.MaxValue became negative and anything past the end overran, so a truncated
        /// or hostile frame threw IndexOutOfRange/ArgumentOutOfRange out of the parser
        /// instead of a single handled InvalidDataException.
        /// </summary>
        private int ReadLength()
        {
            var raw = ReadRawVarint();
            if (raw > int.MaxValue)
                throw new InvalidDataException($"Length-delimited field too large: {raw}");

            var len = (int)raw;
            if (len < 0 || len > _buf.Length - _pos)
                throw new InvalidDataException(
                    $"Length-delimited field ({len}) runs past the end of the message.");

            return len;
        }

        public string ReadString()
        {
            var len = ReadLength();
            var s = Encoding.UTF8.GetString(_buf.Slice(_pos, len));
            _pos += len;
            return s;
        }

        public byte[] ReadBytes()
        {
            var len = ReadLength();
            var b = _buf.Slice(_pos, len).ToArray();
            _pos += len;
            return b;
        }

        /// <summary>Reads a length-delimited sub-message and returns a reader for it.</summary>
        public ProtoReader ReadSubMessage()
        {
            var len = ReadLength();
            var sub = new ProtoReader(_buf.Slice(_pos, len));
            _pos += len;
            return sub;
        }

        /// <summary>Skips the value for the given wire type.</summary>
        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case WireVarint:
                    ReadRawVarint();
                    break;
                case 1: // 64-bit
                    if (_buf.Length - _pos < 8) throw new InvalidDataException("Truncated 64-bit field.");
                    _pos += 8;
                    break;
                case WireLengthDelimited:
                    _pos += ReadLength();
                    break;
                case 5: // 32-bit
                    if (_buf.Length - _pos < 4) throw new InvalidDataException("Truncated 32-bit field.");
                    _pos += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unknown wire type: {wireType}");
            }
        }

        private ulong ReadRawVarint()
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                // Guarded: an unterminated varint at the end of a truncated frame walked
                // straight off the end of the buffer.
                if (_pos >= _buf.Length)
                    throw new InvalidDataException("Truncated varint at end of message.");
                byte b = _buf[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 64) throw new InvalidDataException("Varint too long");
            }
        }
    }

    #endregion

    #region Writer

    internal sealed class ProtoWriter
    {
        private readonly MemoryStream _ms = new();

        public byte[] ToArray() => _ms.ToArray();

        public void WriteVarintField(int fieldNumber, ulong value)
        {
            WriteTag(fieldNumber, WireVarint);
            WriteRawVarint(value);
        }

        public void WriteStringField(int fieldNumber, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteTag(fieldNumber, WireLengthDelimited);
            WriteRawVarint((ulong)bytes.Length);
            _ms.Write(bytes);
        }

        public void WriteBytesField(int fieldNumber, byte[] value)
        {
            WriteTag(fieldNumber, WireLengthDelimited);
            WriteRawVarint((ulong)value.Length);
            _ms.Write(value);
        }

        public void WriteBytesField(int fieldNumber, ReadOnlySpan<byte> value)
        {
            WriteTag(fieldNumber, WireLengthDelimited);
            WriteRawVarint((ulong)value.Length);
            _ms.Write(value);
        }

        public void WriteSubMessageField(int fieldNumber, Action<ProtoWriter> writeInner)
        {
            var inner = new ProtoWriter();
            writeInner(inner);
            var data = inner.ToArray();
            WriteTag(fieldNumber, WireLengthDelimited);
            WriteRawVarint((ulong)data.Length);
            _ms.Write(data);
        }

        private void WriteTag(int fieldNumber, int wireType)
        {
            WriteRawVarint((ulong)((fieldNumber << 3) | wireType));
        }

        private void WriteRawVarint(ulong value)
        {
            while (value > 0x7F)
            {
                _ms.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            _ms.WriteByte((byte)value);
        }
    }

    #endregion
}
