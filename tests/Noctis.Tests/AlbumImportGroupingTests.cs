using System.Collections.Generic;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Covers two import-grouping fixes:
///   #1 Various-Artists compilations must collapse into a single album.
///   #2 Album cover art must be chosen deterministically (lowest disc/track).
/// </summary>
public class AlbumImportGroupingTests
{
    // ── Fix #1: Various-Artists compilation grouping ──

    [Fact]
    public void ResolveAlbumArtist_CompilationWithoutExplicitAlbumArtist_ReturnsVariousArtists()
    {
        var result = Track.ResolveAlbumArtist(explicitAlbumArtist: "", performer: "Bobby Helms", isCompilation: true);
        Assert.Equal("Various Artists", result);
    }

    [Fact]
    public void ResolveAlbumArtist_CompilationWithExplicitAlbumArtist_HonorsExplicit()
    {
        // An explicit album-artist tag already groups correctly; do not override it.
        var result = Track.ResolveAlbumArtist("Now That's What I Call Christmas", "Bobby Helms", isCompilation: true);
        Assert.Equal("Now That's What I Call Christmas", result);
    }

    [Fact]
    public void ResolveAlbumArtist_NonCompilationWithoutExplicit_FallsBackToPerformer()
    {
        var result = Track.ResolveAlbumArtist("", "Mariah Carey", isCompilation: false);
        Assert.Equal("Mariah Carey", result);
    }

    [Fact]
    public void ResolveAlbumArtist_ExplicitAlbumArtist_TakesPrecedence()
    {
        var result = Track.ResolveAlbumArtist("Mariah Carey", "Some Featured Performer", isCompilation: false);
        Assert.Equal("Mariah Carey", result);
    }

    [Fact]
    public void ResolveAlbumArtist_NoInformation_ReturnsUnknownArtist()
    {
        var result = Track.ResolveAlbumArtist("", "", isCompilation: false);
        Assert.Equal("Unknown Artist", result);
    }

    [Fact]
    public void ResolveAlbumArtist_WhitespaceExplicit_TreatedAsEmpty()
    {
        var result = Track.ResolveAlbumArtist("   ", "", isCompilation: true);
        Assert.Equal("Various Artists", result);
    }

    [Fact]
    public void CompilationTracks_WithDifferentPerformers_GroupIntoOneAlbum()
    {
        var aa1 = Track.ResolveAlbumArtist("", "Bobby Helms", isCompilation: true);
        var aa2 = Track.ResolveAlbumArtist("", "Mariah Carey", isCompilation: true);

        var id1 = Track.ComputeAlbumId(aa1, "Essential Christmas");
        var id2 = Track.ComputeAlbumId(aa2, "Essential Christmas");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void NonCompilationTracks_WithDifferentArtists_StayInSeparateAlbums()
    {
        // Regression guard: ordinary singles by different artists must NOT be merged.
        var aa1 = Track.ResolveAlbumArtist("", "Bobby Helms", isCompilation: false);
        var aa2 = Track.ResolveAlbumArtist("", "Mariah Carey", isCompilation: false);

        var id1 = Track.ComputeAlbumId(aa1, "Jingle Bell Rock");
        var id2 = Track.ComputeAlbumId(aa2, "Merry Christmas");

        Assert.NotEqual(id1, id2);
    }

    // ── Fix #2: deterministic album-art representative ──

    [Fact]
    public void SelectArtworkRepresentative_PicksLowestDiscThenTrack()
    {
        var tracks = new List<Track>
        {
            new() { Title = "C", DiscNumber = 2, TrackNumber = 1 },
            new() { Title = "A", DiscNumber = 1, TrackNumber = 5 },
            new() { Title = "B", DiscNumber = 1, TrackNumber = 1 },
        };

        var rep = Album.SelectArtworkRepresentative(tracks);

        Assert.Equal("B", rep!.Title);
    }

    [Fact]
    public void SelectArtworkRepresentative_TreatsDiscZeroAsOne()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Disc0Track2", DiscNumber = 0, TrackNumber = 2 },
            new() { Title = "Disc1Track1", DiscNumber = 1, TrackNumber = 1 },
        };

        var rep = Album.SelectArtworkRepresentative(tracks);

        Assert.Equal("Disc1Track1", rep!.Title);
    }

    [Fact]
    public void SelectArtworkRepresentative_IsDeterministicRegardlessOfInputOrder()
    {
        var a = new Track { Title = "1", DiscNumber = 1, TrackNumber = 1 };
        var b = new Track { Title = "2", DiscNumber = 1, TrackNumber = 2 };

        var forward = Album.SelectArtworkRepresentative(new List<Track> { a, b });
        var reverse = Album.SelectArtworkRepresentative(new List<Track> { b, a });

        Assert.Equal(forward!.Title, reverse!.Title);
        Assert.Equal("1", forward.Title);
    }

    [Fact]
    public void SelectArtworkRepresentative_EmptyReturnsNull()
    {
        Assert.Null(Album.SelectArtworkRepresentative(new List<Track>()));
    }
}
