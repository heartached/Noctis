using System;
using System.Collections.Generic;
using System.Reflection;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
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
        // but a folder directly on the volume root has no artist above it. The
        // shallow fixture sits on the primary volume: only a true filesystem root
        // is parentless everywhere (D:\ has no name, but /mnt/other does).
        var deep = TestPaths.Other("Rips", "Artist", "Album", "01 Song.wav");
        Assert.Equal(("Artist", "Album"), FolderMetadata.InferArtistAlbum(deep, Roots));

        var shallow = TestPaths.Primary("LooseAlbum", "track.wav");
        Assert.Equal(((string?)null, "LooseAlbum"), FolderMetadata.InferArtistAlbum(shallow, Roots));
    }

    /// <summary>
    /// GitHub #108: files added where they are often sit in Downloads or on the Desktop.
    /// Those folders are nobody's artist or album — every untagged file dropped from a
    /// Downloads subfolder was credited to the artist "Downloads", and one sitting directly
    /// in Downloads became the album "Downloads" by an artist named after the account.
    /// </summary>
    [Fact]
    public void WellKnownUserFolders_AreNeverCredited()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) Assert.Skip("no user profile folder on this machine");
        var none = Array.Empty<string>();
        var downloads = System.IO.Path.Combine(profile, "Downloads");

        Assert.Equal(((string?)null, (string?)null),
            FolderMetadata.InferArtistAlbum(System.IO.Path.Combine(downloads, "loose.mp3"), none));
        Assert.Equal(((string?)null, "hftfviceusgskinconcept"),
            FolderMetadata.InferArtistAlbum(System.IO.Path.Combine(downloads, "hftfviceusgskinconcept", "a.mp3"), none));

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
            Assert.Equal(((string?)null, "Mix"),
                FolderMetadata.InferArtistAlbum(System.IO.Path.Combine(desktop, "Mix", "a.mp3"), none));
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

    /// <summary>
    /// Android device run, 2026-09-22: every SAF track's FilePath is a content:// document
    /// URI, and the load-time backfill fed it to this path-shape heuristic, which read the
    /// URI's percent-encoded segments as folders and wrote artist/albumArtist
    /// "primary%3AMusic%2FTones" and album "document" into the library — inventing a phantom
    /// album on the way. Driven through LibraryService.BackfillFolderMetadata itself, the
    /// method that had no localPath guard, so the regression is pinned where it happened.
    /// </summary>
    [Fact]
    public void SafContentUri_IsSkippedByTheBackfill_KeepingItsOwnMetadata()
    {
        const string uri = "content://com.android.externalstorage.documents/tree/" +
                           "primary%3AMusic%2FTones/document/primary%3AMusic%2FTones%2F01%20broken.mp3";
        var tagged = new Track
        {
            Id = Guid.NewGuid(),
            FilePath = uri,
            Title = "Tone A",
            Artist = "Noctis Test",
            AlbumArtist = "Noctis Test",
            Album = "Tones",
            AlbumId = Track.ComputeAlbumId("Noctis Test", "Tones"),
            TrackNumber = 1,
        };
        // The untagged case is the one that actually corrupted on device: placeholders are
        // exactly what invites the heuristic in.
        var untagged = UntaggedWav(uri);

        var settings = new AppSettings();
        settings.MusicFolders.Add(Root);
        var backfill = typeof(LibraryService).GetMethod("BackfillFolderMetadata",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var changed = (bool)backfill.Invoke(null, new object[] { new List<Track> { tagged, untagged }, settings })!;

        Assert.False(changed);
        Assert.Equal("Noctis Test", tagged.Artist);
        Assert.Equal("Noctis Test", tagged.AlbumArtist);
        Assert.Equal("Tones", tagged.Album);
        Assert.Equal(Track.ComputeAlbumId("Noctis Test", "Tones"), tagged.AlbumId);

        Assert.Equal("Unknown Artist", untagged.Artist);
        Assert.Equal("Unknown Album", untagged.Album);
        Assert.Equal(Track.UnknownAlbumBucketId, untagged.AlbumId);
        Assert.Equal(0, untagged.TrackNumber);
    }
}
