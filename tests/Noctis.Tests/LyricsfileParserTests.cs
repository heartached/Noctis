using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class LyricsfileParserTests
{
    [Fact]
    public void Parse_WordTimedLine_KeepsWordTiming()
    {
        const string yaml = """
            version: "1.0"
            lines:
              - text: Hello world
                start_ms: 1000
                end_ms: 2000
                words:
                  - {text: "Hello ", start_ms: 1000, end_ms: 1500}
                  - {text: world, start_ms: 1500, end_ms: 2000}
            """;

        var (lines, _) = LyricsfileParser.Parse(yaml);

        var line = Assert.Single(lines!);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(2, line.Words!.Count);
    }

    [Fact]
    public void Parse_LinesPastCap_StopsAtCap()
    {
        // The lyrics list is not virtualized and realizes every line.
        var yaml = new StringBuilder("version: \"1.0\"\nlines:\n");
        for (int i = 0; i < EnhancedLrcParser.MaxLyricLines + 50; i++)
            yaml.Append("  - {text: la, start_ms: 1000}\n");

        var (lines, _) = LyricsfileParser.Parse(yaml.ToString());

        Assert.Equal(EnhancedLrcParser.MaxLyricLines, lines!.Count);
    }

    [Theory]
    // A 5-byte "- *l" repeats a whole line: one 512-word line x 3000 stayed under both caps.
    [InlineData("lines:\n  - &l {text: la, start_ms: 1000}\n  - *l\n")]
    // "text: *t" repeats a whole string on every line.
    [InlineData("lines:\n  - text: &t la\n    start_ms: 1000\n  - text: *t\n    start_ms: 2000\n")]
    // Refused even where the value is ignored.
    [InlineData("lines:\n  - {text: la, start_ms: 1000}\nplain: &p la\nx: *p\n")]
    public void Parse_AnyAlias_IsRejected(string body)
    {
        var (lines, plain) = LyricsfileParser.Parse("version: \"1.0\"\n" + body);

        Assert.Null(lines);
        Assert.Null(plain);
    }

    [Fact]
    public void Parse_AnchorWithoutAlias_StillParses()
    {
        var (lines, _) = LyricsfileParser.Parse("version: \"1.0\"\nlines:\n  - &l {text: la, start_ms: 1000}\n");

        Assert.Equal("la", Assert.Single(lines!).Text);
    }

    [Fact]
    public void Parse_LineWithMoreWordsThanCap_KeepsTextDropsWordTiming()
    {
        var yaml = new StringBuilder("version: \"1.0\"\nlines:\n  - text: long line\n    start_ms: 1000\n    words:\n");
        for (int i = 0; i <= EnhancedLrcParser.MaxWordsPerLine; i++)
            yaml.Append("      - {text: \"w \", start_ms: 1000}\n");

        var (lines, _) = LyricsfileParser.Parse(yaml.ToString());

        var line = Assert.Single(lines!);
        Assert.Equal("long line", line.Text);
        Assert.Null(line.Words);
    }
}
