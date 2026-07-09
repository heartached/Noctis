using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

public class MetadataLineConverterTests
{
    private static string Convert(object? parameter, params object?[] values) =>
        (string)new MetadataLineConverter().Convert(
            values, typeof(string), parameter, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void MissingGenre_DoesNotLeaveLeadingDot()
    {
        // Untagged files rendered "· 2018 · 24 tracks" on the lyrics page.
        Assert.Equal("2018 · 24 tracks", Convert("track", "", 2018, 24));
    }

    [Fact]
    public void AllSegmentsPresent_JoinsWithDots()
    {
        Assert.Equal("Rock · 2018 · 24 tracks", Convert("track", "Rock", 2018, 24));
    }

    [Fact]
    public void SingleTrack_UsesSingularUnit()
    {
        Assert.Equal("2012 · 1 track", Convert("track", "", 2012, 1));
    }

    [Fact]
    public void MissingYear_IsSkipped()
    {
        Assert.Equal("Rock · 24 tracks", Convert("track", "Rock", 0, 24));
    }

    [Fact]
    public void AllMissing_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Convert("track", "", 0, 0));
    }

    [Fact]
    public void GenreYearVariant_WithoutUnitParameter()
    {
        // Album header variant: "{Genre} · {Year}".
        Assert.Equal("2009", Convert(null, "", 2009));
        Assert.Equal("Rock · 2009", Convert(null, "Rock", 2009));
    }
}
