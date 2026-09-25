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
    public void Parse_AliasedLinesPastCap_StopsAtCap()
    {
        // A 5-byte "- *l" alias repeats a whole line, so a small file can list hundreds of
        // thousands of lines — the lyrics list is not virtualized and realized every one.
        var yaml = new StringBuilder("version: \"1.0\"\nlines:\n  - &l {text: la, start_ms: 1000}\n");
        for (int i = 0; i < EnhancedLrcParser.MaxLyricLines + 50; i++)
            yaml.Append("  - *l\n");

        var (lines, _) = LyricsfileParser.Parse(yaml.ToString());

        Assert.Equal(EnhancedLrcParser.MaxLyricLines, lines!.Count);
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
