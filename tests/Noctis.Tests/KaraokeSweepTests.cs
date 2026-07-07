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
}
