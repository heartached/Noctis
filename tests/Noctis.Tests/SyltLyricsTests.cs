using Noctis.Services;
using TagLib.Id3v2;
using Xunit;

namespace Noctis.Tests;

public class SyltLyricsTests
{
    private static Tag TagWith(TimestampFormat format, SynchedTextType type, params SynchedText[] entries)
    {
        var tag = new Tag();
        tag.AddFrame(new SynchronisedLyricsFrame("", "eng", type) { Format = format, Text = entries });
        return tag;
    }

    [Fact]
    public void MillisecondLyrics_BecomeSortedSyncedLines_BlankEntriesDropped()
    {
        var tag = TagWith(TimestampFormat.AbsoluteMilliseconds, SynchedTextType.Lyrics,
            new SynchedText(2500, "second"), new SynchedText(1000, "first"), new SynchedText(1500, "   "));

        var lines = SyltLyrics.ToLines(tag)!;

        Assert.Equal(new[] { "first", "second" }, lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), lines[0].Timestamp);
        Assert.All(lines, l => Assert.True(l.IsSynced));
    }

    [Fact]
    public void MpegFrameStamps_AndNonLyricFrames_AreIgnored()
    {
        Assert.Null(SyltLyrics.ToLines(TagWith(TimestampFormat.AbsoluteMpegFrames, SynchedTextType.Lyrics, new SynchedText(10, "x"))));
        Assert.Null(SyltLyrics.ToLines(TagWith(TimestampFormat.AbsoluteMilliseconds, SynchedTextType.Chord, new SynchedText(10, "Am"))));
        Assert.Null(SyltLyrics.ToLines(null));
    }

    [Fact]
    public void Read_NonAudioStream_ReturnsNull()
    {
        Assert.Null(SyltLyrics.Read(new MemoryStream(new byte[] { 1, 2, 3 }), "noise.mp3"));
    }

    [Fact]
    public void Read_Mp3WithSyltFrame_ReturnsItsLines()
    {
        var tag = TagWith(TimestampFormat.AbsoluteMilliseconds, SynchedTextType.Lyrics,
            new SynchedText(2500, "second"), new SynchedText(1000, "first"));

        var lines = SyltLyrics.Read(new MemoryStream(BuildMp3Bytes(tag)), "song.mp3")!;

        Assert.Equal(new[] { "first", "second" }, lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), lines[1].Timestamp);
    }

    /// <summary>
    /// Renders an ID3v2 tag and appends synthetic MPEG-1 Layer III frames (128 kbps / 44.1 kHz,
    /// no padding: header FF FB 90 64 followed by a 413-byte zero payload, 417 bytes total) so
    /// TagLib's MPEG reader accepts the stream as a real audio file, exercising the actual
    /// TagLib.File.Create → GetTag(Id3v2) → ToLines pipeline that Read() drives.
    /// </summary>
    private static byte[] BuildMp3Bytes(Tag tag)
    {
        var tagBytes = tag.Render().Data;
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x64;

        using var stream = new MemoryStream();
        stream.Write(tagBytes, 0, tagBytes.Length);
        for (var i = 0; i < 40; i++) stream.Write(frame, 0, frame.Length);
        return stream.ToArray();
    }
}
