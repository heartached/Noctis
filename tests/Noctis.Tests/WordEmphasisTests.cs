using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Held-note emphasis gate (AMLL shouldEmphasize + an adaptive threshold). ELRC word
/// durations include the silence up to the next word, so a flat "≥1s" gate emphasized
/// nearly every word of a slow song; the gate must instead pick out words that stand
/// out from their own line's cadence, keep AMLL's 2–7 character rule for Latin words,
/// and treat CJK characters (one cell each) on duration alone.
/// </summary>
public class WordEmphasisTests
{
    private static WordTiming W(string text, double startMs, double endMs) => new()
    {
        Text = text,
        Start = TimeSpan.FromMilliseconds(startMs),
        End = TimeSpan.FromMilliseconds(endMs),
    };

    private static LyricLine Line(params WordTiming[] words) => new()
    {
        Timestamp = words[0].Start,
        Text = "t",
        Words = words,
    };

    [Fact]
    public void UniformSlowLine_GetsNoEmphasis()
    {
        // A ballad at ~1.2s per word crossed the old fixed 1s gate on every word —
        // the emphasis fired on nearly every lyric. Words matching their own line's
        // cadence must not count as held.
        var line = Line(
            W("Slow ", 0, 1200), W("songs ", 1200, 2400), W("feel ", 2400, 3600),
            W("like ", 3600, 4800), W("this ", 4800, 6000));
        Assert.All(line.Words!, w => Assert.False(w.IsEmphasis));
    }

    [Fact]
    public void HeldOutlierInAFastLine_GetsEmphasis()
    {
        var line = Line(
            W("You ", 0, 300), W("know ", 300, 600), W("that ", 600, 900),
            W("looove", 900, 2900));
        Assert.False(line.Words![0].IsEmphasis);
        Assert.False(line.Words![1].IsEmphasis);
        Assert.False(line.Words![2].IsEmphasis);
        Assert.True(line.Words![3].IsEmphasis);
    }

    [Fact]
    public void WordsLongerThanSevenChars_NeverEmphasized()
    {
        // AMLL: beyond 7 characters the whole-word glow reads unnatural — and merged
        // multi-syllable words ("beautiful") routinely span >1s at a normal pace.
        var line = Line(
            W("You ", 0, 300), W("know ", 300, 600), W("beautiful", 600, 2600));
        Assert.False(line.Words![2].IsEmphasis);
    }

    [Fact]
    public void SingleLatinCharacter_NeverEmphasized()
    {
        // AMLL requires trimmed length > 1 for non-CJK text.
        var line = Line(W("Go ", 0, 300), W("now ", 300, 600), W("I", 600, 3600));
        Assert.False(line.Words![2].IsEmphasis);
    }

    [Fact]
    public void CjkCharacter_EmphasizedOnDurationAlone()
    {
        // CJK cells are one character each (the parser never merges them); AMLL
        // gates them purely on the ≥1s hold.
        var line = Line(W("君", 0, 300), W("を", 300, 600), W("愛", 600, 2100));
        Assert.False(line.Words![0].IsEmphasis);
        Assert.False(line.Words![1].IsEmphasis);
        Assert.True(line.Words![2].IsEmphasis);
    }

    [Fact]
    public void UnambiguousHold_FiresEvenWhenTheWholeLineIsSlow()
    {
        // The adaptive threshold caps out: a multi-second note is a hold no matter
        // what surrounds it.
        var line = Line(W("Ohh ", 0, 2600), W("ohh ", 2600, 5200), W("ohh", 5200, 7800));
        Assert.All(line.Words!, w => Assert.True(w.IsEmphasis));
    }

    [Fact]
    public void WhitespaceToken_NeverEmphasized()
    {
        var line = Line(W("Hey ", 0, 300), W("   ", 300, 2300));
        Assert.False(line.Words![1].IsEmphasis);
    }

    [Fact]
    public void TrailingSpaces_DontCountTowardLength()
    {
        // "go" carries its ELRC trailing space; the 2–7 rule must see 2 characters.
        var line = Line(W("You ", 0, 300), W("know ", 300, 600), W("go   ", 600, 2600));
        Assert.True(line.Words![2].IsEmphasis);
    }

    [Fact]
    public void HeldDuration_IsResolvedForTheBellEnvelope()
    {
        var line = Line(
            W("You ", 0, 300), W("know ", 300, 600), W("that ", 600, 900),
            W("looove", 900, 2900));
        Assert.Equal(300, line.Words![0].HeldDurationMs, 3);
        Assert.Equal(2000, line.Words![3].HeldDurationMs, 3);
    }

    [Fact]
    public void BackgroundWords_UseTheSameGate()
    {
        var line = new LyricLine
        {
            Timestamp = TimeSpan.Zero,
            Text = "(ooh)",
            BackgroundEndTimestamp = TimeSpan.FromMilliseconds(2300),
            BackgroundWords = new[] { W("ah ", 0, 300), W("oooh", 300, 2300) },
        };
        Assert.False(line.BackgroundWords![0].IsEmphasis);
        Assert.True(line.BackgroundWords![1].IsEmphasis);
    }

    [Fact]
    public void NewWords_RestAtTheInertFutureSentinel()
    {
        // Word-timed lines render their sweep layer even while inactive. Under the
        // straddling band a Progress of 0 means "band half entered at the left
        // edge", so fresh words must rest at the inert sentinel, not at 0.
        Assert.Equal(KaraokeSweep.InertFuture, new WordTiming().Progress);
    }

    [Fact]
    public void WordIndex_SnapsPastAndFutureToTheInertSentinels()
    {
        var line = Line(W("a ", 0, 300), W("b ", 300, 600), W("c", 600, 900));
        line.CurrentWordIndex = 1;
        Assert.Equal(KaraokeSweep.InertPast, line.Words![0].Progress);
        Assert.Equal(KaraokeSweep.InertFuture, line.Words![2].Progress);
    }
}
