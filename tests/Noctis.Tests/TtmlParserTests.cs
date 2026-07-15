using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class TtmlParserTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/ns/ttml\"";

    [Fact]
    public void Parse_LineTimedDocument_ReturnsSyncedLines()
    {
        var ttml = $@"<tt {Ns}><body>
            <div>
                <p begin=""0:12.500"" end=""0:15.200"">Hello world</p>
                <p begin=""0:15.200"" end=""0:18.000"">Second line</p>
            </div>
        </body></tt>";

        var (lines, plain) = TtmlParser.Parse(ttml);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal("Hello world", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(12_500), lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(15_200), lines[0].EndTimestamp);
        Assert.True(lines[0].IsSynced);
        Assert.Null(lines[0].Words);
        Assert.Equal("Hello world\nSecond line", plain);
    }

    [Fact]
    public void Parse_WordTimedSpans_ProducesWordTimings()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000"">
                <span begin=""0:10.000"" end=""0:10.500"">Hello</span> <span begin=""0:10.500"" end=""0:11.200"">world</span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.NotNull(words);
        Assert.Equal(2, words!.Count);
        Assert.Equal("Hello ", words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(10_500), words[0].End);
        Assert.Equal("world", words[1].Text);
        Assert.Equal("Hello world", lines[0].Text);
    }

    [Fact]
    public void Parse_SyllableSpansWithoutWhitespace_MergeIntoOneWord()
    {
        // Apple-style syllable timing: no whitespace between spans of the same word.
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000""><span begin=""0:10.000"" end=""0:10.300"">tal</span><span begin=""0:10.300"" end=""0:10.800"">king</span> <span begin=""0:10.800"" end=""0:11.500"">now</span></p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.Equal(2, words!.Count);
        Assert.Equal("talking ", words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(10_800), words[0].End);
        Assert.Equal("now", words[1].Text);
    }

    [Fact]
    public void Parse_BackgroundVocalWrapperSpan_RecursesIntoTimedChildren()
    {
        var ttml = $@"<tt {Ns} xmlns:ttm=""http://www.w3.org/ns/ttml#metadata""><body><div>
            <p begin=""0:20.000"" end=""0:24.000"">
                <span begin=""0:20.000"" end=""0:21.000"">Lead</span> <span ttm:role=""x-bg""><span begin=""0:21.000"" end=""0:22.000"">(echo)</span></span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.Equal(2, words!.Count);
        Assert.Equal("(echo)", words[1].Text.Trim());
        Assert.Equal(TimeSpan.FromMilliseconds(21_000), words[1].Start);
    }

    [Fact]
    public void Parse_MissingWordEnds_BackfillsFromNextStartAndLineEnd()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000""><span begin=""0:10.000"">One</span> <span begin=""0:10.800"">two</span></p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.Equal(TimeSpan.FromMilliseconds(10_800), words![0].End);
        Assert.Equal(TimeSpan.FromMilliseconds(12_000), words[1].End);
    }

    [Fact]
    public void Parse_LinesOutOfOrder_SortsByTimestamp()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:30.000"">Later</p>
            <p begin=""0:10.000"">Earlier</p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Equal("Earlier", lines![0].Text);
        Assert.Equal("Later", lines[1].Text);
    }

    [Fact]
    public void Parse_EmptyOrWhitespaceLines_AreSkipped()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:05.000"" end=""0:06.000""> </p>
            <p begin=""0:10.000"">Real line</p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Single(lines!);
        Assert.Equal("Real line", lines![0].Text);
    }

    [Fact]
    public void Parse_MalformedOrNonTtmlContent_ReturnsNull()
    {
        Assert.Null(TtmlParser.Parse(null).Lines);
        Assert.Null(TtmlParser.Parse("").Lines);
        Assert.Null(TtmlParser.Parse("not xml at all").Lines);
        Assert.Null(TtmlParser.Parse("<html><body><p>web page</p></body></html>").Lines);
        Assert.Null(TtmlParser.Parse($"<tt {Ns}><body></body></tt>").Lines);
    }

    [Theory]
    [InlineData("0:12.500", 12_500)]
    [InlineData("00:01:02.250", 62_250)]
    [InlineData("1:02:03.456", 3_723_456)]
    [InlineData("7.5s", 7_500)]
    [InlineData("1500ms", 1_500)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("12.34", 12_340)]
    public void ParseTime_SupportedFormats_ReturnsExpectedMs(string value, double expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), TtmlParser.ParseTime(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("25f")]
    [InlineData("10t")]
    [InlineData("1:2:3:4")]
    public void ParseTime_UnsupportedFormats_ReturnsNull(string? value)
    {
        Assert.Null(TtmlParser.ParseTime(value));
    }

    [Fact]
    public void Parse_AppleStyleDocument_EndToEnd()
    {
        // Shape matches Apple Music lyric exports (itunes namespace, agents, keys).
        var ttml = @"<tt xmlns=""http://www.w3.org/ns/ttml"" xmlns:ttm=""http://www.w3.org/ns/ttml#metadata"" xmlns:itunes=""http://music.apple.com/lyric-ttml-internal"" itunes:timing=""Word"" xml:lang=""en"">
  <head><metadata><ttm:agent type=""person"" xml:id=""v1""/></metadata></head>
  <body dur=""3:20.416"">
    <div begin=""15.94"" end=""22.198"" itunes:songPart=""Verse"">
      <p begin=""15.94"" end=""18.512"" itunes:key=""L1"" ttm:agent=""v1""><span begin=""15.94"" end=""16.278"">Never</span> <span begin=""16.278"" end=""16.5"">gonna</span> <span begin=""16.5"" end=""16.943"">give</span></p>
    </div>
  </body>
</tt>";

        var (lines, plain) = TtmlParser.Parse(ttml);

        Assert.Single(lines!);
        Assert.Equal("Never gonna give", lines![0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(15_940), lines[0].Timestamp);
        Assert.Equal(3, lines[0].Words!.Count);
        Assert.Equal("Never gonna give", plain);
    }
}
