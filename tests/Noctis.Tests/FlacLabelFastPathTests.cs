using System.Buffers.Binary;
using System.Text;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// The v11 label pass reads a FLAC's label from its VORBIS_COMMENT block alone. Owner 10-08:
/// TagLib# reads every FLAC metadata block's bytes whatever the ReadStyle — measured 11.1 MB of
/// 70.9 MB and 15.1 MB of 88.1 MB on two hi-res files — so a "light" TagLib open still streamed
/// the covers. The fast path must give exactly the full read's label, and hand anything it
/// doesn't fully understand back to TagLib.
/// </summary>
public class FlacLabelFastPathTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly MetadataService _metadata = new();

    public FlacLabelFastPathTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Counts the bytes handed out by Read — what the parser actually pulls off disk.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesRead;
        public CountingStream(Stream inner) => _inner = inner;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
        public override int Read(Span<byte> buffer)
        {
            var n = _inner.Read(buffer);
            BytesRead += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CountingAbstraction : TagLib.File.IFileAbstraction
    {
        public readonly CountingStream Stream;
        public CountingAbstraction(string path)
        {
            Name = path;
            Stream = new CountingStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1));
        }
        public string Name { get; }
        public System.IO.Stream ReadStream => Stream;
        public System.IO.Stream WriteStream => throw new NotSupportedException();
        public void CloseStream(System.IO.Stream stream) { }
    }

    private static CountingStream Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1));

    private static (bool Ok, string Label, long Bytes) Fast(string path)
    {
        using var s = Open(path);
        var ok = ExtendedTagIO.TryReadFlacLabel(s, out var label);
        return (ok, label, s.BytesRead);
    }

    private string FullReadLabel(string path) => _metadata.ReadTrackMetadata(path)?.Label ?? string.Empty;

    // CreateFlac's STREAMINFO-only file with a hand-built VORBIS_COMMENT block after it, so
    // comment keys keep their exact spelling (TagLib# upper-cases keys it writes).
    private string RawFlac(params string[] comments)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.flac");
        File.Move(CreateFlac(_dir), path);
        var original = File.ReadAllBytes(path);

        var block = new List<byte>();
        void U32(int v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); block.AddRange(b); }
        var vendor = Encoding.UTF8.GetBytes("test");
        U32(vendor.Length);
        block.AddRange(vendor);
        U32(comments.Length);
        foreach (var c in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(c);
            U32(bytes.Length);
            block.AddRange(bytes);
        }

        var result = new List<byte>(original[..4]);
        result.Add(0x00);                                  // STREAMINFO, no longer last
        result.AddRange(original[5..42]);
        result.Add(0x84);                                  // last block, VORBIS_COMMENT
        result.Add((byte)(block.Count >> 16));
        result.Add((byte)(block.Count >> 8));
        result.Add((byte)block.Count);
        result.AddRange(block);
        result.AddRange(original[42..]);                   // audio
        File.WriteAllBytes(path, result.ToArray());
        return path;
    }

    private string TaggedFlac(string label, int pictureBytes = 0)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.flac");
        File.Move(CreateFlac(_dir), path);
        using var f = TagLib.File.Create(path);
        ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("LABEL", label);
        if (pictureBytes > 0)
        {
            var art = new byte[pictureBytes];
            new Random(1).NextBytes(art);
            f.Tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(art)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }
            };
        }
        f.Save();
        return path;
    }

    [Fact]
    public void LargeCover_FastPathReadsAFewKb_AndMatchesTheFullRead()
    {
        var path = TaggedFlac("Def Jam", pictureBytes: 12 * 1024 * 1024);

        long tagLibBytes;
        var abstraction = new CountingAbstraction(path);
        using (TagLib.File.Create(abstraction, TagLib.ReadStyle.None | TagLib.ReadStyle.PictureLazy))
            tagLibBytes = abstraction.Stream.BytesRead;
        abstraction.Stream.Dispose();

        var (ok, label, bytes) = Fast(path);
        _output.WriteLine($"file {new FileInfo(path).Length:N0} B; TagLib None|PictureLazy read {tagLibBytes:N0} B; fast path read {bytes:N0} B");

        Assert.True(ok);
        Assert.Equal("Def Jam", label);
        Assert.Equal(FullReadLabel(path), label);
        Assert.Equal(label, _metadata.ReadLabel(path));
        Assert.True(bytes < 4096, $"fast path read {bytes} bytes");
        Assert.True(tagLibBytes > 12 * 1024 * 1024, $"TagLib read {tagLibBytes} bytes");   // why the fast path exists
    }

    [Theory]
    [InlineData("Def Jam", "LABEL=Def Jam")]
    [InlineData("Def Jam", "label=Def Jam")]                              // keys are case-insensitive
    [InlineData("Def Jam", "LABEL=  Def Jam  ")]                          // trimmed
    [InlineData("Def Jam", "LABEL=Def Jam", "LABEL=Second")]              // first value wins
    [InlineData("Warp", "ORGANIZATION=Warp")]                             // TagLib's Xiph publisher
    [InlineData("Warp", "LABEL=   ", "Organization=Warp")]                // blank label falls through
    [InlineData("Warp", "LABEL=Warp", "ORGANIZATION=Other")]
    [InlineData("Ninja Tune", "PUBLISHER=Ninja Tune")]
    [InlineData("Ninja Tune", "ORGANIZATION=", "PUBLISHER=Ninja Tune")]
    [InlineData("Second", "LABEL=", "LABEL=Second")]                      // blank values don't count
    [InlineData("Second", "LABEL=   ", "LABEL=Second")]
    [InlineData("Org", "ORGANIZATION=  ", "ORGANIZATION=Org")]
    [InlineData("y", "=x", "LABEL=y")]
    [InlineData("", " LABEL=sp", "LABEL =sp")]                            // keys are not trimmed
    [InlineData("", "TITLE=Ivy", "NOEQUALSSIGN")]
    [InlineData("")]
    public void FieldOrder_MatchesTheFullRead(string expected, params string[] comments)
    {
        var path = RawFlac(comments);
        var (ok, label, _) = Fast(path);

        Assert.True(ok);
        Assert.Equal(expected, label);
        Assert.Equal(FullReadLabel(path), label);
        Assert.Equal(expected, _metadata.ReadLabel(path));
    }

    [Fact]
    public void Id3v2Prefix_FallsBackToTagLib()
    {
        var path = TaggedFlac("Def Jam");
        var prefix = new TagLib.Id3v2.Tag { Title = "Ivy" }.Render().Data;
        File.WriteAllBytes(path, prefix.Concat(File.ReadAllBytes(path)).ToArray());

        Assert.False(Fast(path).Ok);
        Assert.Equal("Def Jam", FullReadLabel(path));
        Assert.Equal("Def Jam", _metadata.ReadLabel(path));
    }

    [Fact]
    public void GarbageHeader_FallsBackToTagLib()
    {
        var path = TaggedFlac("Def Jam");
        var bytes = File.ReadAllBytes(path);
        bytes[3] = (byte)'X';                                      // "fLaX": not a FLAC we parse

        File.WriteAllBytes(path, bytes);
        Assert.False(Fast(path).Ok);
        Assert.Equal(FullReadLabel(path), _metadata.ReadLabel(path));

        // A block length running past the end of the file.
        var truncated = RawFlac("LABEL=Def Jam");
        var raw = File.ReadAllBytes(truncated);
        raw[43] = 0x7F;
        File.WriteAllBytes(truncated, raw);
        Assert.False(Fast(truncated).Ok);
        Assert.Equal(FullReadLabel(truncated), _metadata.ReadLabel(truncated));
    }

    [Fact]
    public void ApeTrailer_FallsBackToTagLib()
    {
        // No Vorbis LABEL, but an APEv2 "Label" item at the end — ReadLabel reads that too.
        var path = RawFlac("TITLE=Ivy");
        var ape = new TagLib.Ape.Tag();
        ape.SetValue("LABEL", "Ninja Tune");
        File.WriteAllBytes(path, File.ReadAllBytes(path).Concat(ape.Render().Data).ToArray());

        Assert.False(Fast(path).Ok);
        Assert.Equal("Ninja Tune", FullReadLabel(path));
        Assert.Equal("Ninja Tune", _metadata.ReadLabel(path));
    }
}
