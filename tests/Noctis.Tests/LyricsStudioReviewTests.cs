using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class LyricsStudioReviewTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    private static AlignedLine WordLine() => new("Hello big world", S(1), S(2.5),
        new[] { new AlignedWord("Hello", S(1), S(1.5)), new AlignedWord("big", S(1.5), S(2)), new AlignedWord("world", S(2), S(2.5)) }, 0.9, false);

    private static AlignedLine LineOnly() => new("Hello big world", S(10), S(13), Array.Empty<AlignedWord>(), 1, false);

    [Fact]
    public void LineLevelInput_SpreadsWordsButExportsLineLevel()
    {
        var line = new ReviewLine(LineOnly());

        Assert.Equal(3, line.Words.Count);
        Assert.False(line.HasWordTimings);
        Assert.Equal(S(10), line.Start);
        Assert.Equal(S(11), line.Words[1].Start);
        Assert.Equal(S(13), line.End);

        var exported = line.ToAlignedLine();
        Assert.Empty(exported.Words);
        Assert.Equal("Hello big world", exported.Text);
        Assert.Equal(S(10), exported.Start);
    }

    [Fact]
    public void WordLevelInput_RoundTrips_EndIsNextWordStart()
    {
        var line = new ReviewLine(WordLine());

        Assert.True(line.HasWordTimings);
        Assert.Equal(S(1.5), line.Words[0].End);
        Assert.Equal(S(2.5), line.Words[2].End);

        var exported = line.ToAlignedLine();
        Assert.Equal(3, exported.Words.Count);
        Assert.Equal(S(1.5), exported.Words[1].Start);
        Assert.Equal(S(2.5), exported.Words[2].End);
        Assert.Equal("[00:01.00]<00:01.00>Hello <00:01.50>big <00:02.00>world<00:02.50>", TimedLyricsBuilder.BuildElrc(new[] { exported }));
    }

    [Fact]
    public void EditText_SameWordCount_KeepsTimes_DifferentCountSpreads_ThenRestores()
    {
        var line = new ReviewLine(WordLine());

        line.Text = "Hello bad world";
        Assert.Equal("bad", line.Words[1].Text);
        Assert.Equal(S(1.5), line.Words[1].Start);

        line.Text = "Hello world";
        Assert.Equal(2, line.Words.Count);
        Assert.Equal(S(1), line.Words[0].Start);
        Assert.Equal(S(1.75), line.Words[1].Start); // spread over 1.0–2.5

        line.Text = "Hello big world";
        Assert.Equal(S(1.5), line.Words[1].Start); // baseline restored
        Assert.Equal(S(2), line.Words[2].Start);
    }

    [Fact]
    public void NudgeWord_MovesOnlyThatWord_AndClampsBetweenNeighbours()
    {
        var line = new ReviewLine(WordLine());
        var changed = 0;
        line.Changed += () => changed++;

        line.NudgeWord(line.Words[1], TimeSpan.FromMilliseconds(100));
        Assert.Equal(S(1.6), line.Words[1].Start);
        Assert.Equal(S(1), line.Words[0].Start);
        Assert.Equal(S(2), line.Words[2].Start);
        Assert.Equal(S(1), line.Start);
        Assert.Equal(1, changed);

        // Cannot pass the next word.
        line.NudgeWord(line.Words[1], TimeSpan.FromSeconds(5));
        Assert.Equal(S(2) - ReviewLine.MinWordGap, line.Words[1].Start);

        // Cannot pass the previous word.
        line.NudgeWord(line.Words[1], TimeSpan.FromSeconds(-5));
        Assert.Equal(S(1) + ReviewLine.MinWordGap, line.Words[1].Start);

        // Nudging the first word moves the derived line start.
        line.NudgeWord(line.Words[0], TimeSpan.FromMilliseconds(-200));
        Assert.Equal(S(0.8), line.Start);
    }

    [Fact]
    public void NudgeOrTapOnSpreadLine_TurnsItWordLevel()
    {
        var line = new ReviewLine(LineOnly());
        Assert.False(line.HasWordTimings);

        line.SetWordStart(line.Words[1], S(11.4), tapped: true);

        Assert.True(line.HasWordTimings);
        Assert.True(line.Words[1].IsTapped);
        Assert.Equal(3, line.ToAlignedLine().Words.Count);
        Assert.Equal(S(11.4), line.ToAlignedLine().Words[1].Start);
    }

    [Fact]
    public void Shift_MovesEveryWordAndTheEnd_ClampedAtZero()
    {
        var line = new ReviewLine(WordLine());

        line.Shift(TimeSpan.FromSeconds(-0.5));
        Assert.Equal(S(0.5), line.Start);
        Assert.Equal(S(1.5), line.Words[2].Start);
        Assert.Equal(S(2), line.End);

        line.Shift(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, line.Start);
        Assert.Equal(S(1), line.Words[2].Start);
    }

    [Fact]
    public void TapWord_PushesLaterWordsForward_AndStretchesTheEnd()
    {
        var line = new ReviewLine(LineOnly()); // spread: 10, 11, 12; end 13

        // Tapping the second word later than the third's old time pushes the third along.
        line.TapWord(1, S(12.5));
        Assert.Equal(S(12.5), line.Words[1].Start);
        Assert.Equal(S(12.5) + ReviewLine.MinWordGap, line.Words[2].Start);
        Assert.True(line.Words[1].IsTapped);
        Assert.True(line.HasWordTimings);

        // Tapping the last word past the line end stretches the end.
        line.TapWord(2, S(14));
        Assert.Equal(S(14), line.Words[2].Start);
        Assert.True(line.End >= S(14) + ReviewLine.MinWordGap);

        // A tap cannot land before the previous word.
        line.TapWord(2, S(5));
        Assert.Equal(S(12.5) + ReviewLine.MinWordGap, line.Words[2].Start);

        line.ClearTapMarks();
        Assert.All(line.Words, w => Assert.False(w.IsTapped));
        Assert.True(line.HasWordTimings); // marks are cosmetic, the times stay
    }

    [Fact]
    public void EditingAWordDirectly_UpdatesLineText()
    {
        var line = new ReviewLine(WordLine());
        line.Words[2].Text = "girl";
        Assert.Equal("Hello big girl", line.Text);
        Assert.Equal(S(2), line.Words[2].Start);
    }
}

public class LyricsAlignerAnchoredTests
{
    private static RecognizedWord W(string text, double startSec, double durSec = 0.3) =>
        new(text, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(startSec + durSec), 0.9f);
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    [Fact]
    public void AlignWithinLines_RepeatedChorus_StaysInItsOwnWindow()
    {
        var lines = new[] { "Hello world", "Hello world" };
        var starts = new[] { S(1), S(30) };
        var heard = new[] { W("Hello", 1.0), W("world", 1.4), W("Hello", 30.0), W("world", 30.4) };

        var aligned = LyricsAligner.AlignWithinLines(lines, starts, heard, S(40));

        Assert.Equal(2, aligned.Count);
        Assert.Equal(S(1), aligned[0].Start);
        Assert.Equal(S(30), aligned[1].Start);
        Assert.Equal(S(30.4), aligned[1].Words[1].Start);
        Assert.All(aligned, l => Assert.False(l.Interpolated));
    }

    [Fact]
    public void AlignWithinLines_UnheardLine_SpreadsInsideItsOwnWindow()
    {
        var lines = new[] { "Hello world", "Mumble mumble here", "Goodbye" };
        var starts = new[] { S(1), S(5), S(9) };
        var heard = new[] { W("Hello", 1.0), W("world", 1.4), W("Goodbye", 9.1) };

        var aligned = LyricsAligner.AlignWithinLines(lines, starts, heard, S(12));

        Assert.True(aligned[1].Interpolated);
        Assert.Equal(S(5), aligned[1].Start);
        Assert.InRange(aligned[1].Words[2].Start, S(5), S(9));
        Assert.True(aligned[1].End <= S(9));
        Assert.Equal(S(9.1), aligned[2].Start);
    }

    [Fact]
    public void AlignWithinLines_WrongWordInWindowIsIgnored_LineKeepsItsStart()
    {
        var lines = new[] { "Sing along", "Second line" };
        var starts = new[] { S(2), S(6) };
        // A heard "along" far outside the first window must not pull the line there.
        var heard = new[] { W("Sing", 2.0), W("along", 20.0), W("Second", 6.0), W("line", 6.3) };

        var aligned = LyricsAligner.AlignWithinLines(lines, starts, heard, S(30));

        Assert.Equal(S(2), aligned[0].Start);
        Assert.True(aligned[0].Words[1].Start < S(6));
    }
}
