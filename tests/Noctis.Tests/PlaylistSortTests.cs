using System;
using System.Collections.Generic;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class PlaylistSortTests
{
    private static List<Track> Sample() => new()
    {
        new() { Title = "Cherry", Artist = "Beta", Album = "Zed",   Duration = TimeSpan.FromSeconds(200), DateAdded = new DateTime(2026, 1, 1) },
        new() { Title = "Apple",  Artist = "Alpha", Album = "Yarn", Duration = TimeSpan.FromSeconds(100), DateAdded = new DateTime(2026, 3, 1) },
        new() { Title = "Banana", Artist = "Gamma", Album = "Xyz",  Duration = TimeSpan.FromSeconds(300), DateAdded = new DateTime(2026, 2, 1) },
    };

    [Fact]
    public void SortTracks_Manual_PreservesGivenOrder()
    {
        var input = Sample();
        var result = PlaylistViewModel.SortTracks(input, PlaylistSortMode.Manual);
        Assert.Equal(new[] { "Cherry", "Apple", "Banana" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_Title_SortsAlphabetically()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.Title);
        Assert.Equal(new[] { "Apple", "Banana", "Cherry" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_Artist_SortsByArtist()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.Artist);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, result.Select(t => t.Artist));
    }

    [Fact]
    public void SortTracks_Duration_SortsAscending()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.Duration);
        Assert.Equal(new[] { "Apple", "Cherry", "Banana" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_RecentlyAdded_NewestFirst()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.RecentlyAdded);
        Assert.Equal(new[] { "Apple", "Banana", "Cherry" }, result.Select(t => t.Title));
    }

    // ── release date ─────────────────────────────────────────────────
    //
    // Requested for following how a band evolved, so the ordering has to hold
    // inside an album too: year, then the finer release date when two albums
    // share a year, then album, disc and track number.

    private static List<Track> Discography() => new()
    {
        new() { Title = "Later B",  Album = "Later",  Year = 2014, ReleaseDate = "2014-10-27", TrackNumber = 2 },
        new() { Title = "Early",    Album = "Early",  Year = 2009, ReleaseDate = "2009-05-01", TrackNumber = 1 },
        new() { Title = "Later A",  Album = "Later",  Year = 2014, ReleaseDate = "2014-10-27", TrackNumber = 1 },
        new() { Title = "Spring",   Album = "Spring", Year = 2014, ReleaseDate = "2014-01-05", TrackNumber = 1 },
    };

    [Fact]
    public void SortTracks_ReleaseDateOldest_RunsOldestToNewestInTrackOrder()
    {
        var result = PlaylistViewModel.SortTracks(Discography(), PlaylistSortMode.ReleaseDateOldest);
        Assert.Equal(new[] { "Early", "Spring", "Later A", "Later B" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_ReleaseDateNewest_FlipsAlbumsButNotTracksWithinThem()
    {
        var result = PlaylistViewModel.SortTracks(Discography(), PlaylistSortMode.ReleaseDateNewest);
        Assert.Equal(new[] { "Later A", "Later B", "Spring", "Early" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_SameYear_BreaksTheTieOnTheFullReleaseDate()
    {
        // Album order deliberately contradicts date order, so a tie broken on the
        // album name instead of the date would fail this.
        var tracks = new List<Track>
        {
            new() { Title = "December", Album = "A", Year = 2014, ReleaseDate = "2014-12-01" },
            new() { Title = "January",  Album = "B", Year = 2014, ReleaseDate = "2014-01-05" },
        };

        var result = PlaylistViewModel.SortTracks(tracks, PlaylistSortMode.ReleaseDateOldest);
        Assert.Equal(new[] { "January", "December" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_SameReleaseDateAcrossAlbums_NewestMirrorsTheAlbumOrder()
    {
        // Two different albums released the same day: the album-level order has to
        // flip with the direction (Discord: newest-first ended "… 1 2" instead of
        // "… 2 1"), while the running order inside each album stays ascending.
        var tracks = new List<Track>
        {
            new() { Title = "A1", Album = "Alpha", Year = 2014, ReleaseDate = "2014-10-27", TrackNumber = 1 },
            new() { Title = "A2", Album = "Alpha", Year = 2014, ReleaseDate = "2014-10-27", TrackNumber = 2 },
            new() { Title = "B1", Album = "Beta",  Year = 2014, ReleaseDate = "2014-10-27", TrackNumber = 1 },
        };

        var oldest = PlaylistViewModel.SortTracks(tracks, PlaylistSortMode.ReleaseDateOldest);
        Assert.Equal(new[] { "A1", "A2", "B1" }, oldest.Select(t => t.Title));

        var newest = PlaylistViewModel.SortTracks(tracks, PlaylistSortMode.ReleaseDateNewest);
        Assert.Equal(new[] { "B1", "A1", "A2" }, newest.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_UnparseableReleaseDate_FallsBackToTheYear()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Newer", Album = "B", Year = 2014, ReleaseDate = "sometime in 2014" },
            new() { Title = "Older", Album = "A", Year = 2009, ReleaseDate = "no idea" },
        };

        var result = PlaylistViewModel.SortTracks(tracks, PlaylistSortMode.ReleaseDateOldest);
        Assert.Equal(new[] { "Older", "Newer" }, result.Select(t => t.Title));
    }

    [Theory]
    [InlineData(PlaylistSortMode.ReleaseDateOldest)]
    [InlineData(PlaylistSortMode.ReleaseDateNewest)]
    public void SortTracks_UntaggedYear_SinksToTheEndInBothDirections(PlaylistSortMode mode)
    {
        // Year 0 means "not tagged", not "year zero" — ascending order would otherwise
        // open the list with every untagged track.
        var tracks = new List<Track>
        {
            new() { Title = "Untagged", Album = "A", Year = 0 },
            new() { Title = "Tagged",   Album = "B", Year = 2009, ReleaseDate = "2009-05-01" },
        };

        var result = PlaylistViewModel.SortTracks(tracks, mode);
        Assert.Equal("Untagged", result[^1].Title);
    }
}
