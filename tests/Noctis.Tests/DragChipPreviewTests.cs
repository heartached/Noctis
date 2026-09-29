using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The floating drag chip (09-22): the OS drag shows no picture of what is moving, so a
/// chip follows the pointer with the artwork, a title / subtitle and the song count.
/// </summary>
public class DragChipPreviewTests
{
    private static Track T(string title, string artist, string? art = null)
        => new() { Id = Guid.NewGuid(), Title = title, Artist = artist, FilePath = $"C:/m/{title}.flac", AlbumArtworkPath = art };

    [Fact]
    public void Track_ShowsTitleArtistAndArtwork_NoCount()
    {
        var track = T("Ignorantes", "Bad Bunny & Sech", "C:/art/ign.jpg");
        var p = DragFileBehavior.BuildPreview(track, new[] { track })!;
        Assert.Equal("Ignorantes", p.Title);
        Assert.Equal("Bad Bunny & Sech", p.Subtitle);
        Assert.Equal("C:/art/ign.jpg", p.ArtworkPath);
        Assert.Equal(1, p.Count); // the chip hides the badge for one song
    }

    [Fact]
    public void Album_ShowsNameArtistAndSongCount()
    {
        var tracks = new List<Track> { T("One", "Artist"), T("Two", "Artist", "C:/art/two.jpg"), T("Three", "Artist") };
        var album = new Album { Id = Guid.NewGuid(), Name = "LAS QUE NO IBAN A SALIR", Artist = "Bad Bunny", Tracks = tracks };
        var p = DragFileBehavior.BuildPreview(album, tracks)!;
        Assert.Equal("LAS QUE NO IBAN A SALIR", p.Title);
        Assert.Equal("Bad Bunny", p.Subtitle);
        Assert.Equal("C:/art/two.jpg", p.ArtworkPath); // falls back to a track's art
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void ExplicitFlag_FollowsTheDraggedTrackOrAlbum()
    {
        var clean = T("Clean", "Artist");
        var dirty = T("DEMO WRECK", "Juice WRLD");
        dirty.IsExplicit = true;

        Assert.True(DragFileBehavior.BuildPreview(dirty, new[] { dirty })!.IsExplicit);
        Assert.False(DragFileBehavior.BuildPreview(clean, new[] { clean })!.IsExplicit);

        // An album carries the E when any of its tracks does, as its tile title does.
        var tracks = new List<Track> { clean, dirty };
        var album = new Album { Id = Guid.NewGuid(), Name = "DEMO WRECK - Single", Artist = "Juice WRLD", Tracks = tracks };
        Assert.True(DragFileBehavior.BuildPreview(album, tracks)!.IsExplicit);
    }

    [Fact]
    public void NoTracks_NoChip()
        => Assert.Null(DragFileBehavior.BuildPreview(null, Array.Empty<Track>()));
}
