using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord Tangent, "album dates going missing": an album took its year from its first
/// track alone, so an album whose first track is an undated WAV (Coda kept as MP3 + WAV)
/// showed year 0 even though its other tracks carry one; the artist page then printed
/// "Album · 0".
/// </summary>
public class AlbumYearTests
{
    private static Track T(int year) => new() { Title = "t", Year = year };

    [Fact]
    public void FirstTracksYear_Wins()
        => Assert.Equal(1975, Album.ResolveYear(new[] { T(1975), T(1994) }));

    [Fact]
    public void UndatedFirstTrack_FallsBackToTheNextDatedOne()
        => Assert.Equal(1982, Album.ResolveYear(new[] { T(0), T(0), T(1982), T(1990) }));

    [Fact]
    public void NoDatedTrack_StaysZero()
    {
        Assert.Equal(0, Album.ResolveYear(new[] { T(0), T(0) }));
        Assert.Equal(0, Album.ResolveYear(Array.Empty<Track>()));
    }

    [Fact]
    public void KindYearLine_DropsAnUnknownYear()
    {
        var tracks = Enumerable.Range(0, 10).Select(_ => T(0)).ToList();
        var undated = new Album { Tracks = tracks, TrackCount = tracks.Count, Year = 0 };
        Assert.Equal("Album", undated.ReleaseKindYearLine);

        var dated = new Album { Tracks = tracks, TrackCount = tracks.Count, Year = 1982 };
        Assert.Equal("Album · 1982", dated.ReleaseKindYearLine);
    }
}
