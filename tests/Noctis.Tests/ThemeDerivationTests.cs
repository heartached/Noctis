using Avalonia.Media;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ThemeDerivationTests
{
    [Theory]
    [InlineData("#000000", 0.0)]
    [InlineData("#FFFFFF", 1.0)]
    [InlineData("#808080", 0.21586, 0.001)]
    public void RelativeLuminance_MatchesWcag(string hex, double expected, double tolerance = 0.0001)
    {
        var color = Color.Parse(hex);
        var actual = ThemeDerivation.RelativeLuminance(color);
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void Contrast_BlackOnWhite_Is21()
    {
        var c = ThemeDerivation.ContrastRatio(Color.Parse("#000000"), Color.Parse("#FFFFFF"));
        Assert.InRange(c, 20.9, 21.1);
    }

    [Fact]
    public void Contrast_Symmetric()
    {
        var a = ThemeDerivation.ContrastRatio(Color.Parse("#101010"), Color.Parse("#E0E0E0"));
        var b = ThemeDerivation.ContrastRatio(Color.Parse("#E0E0E0"), Color.Parse("#101010"));
        Assert.Equal(a, b, 4);
    }

    [Theory]
    [InlineData("#000000", 4.5)]
    [InlineData("#1E1E1E", 4.5)]
    [InlineData("#0E1322", 4.5)]
    [InlineData("#F5F5F5", 4.5)]
    [InlineData("#888888", 4.5)]
    public void PickReadableText_MeetsTarget(string bgHex, double minRatio)
    {
        var bg = Color.Parse(bgHex);
        var fg = ThemeDerivation.PickReadableText(bg, minRatio);
        Assert.True(
            ThemeDerivation.ContrastRatio(bg, fg) >= minRatio,
            $"got ratio {ThemeDerivation.ContrastRatio(bg, fg)} for bg {bgHex}");
    }

    [Fact]
    public void PickReadableText_PrefersWhiteOnDark_BlackOnLight()
    {
        var onDark = ThemeDerivation.PickReadableText(Color.Parse("#101010"), 4.5);
        var onLight = ThemeDerivation.PickReadableText(Color.Parse("#F5F5F5"), 4.5);
        Assert.True(onDark.R > 200);
        Assert.True(onLight.R < 80);
    }
}
