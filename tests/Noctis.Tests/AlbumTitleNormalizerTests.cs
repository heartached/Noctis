using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class AlbumTitleNormalizerTests
{
    [Theory]
    [InlineData("Goodbye & Good Riddance (5 Year Anniversary Edition) [Deluxe]", "Goodbye & Good Riddance")]
    [InlineData("Views (Deluxe)", "Views")]
    [InlineData("Thriller (25th Anniversary Edition)", "Thriller")]
    [InlineData("Abbey Road (2019 Remaster)", "Abbey Road")]
    [InlineData("Currents - Deluxe Edition", "Currents")]
    [InlineData("Blinding Lights - Single", "Blinding Lights")]
    [InlineData("Some EP - EP", "Some EP")]
    public void Normalize_StripsEditionSuffixes(string input, string expected)
        => Assert.Equal(expected, AlbumTitleNormalizer.Normalize(input));

    [Theory]
    [InlineData("Goodbye & Good Riddance", "Goodbye & Good Riddance")]
    [InlineData("good kid, m.A.A.d city", "good kid, m.A.A.d city")]
    [InlineData("(What's the Story) Morning Glory?", "(What's the Story) Morning Glory?")]
    public void Normalize_LeavesPlainTitlesUntouched(string input, string expected)
        => Assert.Equal(expected, AlbumTitleNormalizer.Normalize(input));

    [Fact]
    public void Normalize_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AlbumTitleNormalizer.Normalize(null));
        Assert.Equal(string.Empty, AlbumTitleNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_AllEdition_FallsBackToOriginal()
    {
        // Stripping everything would leave nothing, so the original (trimmed) is kept.
        Assert.Equal("(Deluxe Edition)", AlbumTitleNormalizer.Normalize("(Deluxe Edition)"));
    }
}
