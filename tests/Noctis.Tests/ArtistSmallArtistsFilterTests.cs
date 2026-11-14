using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #233 (4) and (5): the Artists page shows "N albums · N songs" under each name, and
/// can hide artists with fewer songs than a threshold. Both read the same Artist.TrackCount,
/// so the count a tile shows and the filter's cut always agree.
/// </summary>
[Collection("Localization")] // Loc.Instance is process-wide
public class ArtistSmallArtistsFilterTests
{
    private static List<Artist> Sample() => new()
    {
        new Artist { Name = "Big", TrackCount = 41, AlbumCount = 3 },
        new Artist { Name = "Feature Guest", TrackCount = 1, AlbumCount = 1 },
        new Artist { Name = "Pair", TrackCount = 2, AlbumCount = 1 },
        new Artist { Name = "Trio", TrackCount = 3, AlbumCount = 2 },
    };

    private static List<string> Names(List<ArtistRow> rows)
        => rows.SelectMany(r => r.Artists).Select(a => a.Name).ToList();

    [Fact]
    public void Threshold_off_shows_every_artist()
    {
        var rows = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: true, minSongs: 0);
        Assert.Equal(new[] { "Big", "Feature Guest", "Pair", "Trio" }, Names(rows));
    }

    [Fact]
    public void Threshold_hides_artists_with_fewer_songs()
    {
        var rows = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: true, minSongs: 3);
        Assert.Equal(new[] { "Big", "Trio" }, Names(rows));
    }

    [Fact]
    public void Threshold_applies_to_the_list_view_too()
    {
        var stats = LibraryArtistsViewModel.ComputeStats(Array.Empty<Track>(), ArtistGroupMode.Artist);
        var rows = LibraryArtistsViewModel.BuildListRows(Sample(), string.Empty, "name", true, stats, minSongs: 2);
        Assert.Equal(new[] { "Big", "Pair", "Trio" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void A_search_still_finds_hidden_artists()
    {
        var rows = LibraryArtistsViewModel.BuildRows(Sample(), "guest", "name", ascending: true, minSongs: 10);
        Assert.Equal(new[] { "Feature Guest" }, Names(rows));
    }

    [AvaloniaFact] // SetCulture raises binding updates on the UI thread
    public void Counts_line_uses_singular_and_plural_forms()
    {
        try
        {
            Loc.Instance.SetCulture("en");
            Assert.Equal("3 albums · 41 songs", LibraryArtistsViewModel.CountsLineFor(new Artist { AlbumCount = 3, TrackCount = 41 }));
            Assert.Equal("1 album · 1 song", LibraryArtistsViewModel.CountsLineFor(new Artist { AlbumCount = 1, TrackCount = 1 }));
            Assert.Equal("1 album · 1,234 songs", LibraryArtistsViewModel.CountsLineFor(new Artist { AlbumCount = 1, TrackCount = 1234 }));

            Loc.Instance.SetCulture("ru");
            Assert.Equal("2 альбома · 5 песен", LibraryArtistsViewModel.CountsLineFor(new Artist { AlbumCount = 2, TrackCount = 5 }));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    [AvaloniaFact] // SetCulture raises binding updates on the UI thread
    public void Rebuild_stamps_each_artist_with_its_counts_line()
    {
        Loc.Instance.SetCulture("en");
        var artists = Sample();
        LibraryArtistsViewModel.BuildRows(artists, string.Empty, "name", ascending: true);
        Assert.Equal("3 albums · 41 songs", artists[0].CountsLine);
        Assert.Equal("1 album · 1 song", artists[1].CountsLine);
    }
}
