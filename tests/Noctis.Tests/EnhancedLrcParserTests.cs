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
    public void ParseLine_SyllableTags_MergeIntoWholeWords()
    {
        // "tal" + "king " are syllable tags of one word; they must merge into a
        // single cell or the karaoke WrapPanel can line-break mid-word.
        var (text, words) = EnhancedLrcParser.ParseLine(
            "<00:01.00>tal<00:01.40>king <00:02.00>false<00:02.80>");

        Assert.Equal("talking false", text);
        Assert.Equal(2, words!.Count);
        Assert.Equal("talking ", words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2_000), words[0].End);
        Assert.Equal("false", words[1].Text);
    }

    [Fact]
    public void ParseLine_LeadingSpaceTokens_ShiftSpaceToPreviousWord()
    {
        // "<t>word<t> word" files put the space at the START of each token; the sweep
        // overlay only trims trailing space, so a leading space makes the edge cross
        // invisible whitespace at the start of every word — a per-word stall.
        var (text, words) = EnhancedLrcParser.ParseLine("<00:01.00>Hello<00:02.00> world<00:03.00>");

        Assert.Equal("Hello world", text);
        Assert.Equal(2, words!.Count);
        Assert.Equal("Hello ", words[0].Text);
        Assert.Equal("world", words[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2_000), words[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3_000), words[1].End);
    }

    [Fact]
    public void ParseLine_WhitespaceOnlyToken_DroppedWithoutLosingGap()
    {
        var (_, words) = EnhancedLrcParser.ParseLine("<00:01.00>one<00:01.50> <00:02.00>two<00:02.50>");

        Assert.Equal(2, words!.Count);
        Assert.Equal("one ", words[0].Text);
        // The gap 1.5s→2.0s stays a real pause: "one" still ends at its own tag.
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), words[0].End);
        Assert.Equal("two", words[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2_000), words[1].Start);
    }

    [Fact]
    public void ParseLine_LeadingSpaceOnFirstToken_Trimmed()
    {
        var (text, words) = EnhancedLrcParser.ParseLine("<00:01.00> Hello <00:02.00>world<00:03.00>");

        Assert.Equal("Hello world", text);
        Assert.Equal("Hello ", words![0].Text);
    }

    [Fact]
    public void ParseLine_LeadingSpaceSyllables_StillMergeIntoWholeWords()
    {
        // Leading-space shift must not break syllable merging.
        var (_, words) = EnhancedLrcParser.ParseLine("<00:01.00>tal<00:01.40>king<00:02.00> false<00:02.80>");

        Assert.Equal(2, words!.Count);
        Assert.Equal("talking ", words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2_000), words[0].End);
        Assert.Equal("false", words[1].Text);
    }

    [Fact]
    public void ContainsWordTags_DetectsInlineTags()
    {
        Assert.True(EnhancedLrcParser.ContainsWordTags("<00:05.41>Hi"));
        Assert.False(EnhancedLrcParser.ContainsWordTags("plain"));
    }

    private static Noctis.Models.LyricLine WordLine(string text, double startSec, params (string Text, double Start, double End)[] words)
    {
        var line = new Noctis.Models.LyricLine
        {
            Timestamp = TimeSpan.FromSeconds(startSec),
            Text = text,
        };
        line.Words = words.Select(w => new Noctis.Models.WordTiming
        {
            Text = w.Text,
            Start = TimeSpan.FromSeconds(w.Start),
            End = TimeSpan.FromSeconds(w.End),
        }).ToList();
        return line;
    }

    [Fact]
    public void FoldBackgroundLines_ParenthesizedLine_AttachesToPreviousLine()
    {
        var main = WordLine("Wait for me", 10, ("Wait ", 10, 10.5), ("for ", 10.5, 11), ("me", 11, 11.5));
        var adlib = WordLine("(I will)", 12, ("(I ", 12, 13), ("will)", 13, 14));
        var lines = new List<Noctis.Models.LyricLine> { main, adlib };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Single(lines);
        Assert.True(main.HasBackgroundWords);
        Assert.Equal(2, main.BackgroundWords!.Count);
        Assert.Equal(TimeSpan.FromSeconds(14), main.BackgroundEndTimestamp);
    }

    [Fact]
    public void FoldBackgroundLines_ConsecutiveAdlibs_JoinWithSpaceSeam()
    {
        var main = WordLine("Hey", 10, ("Hey", 10, 10.5));
        var bg1 = WordLine("(Ooh)", 11, ("(Ooh)", 11, 12));
        var bg2 = WordLine("(Ahh)", 12, ("(Ahh)", 12, 13));
        var lines = new List<Noctis.Models.LyricLine> { main, bg1, bg2 };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Single(lines);
        var bg = main.BackgroundWords!;
        Assert.Equal(2, bg.Count);
        Assert.Equal("(Ooh) ", bg[0].Text);
        Assert.Equal("(Ahh)", bg[1].Text);
    }

    [Fact]
    public void FoldBackgroundLines_LeadingAdlib_KeepsItsLineSlot()
    {
        // A fully-parenthesized FIRST line has no preceding main line to fold into —
        // it must keep its slot as a normal line, not vanish or self-extract to nothing.
        var leading = WordLine("(Yeah)", 1, ("(Yeah)", 1, 2));
        var main = WordLine("Hello", 3, ("Hello", 3, 4));
        var lines = new List<Noctis.Models.LyricLine> { leading, main };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Equal(2, lines.Count);
        Assert.False(leading.HasBackgroundWords);
        Assert.Single(leading.Words!);
    }

    [Fact]
    public void FoldBackgroundLines_MultipleInlineRuns_AllExtracted()
    {
        var mixed = WordLine("(Hey) yeah (hey)", 3, ("(Hey) ", 3, 4), ("yeah ", 4, 5), ("(hey)", 5, 6));
        var lines = new List<Noctis.Models.LyricLine> { mixed };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Single(mixed.Words!);
        Assert.Equal("yeah", mixed.Words![0].Text.TrimEnd());
        var bg = mixed.BackgroundWords!;
        Assert.Equal(2, bg.Count);
        Assert.Equal("(Hey) ", bg[0].Text);
        Assert.Equal("(hey)", bg[1].Text);
    }

    [Fact]
    public void FoldBackgroundLines_InlineParenRun_ExtractedToBackground()
    {
        // Adlibs embedded inline: "late at night (I will wait)" — the paren run splits
        // into the background layer; the main words keep their own timings.
        var line = WordLine("Late at night (I will wait)", 10,
            ("Late ", 10, 10.4), ("at ", 10.4, 10.8), ("night ", 10.8, 11.2),
            ("(I ", 11.2, 11.6), ("will ", 11.6, 12.0), ("wait)", 12.0, 12.5));
        var lines = new List<Noctis.Models.LyricLine> { line };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Single(lines);
        Assert.Equal(3, line.Words!.Count);
        Assert.Equal("night", line.Words![2].Text.TrimEnd());
        Assert.True(line.HasBackgroundWords);
        Assert.Equal(3, line.BackgroundWords!.Count);
        Assert.Equal("(I ", line.BackgroundWords![0].Text);
        Assert.Equal("wait)", line.BackgroundWords![2].Text);
        Assert.Equal(TimeSpan.FromSeconds(12.5), line.BackgroundEndTimestamp);
    }

    [Fact]
    public void FoldBackgroundLines_UnmatchedInlineParen_LeftAlone()
    {
        var line = WordLine("Hey (oh no", 10,
            ("Hey ", 10, 10.5), ("(oh ", 10.5, 11), ("no", 11, 11.5));
        var lines = new List<Noctis.Models.LyricLine> { line };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Equal(3, line.Words!.Count);
        Assert.False(line.HasBackgroundWords);
    }

    [Fact]
    public void FoldBackgroundLines_InlineRunPlusFollowingParenLine_MergeInBackground()
    {
        var main = WordLine("Go (yeah)", 10, ("Go ", 10, 10.5), ("(yeah)", 10.5, 11));
        var adlib = WordLine("(Ooh)", 11.5, ("(Ooh)", 11.5, 12.5));
        var lines = new List<Noctis.Models.LyricLine> { main, adlib };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Single(lines);
        Assert.Single(main.Words!);
        var bg = main.BackgroundWords!;
        Assert.Equal(2, bg.Count);
        Assert.Equal("(yeah) ", bg[0].Text);
        Assert.Equal("(Ooh)", bg[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(12.5), main.BackgroundEndTimestamp);
    }

    [Fact]
    public void FoldBackgroundLines_LineLevelParenthesizedLine_NotFolded()
    {
        // Only word-timed adlib lines fold; plain line-level lines keep their slot.
        var main = WordLine("Hello", 10, ("Hello", 10, 11));
        var plain = new Noctis.Models.LyricLine { Timestamp = TimeSpan.FromSeconds(12), Text = "(Whoa)" };
        var lines = new List<Noctis.Models.LyricLine> { main, plain };

        EnhancedLrcParser.FoldBackgroundLines(lines);

        Assert.Equal(2, lines.Count);
        Assert.False(main.HasBackgroundWords);
    }
}
