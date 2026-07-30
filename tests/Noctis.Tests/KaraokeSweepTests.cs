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
