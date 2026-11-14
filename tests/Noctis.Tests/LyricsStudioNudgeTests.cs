using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Studio's line and word nudges (Discord "Manual lyrics editing", 10-14): the pure rules
/// in <see cref="TimingNudge"/> — clamp, never push; 0:00 and the song's end; words inside
/// their line and in order; lines never change order — and their use by a review line.
/// </summary>
public class LyricsStudioNudgeTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    private static readonly TimeSpan Song = S(180);

    /// <summary>Three words at 10.0 / 10.5 / 11.0 s, line end 11.5 s.</summary>
    private static NudgeLine Line() => new(new[] { S(10), S(10.5), S(11) }, S(11.5));

    private static NudgeLine Apply(NudgeLine line, int word, NudgeEdge edge, int deltaMs,
        double? previous = 5, double? next = 15, TimeSpan? duration = null, bool spread = false) =>
        TimingNudge.Apply(line, word, edge, Ms(deltaMs), previous is { } p ? S(p) : null, next is { } n ? S(n) : null, duration ?? Song, spread);

    // ── Whole line ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(100)]
    [InlineData(-100)]
    public void WholeLine_MovesEveryWordAndTheEnd_ByTheStep(int step)
    {
        var moved = Apply(Line(), -1, NudgeEdge.Whole, step);
        Assert.Equal(new[] { S(10) + Ms(step), S(10.5) + Ms(step), S(11) + Ms(step) }, moved.WordStarts);
        Assert.Equal(S(11.5) + Ms(step), moved.End);
    }

    [Fact]
    public void WholeLine_StopsAtZero_WhenItIsTheFirstLine()
    {
        var line = new NudgeLine(new[] { Ms(40), Ms(300) }, Ms(600));
        var moved = Apply(line, -1, NudgeEdge.Whole, -100, previous: null);
        Assert.Equal(new[] { TimeSpan.Zero, Ms(260) }, moved.WordStarts);
        Assert.Equal(Ms(560), moved.End);
        // At 0:00 an earlier nudge does nothing.
        Assert.Same(moved, Apply(moved, -1, NudgeEdge.Whole, -10, previous: null));
    }

    [Fact]
    public void WholeLine_StopsJustAfterThePreviousLine_AndJustBeforeTheNext()
    {
        var earlier = Apply(Line(), -1, NudgeEdge.Whole, -100, previous: 9.95);
        Assert.Equal(S(9.96), earlier.WordStarts[0]);
        var later = Apply(Line(), -1, NudgeEdge.Whole, 100, next: 10.05);
        Assert.Equal(Ms(10040), later.WordStarts[0]);
    }

    [Fact]
    public void WholeLine_StopsWhenItsEndReachesTheSongsEnd()
    {
        var moved = Apply(Line(), -1, NudgeEdge.Whole, 100, next: null, duration: S(11.55));
        Assert.Equal(S(11.55), moved.End);
        Assert.Equal(S(10.05), moved.WordStarts[0]);
    }

    [Fact]
    public void UnknownSongLength_HasNoUpperLimit()
    {
        var moved = Apply(Line(), -1, NudgeEdge.Whole, 100, next: null, duration: TimeSpan.Zero);
        Assert.Equal(S(11.6), moved.End);
    }

    [Fact]
    public void ALineAlreadyPastALimit_IsNotJumpedTheOtherWay()
    {
        // A file with crossed lines: this one starts before the previous line.
        var moved = Apply(Line(), -1, NudgeEdge.Whole, -10, previous: 10.2);
        Assert.Equal(S(10), moved.WordStarts[0]);
        // Later is still allowed: it moves toward the valid range.
        Assert.Equal(S(10.01), Apply(Line(), -1, NudgeEdge.Whole, 10, previous: 10.2).WordStarts[0]);
    }

    // ── Line edges ──────────────────────────────────────────────────────────

    [Fact]
    public void LineStart_MovesOnlyTheFirstWord_OfATimedLine_AndStaysBeforeTheSecond()
    {
        var moved = Apply(Line(), -1, NudgeEdge.Start, 100);
        Assert.Equal(new[] { S(10.1), S(10.5), S(11) }, moved.WordStarts);
        Assert.Equal(S(11.5), moved.End);
        var far = new NudgeLine(new[] { S(10), S(10.05) }, S(11));
        Assert.Equal(Ms(10040), Apply(far, -1, NudgeEdge.Start, 100).WordStarts[0]);
    }

    [Fact]
    public void LineStart_OfASpreadLine_ReSpreadsItsWords()
    {
        var moved = Apply(Line(), -1, NudgeEdge.Start, -100, spread: true);
        Assert.Equal(S(9.9), moved.WordStarts[0]);
        Assert.Equal(S(9.9) + (S(11.5) - S(9.9)) / 3, moved.WordStarts[1]);
        Assert.Equal(S(11.5), moved.End);
    }

    [Fact]
    public void LineEnd_StaysAfterItsLastWord_AndInsideTheSong_ButMayRunIntoTheNextLine()
    {
        Assert.Equal(S(11.01), Apply(Line(), -1, NudgeEdge.End, -1000).End);
        Assert.Equal(S(11.52), Apply(Line(), -1, NudgeEdge.End, 100, duration: S(11.52)).End);
        // Overlapping lines are real (duets): the end is not held before the next line's start.
        Assert.Equal(S(11.6), Apply(Line(), -1, NudgeEdge.End, 100, next: 11.55).End);
    }

    [Fact]
    public void LineEnd_OfASpreadLine_ReSpreadsItsWords_AndKeepsEachOne10ms()
    {
        var moved = Apply(Line(), -1, NudgeEdge.End, -5000, spread: true);
        Assert.Equal(S(10.03), moved.End);
        Assert.Equal(new[] { S(10), S(10.01), S(10.02) }, moved.WordStarts);
    }

    // ── Words ───────────────────────────────────────────────────────────────

    [Fact]
    public void WordStart_StaysBetweenItsNeighbours_ClampedNotPushed()
    {
        Assert.Equal(S(10.51), Apply(Line(), 2, NudgeEdge.Start, -1000).WordStarts[2]);
        var later = Apply(Line(), 1, NudgeEdge.Start, 1000);
        Assert.Equal(S(10.99), later.WordStarts[1]);
        Assert.Equal(S(11), later.WordStarts[2]); // the neighbour did not move
    }

    [Fact]
    public void FirstWordStart_IsTheLineStart_SoItStaysAfterThePreviousLine()
    {
        Assert.Equal(S(5.01), Apply(Line(), 0, NudgeEdge.Start, -10000).WordStarts[0]);
        Assert.Equal(TimeSpan.Zero, Apply(Line(), 0, NudgeEdge.Start, -20000, previous: null).WordStarts[0]);
    }

    [Fact]
    public void LastWordStart_StaysBeforeTheLineEnd()
    {
        Assert.Equal(S(11.49), Apply(Line(), 2, NudgeEdge.Start, 1000).WordStarts[2]);
    }

    [Fact]
    public void WordEnd_IsTheNextWordsStart_KeptBeforeTheWordAfterIt()
    {
        var moved = Apply(Line(), 0, NudgeEdge.End, 100);
        Assert.Equal(new[] { S(10), S(10.6), S(11) }, moved.WordStarts);
        Assert.Equal(S(10.99), Apply(Line(), 0, NudgeEdge.End, 1000).WordStarts[1]);
        Assert.Equal(S(10.01), Apply(Line(), 0, NudgeEdge.End, -1000).WordStarts[1]);
    }

    [Fact]
    public void LastWordEnd_IsTheLineEnd_KeptAfterTheWordAndInsideTheSong()
    {
        Assert.Equal(S(11.6), Apply(Line(), 2, NudgeEdge.End, 100).End);
        Assert.Equal(S(11.01), Apply(Line(), 2, NudgeEdge.End, -1000).End);
        Assert.Equal(S(11.53), Apply(Line(), 2, NudgeEdge.End, 1000, duration: S(11.53)).End);
    }

    [Fact]
    public void WholeWord_MovesItsStartAndEnd_KeepingItsLength_WithinItsNeighbours()
    {
        var moved = Apply(Line(), 1, NudgeEdge.Whole, 100);
        Assert.Equal(new[] { S(10), S(10.6), S(11.1) }, moved.WordStarts);
        Assert.Equal(S(11.5), moved.End);
        // Later: its end (the next word's start) stops 10 ms before the line end.
        var far = Apply(Line(), 1, NudgeEdge.Whole, 1000);
        Assert.Equal(new[] { S(10), S(10.99), S(11.49) }, far.WordStarts);
        // Earlier: its start stops 10 ms after the word before.
        Assert.Equal(new[] { S(10), S(10.01), S(10.51) }, Apply(Line(), 1, NudgeEdge.Whole, -1000).WordStarts);
    }

    [Fact]
    public void WholeLastWord_TakesTheLineEndAlong_UpToTheSongsEnd()
    {
        var moved = Apply(Line(), 2, NudgeEdge.Whole, 100, duration: S(11.55));
        Assert.Equal(S(11.05), moved.WordStarts[2]);
        Assert.Equal(S(11.55), moved.End);
    }

    [Fact]
    public void WholeFirstWord_StaysAfterThePreviousLine()
    {
        var moved = Apply(Line(), 0, NudgeEdge.Whole, -100, previous: 9.95);
        Assert.Equal(S(9.96), moved.WordStarts[0]);
        Assert.Equal(S(10.46), moved.WordStarts[1]);
    }

    [Fact]
    public void NoRoom_ReturnsTheSameLine()
    {
        var tight = new NudgeLine(new[] { S(10), S(10.01) }, S(10.02));
        Assert.Same(tight, Apply(tight, 0, NudgeEdge.Whole, 10));
        Assert.Same(tight, Apply(tight, 1, NudgeEdge.Start, 10));
        Assert.Same(tight, Apply(tight, -1, NudgeEdge.End, -10));
    }

    // ── Why a nudge stopped short (GitHub #237) ─────────────────────────────

    private static NudgeClamp ClampOf(NudgeLine line, int word, NudgeEdge edge, int deltaMs,
        double? previous = 5, double? next = 15, TimeSpan? duration = null, bool spread = false)
    {
        TimingNudge.Apply(line, word, edge, Ms(deltaMs), previous is { } p ? S(p) : null, next is { } n ? S(n) : null, duration ?? Song, out var clamp, spread);
        return clamp;
    }

    [Fact]
    public void FullStep_ReportsNoClamp()
    {
        Assert.Equal(NudgeClamp.None, ClampOf(Line(), 1, NudgeEdge.Start, 10));
        Assert.Equal(NudgeClamp.None, ClampOf(Line(), -1, NudgeEdge.Whole, -100));
    }

    [Fact]
    public void SqueezedWord_IsBlockedByTheNextWord_AndNamesItsStart()
    {
        // The #237 case: words 10 ms apart (35.38 / 35.39), so the first has no room to go later.
        var line = new NudgeLine(new[] { Ms(35380), Ms(35390) }, S(36));
        var after = TimingNudge.Apply(line, 0, NudgeEdge.Start, Ms(10), S(30), S(40), Song, out var clamp);
        Assert.Same(line, after);
        Assert.Equal(new NudgeClamp(NudgeLimit.NextWord, Ms(35390)), clamp);
        // And the second word has no room to go earlier.
        Assert.Equal(new NudgeClamp(NudgeLimit.PreviousWord, Ms(35380)), ClampOf(line, 1, NudgeEdge.Start, -10, previous: 30, next: 40));
    }

    [Fact]
    public void WordStart_IsBlockedByThePreviousWord()
    {
        Assert.Equal(new NudgeClamp(NudgeLimit.PreviousWord, S(10.5)), ClampOf(Line(), 2, NudgeEdge.Start, -1000));
        Assert.Equal(new NudgeClamp(NudgeLimit.PreviousWord, S(10)), ClampOf(Line(), 1, NudgeEdge.Whole, -1000));
    }

    [Fact]
    public void WordEnd_IsBlockedByItsOwnStart_AndByTheWordAfterTheNext()
    {
        Assert.Equal(new NudgeClamp(NudgeLimit.ThisWord, S(10)), ClampOf(Line(), 0, NudgeEdge.End, -1000));
        Assert.Equal(new NudgeClamp(NudgeLimit.NextWord, S(11)), ClampOf(Line(), 0, NudgeEdge.End, 1000));
    }

    [Fact]
    public void LastWord_IsBlockedByTheLineEnd_AndTheSongsEnd()
    {
        Assert.Equal(new NudgeClamp(NudgeLimit.LineEnd, S(11.5)), ClampOf(Line(), 2, NudgeEdge.Start, 1000));
        Assert.Equal(new NudgeClamp(NudgeLimit.LineEnd, S(11.5)), ClampOf(Line(), 1, NudgeEdge.Whole, 1000));
        Assert.Equal(new NudgeClamp(NudgeLimit.SongEnd, S(11.53)), ClampOf(Line(), 2, NudgeEdge.End, 1000, duration: S(11.53)));
    }

    [Fact]
    public void FirstWordAndLine_AreBlockedByTheNeighbouringLines_OrZero()
    {
        Assert.Equal(new NudgeClamp(NudgeLimit.PreviousLine, S(5)), ClampOf(Line(), 0, NudgeEdge.Start, -10000));
        Assert.Equal(new NudgeClamp(NudgeLimit.PreviousLine, S(9.95)), ClampOf(Line(), -1, NudgeEdge.Whole, -100, previous: 9.95));
        Assert.Equal(new NudgeClamp(NudgeLimit.NextLine, S(10.05)), ClampOf(Line(), -1, NudgeEdge.Whole, 100, next: 10.05));
        Assert.Equal(new NudgeClamp(NudgeLimit.Zero, TimeSpan.Zero), ClampOf(Line(), 0, NudgeEdge.Start, -20000, previous: null));
        var atZero = new NudgeLine(new[] { TimeSpan.Zero, Ms(300) }, Ms(600));
        Assert.Equal(new NudgeClamp(NudgeLimit.Zero, TimeSpan.Zero), ClampOf(atZero, -1, NudgeEdge.Whole, -10, previous: null));
    }

    [Fact]
    public void LineEdges_AreBlockedByTheirOwnWords_AndTheSongsEnd()
    {
        Assert.Equal(new NudgeClamp(NudgeLimit.PreviousWord, S(11)), ClampOf(Line(), -1, NudgeEdge.End, -1000));
        Assert.Equal(new NudgeClamp(NudgeLimit.NextWord, S(10.5)), ClampOf(Line(), -1, NudgeEdge.Start, 1000));
        Assert.Equal(new NudgeClamp(NudgeLimit.SongEnd, S(11.55)), ClampOf(Line(), -1, NudgeEdge.Whole, 100, next: null, duration: S(11.55)));
        Assert.Equal(new NudgeClamp(NudgeLimit.LineStart, S(10)), ClampOf(Line(), -1, NudgeEdge.End, -5000, spread: true));
        Assert.Equal(new NudgeClamp(NudgeLimit.LineEnd, S(11.5)), ClampOf(Line(), -1, NudgeEdge.Start, 5000, spread: true));
    }

    [Fact]
    public void PartialMove_StillReportsTheLimit_AndMovesAsBefore()
    {
        var line = Line();
        var after = TimingNudge.Apply(line, 1, NudgeEdge.Start, Ms(1000), S(5), S(15), Song, out var clamp);
        Assert.Equal(S(10.99), after.WordStarts[1]);
        Assert.Equal(new NudgeClamp(NudgeLimit.NextWord, S(11)), clamp);
    }

    // ── A review line ───────────────────────────────────────────────────────

    private static AlignedLine WordLine() => new("Hello big world", S(10), S(11.5),
        new[] { new AlignedWord("Hello", S(10), S(10.5)), new AlignedWord("big", S(10.5), S(11)), new AlignedWord("world", S(11), S(11.5)) }, 0.9, false);

    [Fact]
    public void ReviewLine_WordNudge_MarksItWordTimed_AndExportsTheNewTimes()
    {
        var line = new ReviewLine(new AlignedLine("Hello big world", S(10), S(13), Array.Empty<AlignedWord>(), 1, false));
        var changed = 0;
        line.Changed += () => changed++;

        Assert.True(line.Nudge(line.Words[1], NudgeEdge.Start, Ms(10), null, null, Song));

        Assert.True(line.HasWordTimings);
        Assert.Equal(1, changed);
        var exported = line.ToAlignedLine();
        Assert.Equal(S(11.01), exported.Words[1].Start);
        Assert.Equal(S(11.01), exported.Words[0].End);
        Assert.Equal("[00:10.00]<00:10.00>Hello <00:11.01>big <00:12.00>world<00:13.00>", TimedLyricsBuilder.BuildElrc(new[] { exported }));
    }

    [Fact]
    public void ReviewLine_LineNudge_OnALineLevelLine_StaysLineLevel()
    {
        var line = new ReviewLine(new AlignedLine("Hello big world", S(10), S(13), Array.Empty<AlignedWord>(), 1, false));

        Assert.True(line.Nudge(null, NudgeEdge.Start, Ms(-100), S(5), S(20), Song));

        Assert.False(line.HasWordTimings);
        Assert.Equal(S(9.9), line.Start);
        Assert.Equal("[00:09.90]Hello big world", TimedLyricsBuilder.BuildLrc(new[] { line.ToAlignedLine() }));
    }

    [Fact]
    public void ReviewLine_WholeNudge_TakesItsBackgroundVocalAlong()
    {
        var line = new ReviewLine(WordLine() with { Background = new[] { new AlignedWord("ooh", S(10.2), S(10.8)) } });

        line.Nudge(null, NudgeEdge.Whole, Ms(100), null, null, Song);

        Assert.Equal(S(10.3), line.Background![0].Start);
        Assert.Equal(S(10.9), line.Background![0].End);
        Assert.Equal(S(11.6), line.End);
    }

    [Fact]
    public void ReviewLine_NothingToMove_RaisesNoChange()
    {
        var line = new ReviewLine(WordLine());
        var changed = 0;
        line.Changed += () => changed++;

        Assert.False(line.Nudge(null, NudgeEdge.Whole, Ms(-10), S(10), null, Song));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void ReviewLine_ElrcRow_ShowsTheNudgedTimes()
    {
        var line = new ReviewLine(WordLine()) { ShowWordTags = true };

        line.Nudge(line.Words[2], NudgeEdge.End, Ms(100), null, null, Song);

        Assert.Equal("<00:10.00>Hello <00:10.50>big <00:11.00>world<00:11.60>", line.RowText);
    }
}
