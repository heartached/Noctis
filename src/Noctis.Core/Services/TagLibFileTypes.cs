using System.Runtime.CompilerServices;

namespace Noctis.Services;

/// <summary>
/// Extensions the library takes (<see cref="MetadataService.SupportedExtensions"/>) that TagLib#
/// has no reader for by name: it picks the reader from the extension ("taglib/alac") and threw
/// UnsupportedFormatException, so the scan skipped every .alac and .aifc song without a word
/// and Edit Info could not save them.
/// </summary>
internal static class TagLibFileTypes
{
    /// <summary>Runs once when Noctis.Core loads, before any of its code opens a file.</summary>
    [ModuleInitializer]
    internal static void Register()
    {
        TagLib.File.AddFileTypeResolver(static (abstraction, mimetype, style) => mimetype switch
        {
            // Apple Lossless in an MP4 container, named for its codec.
            "taglib/alac" => new TagLib.Mpeg4.File(abstraction, style),
            // AIFF-C: the AIFF layout (FORM, COMM, an "ID3 " chunk) with form type "AIFC", which
            // TagLib#'s AIFF reader refuses ("File form type is not AIFF").
            "taglib/aifc" => new TagLib.Aiff.File(new AifcAsAiff(abstraction), style),
            _ => null!,
        });
    }

    /// <summary>
    /// Saves an ID3v2.2 tag as ID3v2.3, right before a TagLib# save. TagLib# gives an untagged
    /// AIFF a v2.2 tag (Apple writes v2.2 too), and v2.2 has no sort, lyricist (TEXT) or other
    /// later frames: they were dropped on render, so those Edit Info fields never reached an
    /// AIFF file (owner 11-25 audit). TagLib# reads v2.2 frames into their later ids, so
    /// nothing already in the tag is lost.
    /// </summary>
    internal static void UpgradeId3v22(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag { Version: < 3 } id3)
            id3.Version = 3;
    }

    /// <summary>
    /// Sets an AIFF / AIFF-C file's FORM size to its length after a TagLib# save. TagLib# adds
    /// the first "ID3 " chunk at the end of the file but updates the FORM size only for a tag
    /// that sits inside it, so a song tagged for the first time claimed its old length and
    /// other players and taggers stopped reading before the tags. No-op for any other file.
    /// </summary>
    internal static void RepairAiffFormSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            Span<byte> head = stackalloc byte[12];
            if (stream.Length < 12 || stream.Read(head) != 12) return;
            if (!head[..4].SequenceEqual("FORM"u8)) return;
            var type = head[8..12];
            if (!type.SequenceEqual("AIFF"u8) && !type.SequenceEqual("AIFC"u8)) return;
            var size = stream.Length - 8;
            if (size > uint.MaxValue) return;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[4..8]) == size) return;
            Span<byte> fixedSize = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fixedSize, (uint)size);
            stream.Position = 4;
            stream.Write(fixedSize);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The file with its form type read as "AIFF". Writes go through untouched, and
    /// TagLib# never rewrites the form type, so the file stays AIFF-C on disk.</summary>
    private sealed class AifcAsAiff(TagLib.File.IFileAbstraction inner) : TagLib.File.IFileAbstraction
    {
        public string Name => inner.Name;
        public Stream ReadStream => new FormTypeStream(inner.ReadStream);
        public Stream WriteStream => new FormTypeStream(inner.WriteStream);
        public void CloseStream(Stream stream) => inner.CloseStream(stream is FormTypeStream f ? f.Inner : stream);
    }

    private sealed class FormTypeStream(Stream inner) : Stream
    {
        private const int FormTypeOffset = 8;
        private static readonly byte[] Aiff = "AIFF"u8.ToArray();

        public Stream Inner { get; } = inner;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var start = Inner.Position;
            var read = Inner.Read(buffer, offset, count);
            Patch(buffer.AsSpan(offset, read), start);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var start = Inner.Position;
            var read = Inner.Read(buffer);
            Patch(buffer[..read], start);
            return read;
        }

        private static void Patch(Span<byte> data, long start)
        {
            for (var i = 0; i < Aiff.Length; i++)
            {
                var at = FormTypeOffset + i - start;
                if (at >= 0 && at < data.Length) data[(int)at] = Aiff[i];
            }
        }

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }
        public override void Flush() => Inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => Inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => Inner.Write(buffer);
    }
}
