using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class KaraokeSweepTests
{
    [Theory]
    [InlineData(10.0, 12.0, 9.0, 0.0)]    // before the word
    [InlineData(10.0, 12.0, 11.0, 0.5)]   // mid-word
    [InlineData(10.0, 12.0, 13.0, 1.0)]   // past the word
    [InlineData(10.0, 10.0, 10.0, 1.0)]   // zero-length word: lit once reached
    [InlineData(10.0, 10.0, 9.9, 0.0)]    // zero-length word: unlit before
    public void WordProgress_ClampsAndDividesElapsed(double start, double end, double t, double expected)
    {
        Assert.Equal(expected, KaraokeSweep.WordProgress(start, end, t), 6);
    }

    [Fact]
    public void MapRowsToTokenRanges_SplitsAcrossRows()
    {
        var tokens = new[] { "Never", "gonna", "give", "you", "up" };
        var rows = new[] { "Never gonna give", "you up" };
        var ranges = KaraokeSweep.MapRowsToTokenRanges(tokens, rows);
        Assert.NotNull(ranges);
        Assert.Equal(new[] { (0, 3), (3, 2) }, ranges!.ToArray());
    }

    [Fact]
    public void MapRowsToTokenRanges_SingleRowExactMatch()
    {
        var ranges = KaraokeSweep.MapRowsToTokenRanges(new[] { "hi", "there" }, new[] { "hi there" });
        Assert.Equal(new[] { (0, 2) }, ranges!.ToArray());
    }

    [Fact]
    public void MapRowsToTokenRanges_EditedTextReturnsNull()
    {
        // User edited the rendered line; tokens no longer match the ELRC words.
        var ranges = KaraokeSweep.MapRowsToTokenRanges(
            new[] { "Never", "gonna", "give" }, new[] { "Never ever give" });
        Assert.Null(ranges);
    }

    [Fact]
    public void MapRowsToTokenRanges_HardBrokenWordReturnsNull()
    {
        // WrapText hard-breaks an oversized word mid-token — no clean word mapping.
        var ranges = KaraokeSweep.MapRowsToTokenRanges(
            new[] { "Supercalifragilistic" }, new[] { "Supercalifra", "gilistic" });
        Assert.Null(ranges);
    }

    [Fact]
    public void MapRowsToTokenRanges_LeftoverTokensReturnsNull()
    {
        var ranges = KaraokeSweep.MapRowsToTokenRanges(
            new[] { "one", "two", "three" }, new[] { "one two" });
        Assert.Null(ranges);
    }

    [Fact]
    public void ResolveOpenLastWordEnd_UsesNextLineStartWithinCap()
    {
        // Next line starts 1.2s after the word — sweep runs until the handoff.
        var end = KaraokeSweep.ResolveOpenLastWordEnd(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11.2));
        Assert.Equal(TimeSpan.FromSeconds(11.2), end);
    }

    [Fact]
    public void ResolveOpenLastWordEnd_CapsLongGaps()
    {
        // 10s instrumental gap — the word isn't sung that long; cap at +2s.
        var end = KaraokeSweep.ResolveOpenLastWordEnd(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(12), end);
    }

    [Fact]
    public void ResolveOpenLastWordEnd_NoNextLineFallsBackToCap()
    {
        // Final line of the song — no next line to bound against.
        var end = KaraokeSweep.ResolveOpenLastWordEnd(TimeSpan.FromSeconds(10), null);
        Assert.Equal(TimeSpan.FromSeconds(12), end);
    }

    [Fact]
    public void ResolveOpenLastWordEnd_NextLineBeforeWordPassesThrough()
    {
        // Malformed data (next line starts before the word) degrades to the old
        // snap-to-lit behavior: end <= start means WordProgress returns 1 instantly.
        var end = KaraokeSweep.ResolveOpenLastWordEnd(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9));
        Assert.Equal(TimeSpan.FromSeconds(9), end);
    }

    [Theory]
    [InlineData(10.0, 12.0, 11.0, 0.5)]    // mid-word: same as WordProgress
    [InlineData(10.0, 12.0, 12.1, 1.05)]   // keeps advancing past the end (trailing feather finishes)
    [InlineData(10.0, 12.0, 9.9, -0.05)]   // pre-roll before the start (leading feather enters)
    public void BandProgress_ExtendsLinearlyPastBothEnds(double start, double end, double t, double expected)
    {
        Assert.Equal(expected, KaraokeSweep.BandProgress(start, end, t), 6);
    }

    [Fact]
    public void BandProgress_SnapsToInertSentinelsFarOutside()
    {
        Assert.Equal(KaraokeSweep.InertFuture, KaraokeSweep.BandProgress(10, 12, 7.9));  // raw ≤ -1
        Assert.Equal(KaraokeSweep.InertPast, KaraokeSweep.BandProgress(10, 12, 16.1));   // raw ≥ 2
    }

    /// <summary>
    /// "compromise" from issue #32: com 55.647–56.352, pro 56.352–56.728, mise 56.728–58.399.
    /// "com pro" is 39% of the word's time but 55% of its glyphs, so a linear ramp trails
    /// the voice through them and then lurches across the held "mise"; weighting by
    /// characters keeps the edge on the syllable actually being sung.
    /// </summary>
    [Fact]
    public void SyllableBandProgress_WeightsEachSyllableByItsCharacters()
    {
        var syllables = Compromise();

        // Each syllable boundary lands on its own character offset.
        Assert.Equal(3 / 11.0, KaraokeSweep.SyllableBandProgress(syllables, 55.647, 58.399, 56.352), 6);
        Assert.Equal(6 / 11.0, KaraokeSweep.SyllableBandProgress(syllables, 55.647, 58.399, 56.728), 6);

        // Halfway through the held final syllable: 6 chars + half of 5.
        Assert.Equal(8.5 / 11.0, KaraokeSweep.SyllableBandProgress(syllables, 55.647, 58.399, 57.5635), 6);

        // A linear sweep is still short of "mise" when the voice reaches it.
        Assert.True(KaraokeSweep.BandProgress(55.647, 58.399, 56.728) < 6 / 11.0);
    }

    [Fact]
    public void SyllableBandProgress_OutsideTheWordDefersToBandProgress()
    {
        var syllables = Compromise();

        // Pre-roll, overshoot and the far-out sentinels stay identical, so the
        // feathered edge still straddles the neighbouring words.
        Assert.Equal(KaraokeSweep.BandProgress(55.647, 58.399, 55.6),
                     KaraokeSweep.SyllableBandProgress(syllables, 55.647, 58.399, 55.6), 6);
        Assert.Equal(KaraokeSweep.BandProgress(55.647, 58.399, 58.5),
                     KaraokeSweep.SyllableBandProgress(syllables, 55.647, 58.399, 58.5), 6);
        Assert.Equal(KaraokeSweep.InertFuture,
                     KaraokeSweep.SyllableBandProgress(syllables, 55.647, 58.399, 50.0));
        Assert.Equal(KaraokeSweep.InertPast,
                     KaraokeSweep.SyllableBandProgress(syllables, 55.647, 58.399, 65.0));
    }

    [Fact]
    public void SyllableBandProgress_HoldsThroughAGapBetweenSyllables()
    {
        // Second syllable opens a beat late: the reveal parks on the character
        // boundary through the rest instead of creeping across unsung glyphs.
        var syllables = new List<WordSyllable>
        {
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.5), 3),
            new(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(12), 3),
        };

        Assert.Equal(0.5, KaraokeSweep.SyllableBandProgress(syllables, 10, 12, 10.75), 6);
    }

    [Fact]
    public void SyllableBandProgress_MissingEndFallsBackToTheNextSyllable()
    {
        var syllables = new List<WordSyllable>
        {
            new(TimeSpan.FromSeconds(10), null, 2),
            new(TimeSpan.FromSeconds(11), null, 2),
        };

        Assert.Equal(0.25, KaraokeSweep.SyllableBandProgress(syllables, 10, 12, 10.5), 6);
        Assert.Equal(0.75, KaraokeSweep.SyllableBandProgress(syllables, 10, 12, 11.5), 6);
    }

    private static List<WordSyllable> Compromise() =>
    [
        new(TimeSpan.FromSeconds(55.647), TimeSpan.FromSeconds(56.352), 3),  // com
        new(TimeSpan.FromSeconds(56.352), TimeSpan.FromSeconds(56.728), 3),  // pro
        new(TimeSpan.FromSeconds(56.728), TimeSpan.FromSeconds(58.399), 5),  // mise?
    ];

    [Fact]
    public void BandProgress_ZeroLengthWordSnapsToInertStates()
    {
        Assert.Equal(KaraokeSweep.InertFuture, KaraokeSweep.BandProgress(10, 10, 9.9));
        Assert.Equal(KaraokeSweep.InertPast, KaraokeSweep.BandProgress(10, 10, 10.0));
    }

    [Fact]
    public void InertSentinels_SitOutsideAnyFeatherReach()
    {
        // Contract with the sweep converter: the sentinels must clear the widest
        // possible feather and the band's pass-through range (-1, 2), so a word at
        // rest can never render a partial band.
        Assert.True(KaraokeSweep.InertFuture <= -1);
        Assert.True(KaraokeSweep.InertPast >= 2);
    }
}
