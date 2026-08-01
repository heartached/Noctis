using System.Collections.Generic;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Guards both release-date formats after the shared-parse refactor: full month for
/// the album page footer, compact month for the description dialog's facts grid.
/// </summary>
public class AlbumReleaseDateFormatTests
{
    private static Album WithReleaseDate(string releaseDate, int year = 0) =>
        new() { Year = year, Tracks = new List<Track> { new() { ReleaseDate = releaseDate } } };

    [Theory]
    [InlineData("2014-10-27", "October 27, 2014", "Oct 27, 2014")]
    [InlineData("2014/10/27", "October 27, 2014", "Oct 27, 2014")]
    public void FullAndShort_FormatParseableDates(string raw, string full, string compact)
    {
        var album = WithReleaseDate(raw);
        Assert.Equal(full, album.ReleaseDateFormatted);
        Assert.Equal(compact, album.ReleaseDateShortFormatted);
    }

    [Fact]
    public void FallsBackToYear_WhenNoTrackDate()
    {
        var album = WithReleaseDate(string.Empty, year: 2014);
        Assert.Equal("2014", album.ReleaseDateFormatted);
        Assert.Equal("2014", album.ReleaseDateShortFormatted);
    }
}
