using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Artists grid sort (Name / Song count / Album count + direction) added to the top bar.</summary>
public class ArtistSortTests
{
    private static List<Artist> Sample() => new()
    {
        new Artist { Name = "Bad Bunny", TrackCount = 40, AlbumCount = 5 },
        new Artist { Name = "Aphex Twin", TrackCount = 12, AlbumCount = 2, IsFavorite = true },
        new Artist { Name = "Caribou", TrackCount = 25, AlbumCount = 3 },
    };

    private static List<string> Names(List<ArtistRow> rows)
        => rows.SelectMany(r => r.Artists).Select(a => a.Name).ToList();

    [Fact]
    public void Name_sort_keeps_favorites_first_then_alphabetical()
    {
        var rows = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: true);
        Assert.Equal(new[] { "Aphex Twin", "Bad Bunny", "Caribou" }, Names(rows));
    }

    [Fact]
    public void Song_count_descending_ranks_by_tracks_ignoring_favorites()
    {
        var rows = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "songs", ascending: false);
        Assert.Equal(new[] { "Bad Bunny", "Caribou", "Aphex Twin" }, Names(rows));
    }

    [Fact]
    public void Album_count_ascending()
    {
        var rows = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "albums", ascending: true);
        Assert.Equal(new[] { "Aphex Twin", "Caribou", "Bad Bunny" }, Names(rows));
    }

    [Fact]
    public void Search_rank_wins_over_the_chosen_sort()
    {
        // Both match "a"; a prefix match ranks above a mid-word match regardless of counts.
        var artists = new List<Artist>
        {
            new() { Name = "Zebra Alpha", TrackCount = 99 },
            new() { Name = "Alpha", TrackCount = 1 },
        };
        var rows = LibraryArtistsViewModel.BuildRows(artists, "alpha", "songs", ascending: false);
        Assert.Equal("Alpha", Names(rows)[0]);
    }
}
