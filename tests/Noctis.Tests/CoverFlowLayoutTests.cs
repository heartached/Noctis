using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Cover Flow layout is persisted as a string (Appearance → Cover Flow Layout) and also
/// stepped by the top-bar pill segment, so both the parse fallback and the cycle order matter.
/// </summary>
public class CoverFlowLayoutTests
{
    [Theory]
    [InlineData("Carousel", CoverFlowLayout.Carousel)]
    [InlineData("Cascade", CoverFlowLayout.Cascade)]
    [InlineData("Collage", CoverFlowLayout.Collage)]
    [InlineData("collage", CoverFlowLayout.Collage)]
    [InlineData(" cascade ", CoverFlowLayout.Cascade)]
    public void Parse_AcceptsEveryLayout_CaseInsensitively(string setting, CoverFlowLayout expected)
        => Assert.Equal(expected, CoverFlowLayouts.Parse(setting));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mosaic")]
    [InlineData("2")]
    public void Parse_FallsBackToTheCarousel_ForAnythingUnknown(string? setting)
        => Assert.Equal(CoverFlowLayout.Carousel, CoverFlowLayouts.Parse(setting));

    [Fact]
    public void DefaultSetting_IsTheClassicCarousel()
        => Assert.Equal(CoverFlowLayout.Carousel, CoverFlowLayouts.Parse(CoverFlowLayouts.DefaultSetting));

    [Fact]
    public void Next_CyclesThroughAllThreeLayouts_AndWraps()
    {
        Assert.Equal(CoverFlowLayout.Cascade, CoverFlowLayouts.Next(CoverFlowLayout.Carousel));
        Assert.Equal(CoverFlowLayout.Collage, CoverFlowLayouts.Next(CoverFlowLayout.Cascade));
        Assert.Equal(CoverFlowLayout.Carousel, CoverFlowLayouts.Next(CoverFlowLayout.Collage));
    }
}
