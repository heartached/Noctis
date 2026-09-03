using Noctis.Helpers;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>GitHub #57: shifting every timestamp of an LRC by one offset.</summary>
public class LyricsShiftAllTests
{
    private const string NL = "\n";

    [Fact]
    public void ShiftsLineAndWordTags_LeavesMetadataAndUntimedLinesAlone()
    {
        var lrc = "[ar:Someone]" + NL
                + "[00:10.00]first <00:10.50>word" + NL
                + "untimed line" + NL
                + "[01:00.5]half-second fraction";

        var later = LyricsTextHelper.ShiftAllTimestamps(lrc, TimeSpan.FromSeconds(1.25));

        Assert.Equal("[ar:Someone]" + NL
                     + "[00:11.25]first <00:11.75>word" + NL
                     + "untimed line" + NL
                     + "[01:01.75]half-second fraction", later);
    }

    [Fact]
    public void NegativeShift_ClampsAtZero()
    {
        var shifted = LyricsTextHelper.ShiftAllTimestamps("[00:00.50]a" + NL + "[00:05.00]b", TimeSpan.FromSeconds(-2));
        Assert.Equal("[00:00.00]a" + NL + "[00:03.00]b", shifted);
    }

    [Fact]
    public void ZeroOffsetOrEmpty_IsIdentity()
    {
        Assert.Equal("[00:01.00]x", LyricsTextHelper.ShiftAllTimestamps("[00:01.00]x", TimeSpan.Zero));
        Assert.Equal(string.Empty, LyricsTextHelper.ShiftAllTimestamps(null, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("1", 1)]
    [InlineData("1,5", 1.5)]
    [InlineData(" 2 ", 2)]
    public void ShiftSeconds_ParsesLooseInput(string text, double expected)
    {
        Assert.True(MetadataViewModel.TryParseShiftSeconds(text, out var s));
        Assert.Equal(expected, s);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void ShiftSeconds_RejectsGarbage(string text)
        => Assert.False(MetadataViewModel.TryParseShiftSeconds(text, out _));
}
