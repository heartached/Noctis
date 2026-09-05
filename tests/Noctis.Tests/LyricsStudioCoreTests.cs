using Noctis.Services;
using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

public class LyricsStudioCoreTests
{
    private static RecognizedWord W(string text, double startSec, double durSec = 0.3, float p = 0.9f) =>
        new(text, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(startSec + durSec), p);

    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    [Fact]
    public void Align_PerfectTranscript_LinesStartOnTheirFirstWord()
    {
        var lines = new[] { "Hello world", "This is a test", "Goodbye" };
        var heard = new[]
        {
            W("Hello", 1.0), W("world", 1.4),
            W("This", 5.0), W("is", 5.3), W("a", 5.5), W("test", 5.7),
            W("Goodbye", 9.0),
        };

        var aligned = LyricsAligner.Align(lines, heard);

        Assert.Equal(3, aligned.Count);
        Assert.Equal(S(1.0), aligned[0].Start);
        Assert.Equal(S(5.0), aligned[1].Start);
        Assert.Equal(S(9.0), aligned[2].Start);
        Assert.All(aligned, l => Assert.False(l.Interpolated));
        Assert.All(aligned, l => Assert.True(l.Confidence > 0.9));
        Assert.Equal("world", aligned[0].Words[1].Text);
        Assert.Equal(S(1.4), aligned[0].Words[1].Start);
    }

    [Fact]
    public void Align_ToleratesMisheardWords_AndPunctuation()
    {
        var lines = new[] { "Héllo, world!", "Shine on you crazy diamond" };
        var heard = new[]
        {
            W("hello", 2.0), W("wrld", 2.4),
            W("shine", 6.0), W("on", 6.3), W("you", 6.5), W("crazy", 6.7), W("dimond", 7.1),
        };

        var aligned = LyricsAligner.Align(lines, heard);

        Assert.Equal(S(2.0), aligned[0].Start);
        Assert.Equal(S(6.0), aligned[1].Start);
        Assert.Equal("Héllo, world!", aligned[0].Text);
        Assert.True(aligned[1].Confidence > 0.7);
    }

    [Fact]
    public void Align_LineNobodyHeard_IsInterpolatedBetweenNeighbours()
    {
        var lines = new[] { "First line here", "Mumbled middle", "Third line here" };
        var heard = new[]
        {
            W("first", 1.0), W("line", 1.3), W("here", 1.6),
            W("third", 8.0), W("line", 8.3), W("here", 8.6),
        };

        var aligned = LyricsAligner.Align(lines, heard);

        Assert.True(aligned[1].Interpolated);
        Assert.True(aligned[1].Start >= aligned[0].End);
        Assert.True(aligned[1].End <= aligned[2].Start);
        Assert.Equal(0, aligned[1].Confidence);
        Assert.Equal(2, aligned[1].Words.Count);
    }

    [Fact]
    public void Align_ExtraHeardWords_DoNotDerailLaterLines()
    {
        var lines = new[] { "Yeah", "Take me home" };
        var heard = new[]
        {
            W("uh", 0.5), W("yeah", 1.0), W("oh", 1.5), W("oh", 1.8), W("baby", 2.2),
            W("take", 4.0), W("me", 4.2), W("home", 4.4),
        };

        var aligned = LyricsAligner.Align(lines, heard);

        Assert.Equal(S(1.0), aligned[0].Start);
        Assert.Equal(S(4.0), aligned[1].Start);
    }

    [Fact]
    public void Align_NothingHeard_SpreadsLinesAcrossTheTrack()
    {
        var lines = new[] { "One", "Two", "Three", "Four" };
        var aligned = LyricsAligner.Align(lines, Array.Empty<RecognizedWord>(), TimeSpan.FromSeconds(40));

        Assert.Equal(4, aligned.Count);
        Assert.All(aligned, l => Assert.True(l.Interpolated));
        Assert.Equal(TimeSpan.Zero, aligned[0].Start);
        Assert.Equal(S(10), aligned[1].Start);
        Assert.Equal(S(30), aligned[3].Start);
        Assert.Equal(S(40), aligned[3].End);
    }

    [Fact]
    public void Align_StartsAreStrictlyIncreasing_EvenWhenAnchorsGoBackwards()
    {
        var lines = new[] { "Alpha beta", "Gamma delta" };
        var heard = new[] { W("gamma", 1.0), W("delta", 1.3), W("alpha", 5.0), W("beta", 5.3) };

        var aligned = LyricsAligner.Align(lines, heard);

        Assert.True(aligned[1].Start > aligned[0].Start);
    }

    [Fact]
    public void Align_PartialLine_UnheardWordsAreSpreadInside()
    {
        var lines = new[] { "I want to break free" };
        var heard = new[] { W("I", 3.0, 0.2), W("break", 4.0, 0.2), W("free", 4.5, 0.4) };

        var line = LyricsAligner.Align(lines, heard)[0];

        Assert.Equal(S(3.0), line.Start);
        Assert.Equal(5, line.Words.Count);
        Assert.Equal(S(3.2), line.Words[1].Start);            // "want" starts where "I" ended
        Assert.Equal(S(4.0), line.Words[2].End);              // "to" ends where "break" begins
        Assert.Equal(S(4.9), line.End);
        for (var i = 0; i + 1 < line.Words.Count; i++)
            Assert.True(line.Words[i].End <= line.Words[i + 1].Start);
    }

    [Fact]
    public void Normalize_And_Similarity_BehaveAsExpected()
    {
        Assert.Equal("hello", LyricsAligner.Normalize("Héllo,"));
        Assert.Equal("", LyricsAligner.Normalize("♪ …"));
        Assert.Equal(1.0, LyricsAligner.Similarity("world", "world"));
        Assert.True(LyricsAligner.Similarity("world", "wrld") >= 0.75);
        Assert.True(LyricsAligner.Similarity("diamond", "dimond") >= 0.8);
        Assert.True(LyricsAligner.Similarity("cat", "moon") < 0.45);
    }

    [Fact]
    public void TranscriptLines_BreakOnPause_Punctuation_AndLength()
    {
        var words = new List<RecognizedWord>();
        var t = 0.0;
        for (var i = 0; i < 6; i++) { words.Add(W($"w{i}", t)); t += 0.35; }   // 6 words, no pause
        t += 2.0;                                                              // long pause
        words.Add(W("Next", t)); words.Add(W("phrase", t + 0.3)); words.Add(W("ends.", t + 0.6));
        words.Add(W("Then", t + 1.0)); words.Add(W("more", t + 1.3));
        for (var i = 0; i < 10; i++) { words.Add(W($"x{i}", t + 2.0 + i * 0.3)); }

        var lines = TranscriptLines.Group(words, maxWordsPerLine: 8);

        Assert.Equal("w0 w1 w2 w3 w4 w5", lines[0].Text);
        Assert.Equal("Next phrase ends.", lines[1].Text);
        Assert.StartsWith("Then more", lines[2].Text);
        Assert.All(lines, l => Assert.True(l.Words.Count <= 8));
        Assert.All(lines, l => Assert.True(l.Confidence > 0.8));
    }

    [Fact]
    public void Builders_ProduceLrc_AndElrcThatTheParserReads()
    {
        var line = new AlignedLine("Hello world", S(5.41), S(6.4),
            new[] { new AlignedWord("Hello", S(5.41), S(5.9)), new AlignedWord("world", S(5.9), S(6.4)) }, 1, false);
        var second = new AlignedLine("Again", S(65.017), S(66), new[] { new AlignedWord("Again", S(65.017), S(66)) }, 1, false);

        var lrc = TimedLyricsBuilder.BuildLrc(new[] { second, line });
        Assert.Equal("[00:05.41]Hello world\n[01:05.01]Again", lrc);

        var elrc = TimedLyricsBuilder.BuildElrc(new[] { line });
        Assert.Equal("[00:05.41]<00:05.41>Hello <00:05.90>world<00:06.40>", elrc);

        var (text, words) = EnhancedLrcParser.ParseLine(elrc[(elrc.IndexOf(']') + 1)..]);
        Assert.Equal("Hello world", text.Trim());
        Assert.NotNull(words);
        Assert.Equal(2, words!.Count);

        Assert.Equal("Hello world\nAgain", TimedLyricsBuilder.BuildPlain(new[] { line, second }));
        Assert.Equal("00:00.00", TimedLyricsBuilder.FormatTimestamp(TimeSpan.FromSeconds(-3)));
    }
}
