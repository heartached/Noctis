using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class EnhancedLrcParserTests
{
    [Fact]
    public void ParseLine_PlainBody_ReturnsTextAndNoWords()
    {
        var (text, words) = EnhancedLrcParser.ParseLine("Hello world");

        Assert.Equal("Hello world", text);
        Assert.Null(words);
    }

    [Fact]
    public void ParseLine_WordTags_StripsTagsFromDisplayText()
    {
        var (text, words) = EnhancedLrcParser.ParseLine("<00:05.41>Hello <00:05.90>world<00:06.40>");

        Assert.Equal("Hello world", text);
        Assert.NotNull(words);
        Assert.Equal(2, words!.Count);
    }

    [Fact]
    public void ParseLine_WordTags_AssignsStartAndEndTimes()
    {
        var (_, words) = EnhancedLrcParser.ParseLine("<00:05.41>Hello <00:05.90>world<00:06.40>");

        Assert.Equal(TimeSpan.FromMilliseconds(5_410), words![0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(5_900), words[0].End);
        Assert.Equal("Hello ", words[0].Text);

        Assert.Equal(TimeSpan.FromMilliseconds(5_900), words[1].Start);
        // Trailing tag with no following text marks the end of the last word.
        Assert.Equal(TimeSpan.FromMilliseconds(6_400), words[1].End);
        Assert.Equal("world", words[1].Text);
    }

    [Fact]
    public void ParseLine_CjkPerCharacterTiming_Parses()
    {
        var (text, words) = EnhancedLrcParser.ParseLine(
            "<00:05.416>夢<00:06.256>を<00:06.406>語<00:06.716>っ<00:07.126>て<00:07.826>");

        Assert.Equal("夢を語って", text);
        Assert.Equal(5, words!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(5_416), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(7_826), words[^1].End);
    }

    [Fact]
    public void ParseLine_EmptyBody_ReturnsEmpty()
    {
        var (text, words) = EnhancedLrcParser.ParseLine("");

        Assert.Equal("", text);
        Assert.Null(words);
    }

    [Fact]
    public void ParseLine_FinalWordWithoutTrailingTag_HasNullEnd()
    {
        var (_, words) = EnhancedLrcParser.ParseLine("<00:01.00>one <00:02.00>two");

        Assert.Equal(2, words!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(2_000), words[0].End);
        Assert.Null(words[1].End);
    }

    [Fact]
    public void ContainsWordTags_DetectsInlineTags()
    {
        Assert.True(EnhancedLrcParser.ContainsWordTags("<00:05.41>Hi"));
        Assert.False(EnhancedLrcParser.ContainsWordTags("plain"));
    }
}
