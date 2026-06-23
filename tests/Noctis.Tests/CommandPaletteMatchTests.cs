using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class CommandPaletteMatchTests
{
    [Fact]
    public void MatchScore_PrefixBeatsWordBoundaryBeatsSubstring()
    {
        var prefix = CommandPaletteViewModel.MatchScore("Nightwish", "night");
        var wordBoundary = CommandPaletteViewModel.MatchScore("The Night", "night");
        var substring = CommandPaletteViewModel.MatchScore("Goodnight", "night");

        Assert.True(prefix > wordBoundary);
        Assert.True(wordBoundary > substring);
        Assert.True(substring > 0);
    }

    [Fact]
    public void MatchScore_NoMatch_ReturnsZero()
    {
        Assert.Equal(0, CommandPaletteViewModel.MatchScore("Nightwish", "xyz"));
        Assert.Equal(0, CommandPaletteViewModel.MatchScore("", "night"));
        Assert.Equal(0, CommandPaletteViewModel.MatchScore("Nightwish", ""));
    }

    [Fact]
    public void MatchScore_IsCaseInsensitive()
    {
        Assert.Equal(100, CommandPaletteViewModel.MatchScore("NIGHTWISH", "night"));
    }
}
