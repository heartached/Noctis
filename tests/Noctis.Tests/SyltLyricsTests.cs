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
}
