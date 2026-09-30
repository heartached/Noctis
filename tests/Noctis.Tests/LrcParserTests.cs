using System.Text;
using Noctis.Helpers;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The LRC rules now shared by desktop and phone, and the byte decoder SAF reads go through.</summary>
public class LrcParserTests
{
    [Fact]
    public void Parse_AppliesOffset_SkipsMetadata_SplitsMultiTimestamps_KeepsWords()
    {
        const string lrc = "[ar:Someone]\n[offset:+500]\n[00:01.00][00:03.00]twice\n[00:05.00]<00:05.00>word <00:05.50>tags\n[00:09.00]";
        var lines = LrcParser.Parse(lrc);

        Assert.Equal(3, lines.Count);                                    // metadata, offset and the empty end marker dropped
        Assert.Equal(TimeSpan.FromSeconds(1.5), lines[0].Timestamp);     // +500 ms offset
        Assert.Equal(TimeSpan.FromSeconds(3.5), lines[1].Timestamp);
        Assert.Equal("twice", lines[1].Text);
        Assert.Equal(new[] { "word", "tags" }, lines[2].Words!.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromSeconds(5.5), lines[2].Words![0].Start);
    }

    [Fact]
    public void DesktopForwarder_ReturnsTheSameLines()
    {
        const string lrc = "[00:01.00]a\n[00:02.00]b";
        Assert.Equal(
            LrcParser.Parse(lrc).Select(l => (l.Timestamp, l.Text)),
            Noctis.ViewModels.LyricsViewModel.ParseLrcContent(lrc).Select(l => (l.Timestamp, l.Text)));
    }

    [Fact]
    public void ContainsTimestamp_DetectsLrcStamps()
    {
        Assert.True(LrcParser.ContainsTimestamp("[01:02.03]x"));
        Assert.False(LrcParser.ContainsTimestamp("plain words [chorus]"));
        Assert.False(LrcParser.ContainsTimestamp(null));
    }

    [Fact]
    public void InsertIntroPlaceholder_OnlyWhenFirstLineIsLate()
    {
        var late = LrcParser.Parse("[00:05.00]late");
        LrcParser.InsertIntroPlaceholderIfNeeded(late);
        Assert.True(late[0].IsIntroPlaceholder);

        var early = LrcParser.Parse("[00:01.00]early");
        LrcParser.InsertIntroPlaceholderIfNeeded(early);
        Assert.False(early[0].IsIntroPlaceholder);
    }

    [Fact]
    public void Decode_Utf8WithoutBom_AndUtf16WithBom()
    {
        Assert.Equal("例えば", LyricsTextDecoder.Decode(Encoding.UTF8.GetBytes("例えば")));
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[00:01.00]ok")).ToArray();
        Assert.Equal("[00:01.00]ok", LyricsTextDecoder.Decode(utf16));
    }

    [Fact]
    public void Decode_InvalidUtf8_FallsBackWithoutThrowing()
    {
        // "あい" in Shift-JIS: not valid UTF-8. On Android the ANSI code page lookup may not
        // exist; the decoder must still return something rather than throw or return "".
        var shiftJis = new byte[] { 0x82, 0xA0, 0x82, 0xA2 };
        var text = LyricsTextDecoder.Decode(shiftJis);
        Assert.False(string.IsNullOrEmpty(text));
    }
}
