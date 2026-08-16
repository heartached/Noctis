using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Folder-derived metadata for untagged files (Discord report, v1.4.6): WAV rips
/// carry no tags, so every file fell into "Unknown Artist"/"Unknown Album". The
/// iTunes-style layout <root>/<Artist>/<Album>/NN Title.wav carries the identity;
/// these pin the inference rules and the track-field application.
/// </summary>
public class FolderMetadataTests
{
    private static readonly string Root = TestPaths.Primary("Music");
    private static readonly string[] Roots = { Root };

    // ── InferArtistAlbum ──

    [Fact]
    public void ITunesLayout_YieldsArtistAndAlbum()
    {
        var path = TestPaths.Primary("Music", "Ulrich Schnauss", "A Strangely Isolated Place", "01 Gone Forever.wav");
        Assert.Equal(("Ulrich Schnauss", "A Strangely Isolated Place"),
            FolderMetadata.InferArtistAlbum(path, Roots));
    }

    [Fact]
    public void FileDirectlyInRoot_YieldsNothing()
    {
        var path = TestPaths.Primary("Music", "track.wav");
        Assert.Equal(((string?)null, (string?)null), FolderMetadata.InferArtistAlbum(path, Roots));
    }

    [Fact]
    public void SingleFolderBelowRoot_YieldsAlbumOnly()
    {
        // The root itself must never become the artist credit.
        var path = TestPaths.Primary("Music", "Some Album", "track.wav");
        Assert.Equal(((string?)null, "Some Album"), FolderMetadata.InferArtistAlbum(path, Roots));
    }

    [Theory]
    [InlineData("CD1")]
    [InlineData("Disc 2")]
    [InlineData("disk3")]
    public void DiscSubfolder_IsSkipped(string discFolder)
    {
        var path = TestPaths.Primary("Music", "Artist", "Album", discFolder, "01 Song.wav");
        Assert.Equal(("Artist", "Album"), FolderMetadata.InferArtistAlbum(path, Roots));
    }

    [Fact]
    public void RootMatch_IsCaseInsensitive()
    {
        var path = TestPaths.Primary("MUSIC", "Album Folder", "track.wav");
        Assert.Equal(((string?)null, "Album Folder"), FolderMetadata.InferArtistAlbum(path, Roots));
    }

    [Fact]
    public void OutsideAnyRoot_InfersFromStructure_ButNeverTheVolumeRoot()
    {
        // Drag-drop imports live outside configured roots; structure still counts,
        // but a folder directly on the volume root has no artist above it.
        var deep = TestPaths.Other("Rips", "Artist", "Album", "01 Song.wav");
        Assert.Equal(("Artist", "Album"), FolderMetadata.InferArtistAlbum(deep, Roots));

        var shallow = TestPaths.Other("LooseAlbum", "track.wav");
        Assert.Equal(((string?)null, "LooseAlbum"), FolderMetadata.InferArtistAlbum(shallow, Roots));
    }

    // ── ParseTrackFilename ──

    [Theory]
    [InlineData("01 Gone Forever", 0, 1, "Gone Forever")]
    [InlineData("04 Monday-Paracetamol", 0, 4, "Monday-Paracetamol")]
    [InlineData("12. Track Name", 0, 12, "Track Name")]
    [InlineData("1-01 Song", 1, 1, "Song")]
    [InlineData("2-05 Another", 2, 5, "Another")]
    public void NumberPrefix_ParsesDiscTrackAndCleanTitle(string name, int disc, int track, string title)
    {
        Assert.Equal((disc, track, title), FolderMetadata.ParseTrackFilename(name));
    }

    [Theory]
    [InlineData("2001 A Space Odyssey")] // year, not a track number
    [InlineData("Plain Title")]
    [InlineData("05")]                    // digits only — nothing left for a title
    public void NoUsablePrefix_LeavesTitleAlone(string name)
    {
        Assert.Equal((0, 0, name), FolderMetadata.ParseTrackFilename(name));
    }

    // ── TryApplyToTrack ──

    private static Track UntaggedWav(string path) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        Artist = "Unknown Artist",
        AlbumArtist = "Unknown Artist",
        Album = "Unknown Album",
        AlbumId = Track.UnknownAlbumBucketId,
        TrackNumber = 0,
    };

    [Fact]
    public void UntaggedTrack_GetsFolderIdentityAndFilenameNumber()
    {
        var track = UntaggedWav(TestPaths.Primary("Music", "Ulrich Schnauss", "A Strangely Isolated Place", "01 Gone Forever.wav"));

        var changed = FolderMetadata.TryApplyToTrack(track, Roots);

        Assert.True(changed);
        Assert.Equal("Ulrich Schnauss", track.Artist);
        Assert.Equal("Ulrich Schnauss", track.AlbumArtist);
        Assert.Equal("A Strangely Isolated Place", track.Album);
        Assert.Equal("Gone Forever", track.Title);
        Assert.Equal(1, track.TrackNumber);
        Assert.Equal(Track.ComputeAlbumId("Ulrich Schnauss", "A Strangely Isolated Place"), track.AlbumId);
    }

    [Fact]
    public void FullyTaggedTrack_IsUntouched()
    {
        var track = new Track
        {
            FilePath = TestPaths.Primary("Music", "FolderArtist", "FolderAlbum", "01 X.wav"),
            Title = "Real Title",
            Artist = "Real Artist",
            AlbumArtist = "Real Artist",
            Album = "Real Album",
            AlbumId = Track.ComputeAlbumId("Real Artist", "Real Album"),
            TrackNumber = 3,
        };

        Assert.False(FolderMetadata.TryApplyToTrack(track, Roots));
        Assert.Equal("Real Artist", track.Artist);
        Assert.Equal("Real Album", track.Album);
        Assert.Equal("Real Title", track.Title);
        Assert.Equal(3, track.TrackNumber);
    }

    [Fact]
    public void ArtistPlaceholderWithRealAlbum_InfersArtistAndRekeysAlbumId()
    {
        var track = UntaggedWav(TestPaths.Primary("Music", "FolderArtist", "FolderAlbum", "02 Y.wav"));
        track.Album = "Tagged Album";
        track.AlbumId = Track.ComputeAlbumId("Unknown Artist", "Tagged Album");

        var changed = FolderMetadata.TryApplyToTrack(track, Roots);

        Assert.True(changed);
        Assert.Equal("FolderArtist", track.Artist);
        Assert.Equal("Tagged Album", track.Album); // real tag wins over folder name
        Assert.Equal(Track.ComputeAlbumId("FolderArtist", "Tagged Album"), track.AlbumId);
    }

    [Fact]
    public void RealTitleWithMissingNumber_TakesNumberButKeepsTitle()
    {
        var track = UntaggedWav(TestPaths.Primary("Music", "A", "B", "07 Song.wav"));
        track.Title = "Song"; // real title tag

        FolderMetadata.TryApplyToTrack(track, Roots);

        Assert.Equal("Song", track.Title);
        Assert.Equal(7, track.TrackNumber);
    }

    [Fact]
    public void NothingInferable_ReturnsFalse()
    {
        var track = UntaggedWav(TestPaths.Primary("Music", "Untitled.wav"));

        Assert.False(FolderMetadata.TryApplyToTrack(track, Roots));
        Assert.Equal("Unknown Artist", track.Artist);
        Assert.Equal("Unknown Album", track.Album);
        Assert.Equal(Track.UnknownAlbumBucketId, track.AlbumId);
    }
}
