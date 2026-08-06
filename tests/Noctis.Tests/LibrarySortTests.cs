using System;
using System.Collections.Generic;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Locks the library sort options added for issue #23: Songs "Album Artist"
/// (with the empty-tag → track-artist fallback) and the Albums grid
/// "albumartist"/"year" modes, plus ordering stability.
/// </summary>
public class LibrarySortTests
{
    private static List<Track> SortSongs(List<Track> tracks, string sortCol, bool sortAsc) =>
        LibrarySongsViewModel.BuildFilteredAndSortedTracks(
            tracks, filter: string.Empty, sortCol, sortAsc, favOnly: false, qualityFilter: "All");

    // ── Songs: Album Artist ──

    [Fact]
    public void Songs_AlbumArtist_GroupsByAlbumArtistThenYearAlbumTrack()
    {
        var tracks = new List<Track>
        {
            new() { Title = "B2", Artist = "Someone", AlbumArtist = "Beta",  Album = "Late",  Year = 2020, TrackNumber = 1 },
            new() { Title = "A2", Artist = "Alpha",   AlbumArtist = "Alpha", Album = "Second", Year = 2015, TrackNumber = 2 },
            new() { Title = "B1", Artist = "Feature", AlbumArtist = "Beta",  Album = "Early", Year = 2010, TrackNumber = 1 },
            new() { Title = "A1", Artist = "Alpha",   AlbumArtist = "Alpha", Album = "Second", Year = 2015, TrackNumber = 1 },
        };

        var result = SortSongs(tracks, "Album Artist", sortAsc: true);

        // Alpha before Beta; within Alpha album order by track number; within Beta
        // chronological (Early 2010 before Late 2020).
        Assert.Equal(new[] { "A1", "A2", "B1", "B2" }, result.Select(t => t.Title));
    }

    [Fact]
    public void Songs_AlbumArtist_EmptyTagFallsBackToTrackArtist()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Tagged",   Artist = "Someone", AlbumArtist = "Middle" },
            new() { Title = "Untagged", Artist = "Zulu",    AlbumArtist = "" },
            new() { Title = "First",    Artist = "Other",   AlbumArtist = "Alpha" },
        };

        var result = SortSongs(tracks, "Album Artist", sortAsc: true);

        // The empty album-artist track sorts by its own artist (Zulu), not first/last
        // by the empty string — Apple Music/MusicBee fallback semantics.
        Assert.Equal(new[] { "First", "Tagged", "Untagged" }, result.Select(t => t.Title));
    }

    [Fact]
    public void Songs_AlbumArtistSortKey_FallsBackOnlyWhenBlank()
    {
        Assert.Equal("Band", LibrarySongsViewModel.AlbumArtistSortKey(
            new Track { Artist = "Singer", AlbumArtist = "Band" }));
        Assert.Equal("Singer", LibrarySongsViewModel.AlbumArtistSortKey(
            new Track { Artist = "Singer", AlbumArtist = "" }));
        Assert.Equal("Singer", LibrarySongsViewModel.AlbumArtistSortKey(
            new Track { Artist = "Singer", AlbumArtist = "   " }));
    }

    [Fact]
    public void Songs_AlbumArtist_DescendingReversesArtistButKeepsAlbumOrder()
    {
        var tracks = new List<Track>
        {
            new() { Title = "A1", Artist = "Alpha", AlbumArtist = "Alpha", Album = "Solo", Year = 2001, TrackNumber = 1 },
            new() { Title = "B1", Artist = "Beta",  AlbumArtist = "Beta",  Album = "One", Year = 2010, TrackNumber = 1 },
            new() { Title = "B2", Artist = "Beta",  AlbumArtist = "Beta",  Album = "One", Year = 2010, TrackNumber = 2 },
        };

        var result = SortSongs(tracks, "Album Artist", sortAsc: false);

        // Beta group first, but tracks inside the album stay in ascending track order.
        Assert.Equal(new[] { "B1", "B2", "A1" }, result.Select(t => t.Title));
    }

    // ── Songs: Year ──

    [Fact]
    public void Songs_Year_AscendingOrdersOldestFirstThenAlbumTrack()
    {
        var tracks = new List<Track>
        {
            new() { Title = "New2", Album = "N", Year = 2024, TrackNumber = 2 },
            new() { Title = "Old",  Album = "O", Year = 1999, TrackNumber = 5 },
            new() { Title = "New1", Album = "N", Year = 2024, TrackNumber = 1 },
        };

        var result = SortSongs(tracks, "Year", sortAsc: true);

        Assert.Equal(new[] { "Old", "New1", "New2" }, result.Select(t => t.Title));
    }

    [Fact]
    public void Songs_Year_DescendingOrdersNewestFirst()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Old",  Album = "O", Year = 1999, TrackNumber = 1 },
            new() { Title = "New1", Album = "N", Year = 2024, TrackNumber = 1 },
            new() { Title = "New2", Album = "N", Year = 2024, TrackNumber = 2 },
        };

        var result = SortSongs(tracks, "Year", sortAsc: false);

        // Newest year first; album track order stays ascending within the year.
        Assert.Equal(new[] { "New1", "New2", "Old" }, result.Select(t => t.Title));
    }

    [Fact]
    public void Songs_Sort_IsStableForEqualKeys()
    {
        // Four tracks with identical sort keys all the way down — the input order
        // must survive (OrderBy/ThenBy are stable; this locks that contract).
        var tracks = new List<Track>
        {
            new() { Title = "Same", Artist = "X", AlbumArtist = "X", Album = "A", Year = 2000, TrackNumber = 1, FilePath = "1" },
            new() { Title = "Same", Artist = "X", AlbumArtist = "X", Album = "A", Year = 2000, TrackNumber = 1, FilePath = "2" },
            new() { Title = "Same", Artist = "X", AlbumArtist = "X", Album = "A", Year = 2000, TrackNumber = 1, FilePath = "3" },
            new() { Title = "Same", Artist = "X", AlbumArtist = "X", Album = "A", Year = 2000, TrackNumber = 1, FilePath = "4" },
        };

        var result = SortSongs(tracks, "Album Artist", sortAsc: true);

        Assert.Equal(new[] { "1", "2", "3", "4" }, result.Select(t => t.FilePath));
    }

    // ── Albums grid sort modes ──

    private static Album Alb(string name, string artist, int year,
        DateTime? dateAdded = null, int playCount = 0) => new()
    {
        Name = name,
        Artist = artist,
        Year = year,
        Tracks = new List<Track>
        {
            new()
            {
                Title = name + " t1",
                DateAdded = dateAdded ?? new DateTime(2026, 1, 1),
                PlayCount = playCount,
            },
        },
    };

    [Fact]
    public void Albums_AlbumArtistMode_ArtistsAlphabeticalThenChronological()
    {
        var albums = new List<Album>
        {
            Alb("Late",  "beta",  2020),
            Alb("Solo",  "Alpha", 2005),
            Alb("Early", "Beta",  2010),
        };

        var result = LibraryAlbumsViewModel.ApplySortMode(albums, "albumartist", ascending: true).ToList();

        // Case-insensitive artist A→Z, then each artist's releases oldest→newest.
        Assert.Equal(new[] { "Solo", "Early", "Late" }, result.Select(a => a.Name));
    }

    [Fact]
    public void Albums_YearMode_NewestFirstUnknownYearsLast()
    {
        var albums = new List<Album>
        {
            Alb("Middle",  "A", 2010),
            Alb("Unknown", "A", 0),
            Alb("Newest",  "A", 2024),
        };

        var result = LibraryAlbumsViewModel.ApplySortMode(albums, "year", ascending: false).ToList();

        Assert.Equal(new[] { "Newest", "Middle", "Unknown" }, result.Select(a => a.Name));
    }

    [Fact]
    public void Albums_YearMode_TiesBreakByArtistThenName()
    {
        var albums = new List<Album>
        {
            Alb("Zed",   "Beta",  2020),
            Alb("Apple", "Beta",  2020),
            Alb("Any",   "Alpha", 2020),
        };

        var result = LibraryAlbumsViewModel.ApplySortMode(albums, "year", ascending: false).ToList();

        Assert.Equal(new[] { "Any", "Apple", "Zed" }, result.Select(a => a.Name));
    }

    [Fact]
    public void Albums_ExistingModes_SurviveTheExtraction()
    {
        var albums = new List<Album>
        {
            Alb("Older", "A", 2000, dateAdded: new DateTime(2026, 1, 1), playCount: 9),
            Alb("Newer", "B", 2001, dateAdded: new DateTime(2026, 6, 1), playCount: 2),
        };

        Assert.Equal(new[] { "Newer", "Older" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "dateadded", ascending: false).Select(a => a.Name));
        Assert.Equal(new[] { "Older", "Newer" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "mostplayed", ascending: false).Select(a => a.Name));
        // Unrecognized/default mode leaves the incoming order untouched.
        Assert.Equal(new[] { "Older", "Newer" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "default", ascending: true).Select(a => a.Name));
    }

    // ── Albums: alphabetical sort + direction (issue #33) ──

    [Fact]
    public void Albums_TitleMode_SortsAlphabeticallyIgnoringCase()
    {
        var albums = new List<Album>
        {
            Alb("zebra",   "X", 2001),
            Alb("Apple",   "Y", 2002),
            Alb("mango",   "Z", 2003),
        };

        Assert.Equal(new[] { "Apple", "mango", "zebra" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "title", ascending: true).Select(a => a.Name));
    }

    [Fact]
    public void Albums_TitleMode_Descending_ReversesTheOrder()
    {
        var albums = new List<Album>
        {
            Alb("Apple", "Y", 2002),
            Alb("zebra", "X", 2001),
            Alb("mango", "Z", 2003),
        };

        Assert.Equal(new[] { "zebra", "mango", "Apple" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "title", ascending: false).Select(a => a.Name));
    }

    [Fact]
    public void Albums_TitleMode_TiesBreakByArtist()
    {
        // Same title from two artists — the tie-break stays ascending in both
        // directions so a reversal doesn't scramble equal-titled releases.
        var albums = new List<Album>
        {
            Alb("Greatest Hits", "Zephyr", 2001),
            Alb("Greatest Hits", "Alpha",  2002),
        };

        Assert.Equal(new[] { "Alpha", "Zephyr" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "title", ascending: true).Select(a => a.Artist));
        Assert.Equal(new[] { "Alpha", "Zephyr" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "title", ascending: false).Select(a => a.Artist));
    }

    [Fact]
    public void Albums_EveryMode_HonoursTheDirectionFlag()
    {
        var albums = new List<Album>
        {
            Alb("Older", "A", 2000, dateAdded: new DateTime(2026, 1, 1), playCount: 9),
            Alb("Newer", "B", 2001, dateAdded: new DateTime(2026, 6, 1), playCount: 2),
        };

        // Each mode's natural direction, then its opposite.
        Assert.Equal(new[] { "Newer", "Older" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "dateadded", ascending: false).Select(a => a.Name));
        Assert.Equal(new[] { "Older", "Newer" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "dateadded", ascending: true).Select(a => a.Name));

        Assert.Equal(new[] { "Older", "Newer" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "mostplayed", ascending: false).Select(a => a.Name));
        Assert.Equal(new[] { "Newer", "Older" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "mostplayed", ascending: true).Select(a => a.Name));

        Assert.Equal(new[] { "Newer", "Older" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "year", ascending: false).Select(a => a.Name));
        Assert.Equal(new[] { "Older", "Newer" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "year", ascending: true).Select(a => a.Name));

        Assert.Equal(new[] { "Older", "Newer" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "albumartist", ascending: true).Select(a => a.Name));
        Assert.Equal(new[] { "Newer", "Older" },
            LibraryAlbumsViewModel.ApplySortMode(albums, "albumartist", ascending: false).Select(a => a.Name));
    }

    [Fact]
    public void Albums_NaturalDirection_IsDescendingOnlyForMostRecentModes()
    {
        // Picking "Year" or "Recently added" means latest first; picking "Title" or
        // "Album Artist" means A→Z.
        Assert.True(LibraryAlbumsViewModel.IsDescendingByDefault("dateadded"));
        Assert.True(LibraryAlbumsViewModel.IsDescendingByDefault("mostplayed"));
        Assert.True(LibraryAlbumsViewModel.IsDescendingByDefault("year"));
        Assert.False(LibraryAlbumsViewModel.IsDescendingByDefault("title"));
        Assert.False(LibraryAlbumsViewModel.IsDescendingByDefault("albumartist"));
        Assert.False(LibraryAlbumsViewModel.IsDescendingByDefault("default"));
    }
}
