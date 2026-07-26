using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Guards around the shared "Unknown Artist::Unknown Album" bucket and the
/// managed-import root — the two mechanisms behind dropped files adopting other
/// albums' covers and being relocated into other albums' folders.
/// </summary>
public class UnknownAlbumBucketTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public UnknownAlbumBucketTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Track helpers ──

    [Fact]
    public void UnknownAlbumBucketId_MatchesComputeAlbumId_AnyCasing()
    {
        Assert.Equal(Track.UnknownAlbumBucketId, Track.ComputeAlbumId("Unknown Artist", "Unknown Album"));
        Assert.Equal(Track.UnknownAlbumBucketId, Track.ComputeAlbumId("UNKNOWN ARTIST", "unknown album"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Unknown Album", false)]
    [InlineData("unknown album", false)]
    [InlineData(" Unknown Album ", false)]
    [InlineData("No Me Conoce (Remix) - Single", true)]
    [InlineData("Unknown Pleasures", true)] // real Joy Division album — must not be swallowed
    public void IsRealAlbumName_MatchesPlaceholderByValue(string? album, bool expected)
    {
        Assert.Equal(expected, Track.IsRealAlbumName(album));
    }

    // ── Artwork cache refuses the shared bucket ──

    [Fact]
    public void SaveArtwork_UnknownBucket_WritesNothing()
    {
        var persistence = new PersistenceService(Path.Combine(_dir, "data"));

        persistence.SaveArtwork(Track.UnknownAlbumBucketId, new byte[] { 1, 2, 3 });

        Assert.False(File.Exists(persistence.GetArtworkPath(Track.UnknownAlbumBucketId)));
    }

    [Fact]
    public void SaveArtwork_RealAlbum_StillWrites()
    {
        var persistence = new PersistenceService(Path.Combine(_dir, "data"));
        var albumId = Track.ComputeAlbumId("Future", "I NEVER LIKED YOU");

        persistence.SaveArtwork(albumId, new byte[] { 1, 2, 3 });

        Assert.True(File.Exists(persistence.GetArtworkPath(albumId)));
    }

    // ── Managed-import root selection ──

    [Fact]
    public void SelectManagedImportRoot_SkipsAlbumFolderRoots()
    {
        // The field failure: after the original managed root was scrubbed, a dropped
        // album folder became MusicFolders[0] and loose drops were moved into it.
        var roots = new[]
        {
            TestPaths.Other("ALAC", "Future", "I NEVER LIKED YOU [E]"),
            TestPaths.Other("ALAC", "Tory Lanez", "I Told You (Deluxe Edition) [E]"),
        };

        Assert.Null(MainWindowViewModel.SelectManagedImportRoot(roots));
    }

    [Fact]
    public void SelectManagedImportRoot_PrefersTheManagedFolder_RegardlessOfPosition()
    {
        var managed = TestPaths.Primary("Users", "someone", "Music", "Noctis Imports");
        var roots = new[]
        {
            TestPaths.Other("ALAC", "Future", "I NEVER LIKED YOU [E]"),
            managed,
        };

        Assert.Equal(managed, MainWindowViewModel.SelectManagedImportRoot(roots));
    }

    [Fact]
    public void SelectManagedImportRoot_MatchesLeafCaseInsensitively_AndTrailingSeparator()
    {
        var root = TestPaths.Other("music", "NOCTIS IMPORTS") + Path.DirectorySeparatorChar;

        Assert.Equal(root, MainWindowViewModel.SelectManagedImportRoot(new[] { root }));
    }

    // ── Orphaned artwork selection on removal ──

    private static Track MakeTrack(string artist, string album) => new()
    {
        Id = Guid.NewGuid(),
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        AlbumId = Track.ComputeAlbumId(artist, album),
    };

    [Fact]
    public void SelectOrphanedAlbumIds_AlbumFullyRemoved_IsOrphaned()
    {
        var removed = new[] { MakeTrack("Unknown Artist", "Unknown Album") };

        var orphans = LibraryService.SelectOrphanedAlbumIds(removed, Array.Empty<Track>());

        Assert.Equal(new[] { Track.UnknownAlbumBucketId }, orphans);
    }

    [Fact]
    public void SelectOrphanedAlbumIds_AlbumStillHasTracks_IsKept()
    {
        var removed = new[] { MakeTrack("Future", "I NEVER LIKED YOU") };
        var remaining = new[] { MakeTrack("Future", "I NEVER LIKED YOU") };

        Assert.Empty(LibraryService.SelectOrphanedAlbumIds(removed, remaining));
    }
}
