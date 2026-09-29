using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Island artist link (Discord, aaron 2026-09-23): "Kanye West, GLC, Consequence" was one
/// link that always opened the primary artist. The credit stays one marquee run, so the name
/// under the pointer is resolved from the character index instead.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class ArtistCreditSpansTests
{
    public ArtistCreditSpansTests() => ArtistCredit.ResetToDefaults();

    [Fact]
    public void Locate_FindsEachNameWhereItIsDisplayed()
    {
        var spans = ArtistCreditSpans.Locate("Kanye West, GLC, Consequence");

        Assert.Equal(3, spans.Count);
        Assert.Equal(new ArtistCreditSpans.Span(0, 10, "Kanye West"), spans[0]);
        Assert.Equal(new ArtistCreditSpans.Span(12, 3, "GLC"), spans[1]);
        Assert.Equal(new ArtistCreditSpans.Span(17, 11, "Consequence"), spans[2]);
    }

    [Theory]
    [InlineData(0, "Kanye West")]
    [InlineData(9, "Kanye West")]
    [InlineData(10, null)]   // the comma
    [InlineData(11, null)]   // the space
    [InlineData(12, "GLC")]
    [InlineData(14, "GLC")]
    [InlineData(17, "Consequence")]
    [InlineData(27, "Consequence")]
    [InlineData(28, null)]   // past the end
    [InlineData(99, null)]
    public void NameAt_ResolvesTheArtistUnderACharacter_AndNothingOnSeparators(int index, string? expected)
    {
        Assert.Equal(expected, ArtistCreditSpans.NameAt("Kanye West, GLC, Consequence", index));
    }

    [Fact]
    public void NameAt_SingleArtist_ResolvesEverywhere()
    {
        Assert.Equal("Taylor Swift", ArtistCreditSpans.NameAt("Taylor Swift", 0));
        Assert.Equal("Taylor Swift", ArtistCreditSpans.NameAt("Taylor Swift", 40));
    }

    [Fact]
    public void NameAt_FeaturingSpelling_SkipsTheSeparatorWord()
    {
        const string credit = "Future feat. Metro Boomin";
        Assert.Equal("Future", ArtistCreditSpans.NameAt(credit, 2));
        Assert.Null(ArtistCreditSpans.NameAt(credit, 8));           // inside "feat."
        Assert.Equal("Metro Boomin", ArtistCreditSpans.NameAt(credit, 14));
    }

    [Fact]
    public void Locate_EmptyCredit_HasNoSpans()
    {
        Assert.Empty(ArtistCreditSpans.Locate(null));
        Assert.Empty(ArtistCreditSpans.Locate("   "));
        Assert.Null(ArtistCreditSpans.NameAt("", 0));
    }
}
