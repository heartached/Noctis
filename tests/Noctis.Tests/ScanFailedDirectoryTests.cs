using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Guards the scan's "couldn't list" ≠ "deleted" distinction: tracks under a
/// directory whose enumeration failed (cloud provider not running yet, access
/// denied, transient I/O) must be carried forward instead of silently removed,
/// while tracks under directories that listed fine still reconcile normally.
/// </summary>
public class ScanFailedDirectoryTests
{
    private static Track MakeTrack(string filePath) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = filePath,
        Title = Path.GetFileNameWithoutExtension(filePath),
    };

    [Fact]
    public void TrackUnderFailedDirectory_NotSeenByScan_IsCarried()
    {
        var track = MakeTrack(TestPaths.Primary("Music", "Amazon", "song.mp3"));

        var carried = LibraryService.SelectTracksUnderFailedDirectories(
            new[] { track },
            new HashSet<Guid>(),
            new[] { TestPaths.Primary("Music", "Amazon") });

        Assert.Equal(new[] { track }, carried);
    }

    [Fact]
    public void TrackAlreadySeenByScan_IsNotCarriedAgain()
    {
        var track = MakeTrack(TestPaths.Primary("Music", "Amazon", "song.mp3"));

        var carried = LibraryService.SelectTracksUnderFailedDirectories(
            new[] { track },
            new HashSet<Guid> { track.Id },
            new[] { TestPaths.Primary("Music", "Amazon") });

        Assert.Empty(carried);
    }

    [Fact]
    public void TrackOutsideFailedDirectories_IsNotCarried()
    {
        // A folder whose PARENT listed fine but whose entry is simply gone was
        // genuinely deleted — its tracks must still drop out of the library.
        var deleted = MakeTrack(TestPaths.Primary("Music", "iTunes", "gone.mp3"));

        var carried = LibraryService.SelectTracksUnderFailedDirectories(
            new[] { deleted },
            new HashSet<Guid>(),
            new[] { TestPaths.Primary("Music", "Amazon") });

        Assert.Empty(carried);
    }

    [Fact]
    public void FailedDirectoryPrefix_DoesNotMatchSiblingWithSameStem()
    {
        // From the field report: "Amazon" and "Amazon Music" are sibling folders.
        // A failure on one must not capture (or spare) tracks of the other.
        var amazonMusic = MakeTrack(TestPaths.Primary("Music", "Amazon Music", "song.mp3"));
        var amazon = MakeTrack(TestPaths.Primary("Music", "Amazon", "song.mp3"));

        var carried = LibraryService.SelectTracksUnderFailedDirectories(
            new[] { amazonMusic, amazon },
            new HashSet<Guid>(),
            new[] { TestPaths.Primary("Music", "Amazon") });

        Assert.Equal(new[] { amazon }, carried);
    }

    [Fact]
    public void DeeplyNestedTrack_UnderFailedSubfolder_IsCarried()
    {
        var nested = MakeTrack(TestPaths.Primary("Music", "FLAC", "Artist", "Album", "song.flac"));
        var sibling = MakeTrack(TestPaths.Primary("Music", "iTunes", "song.m4a"));

        var carried = LibraryService.SelectTracksUnderFailedDirectories(
            new[] { nested, sibling },
            new HashSet<Guid>(),
            new[] { TestPaths.Primary("Music", "FLAC") });

        Assert.Equal(new[] { nested }, carried);
    }

    [Fact]
    public void FailedDirectoryMatch_FollowsPlatformCaseSensitivity()
    {
        var track = MakeTrack(TestPaths.Primary("Music", "Firesign", "song.mp3"));

        var carried = LibraryService.SelectTracksUnderFailedDirectories(
            new[] { track },
            new HashSet<Guid>(),
            new[] { TestPaths.Primary("music", "FIRESIGN") });

        if (OperatingSystem.IsLinux())
            // Case-differing paths are distinct directories on Linux (AUDIT M22):
            // a failure recorded for music/FIRESIGN says nothing about
            // Music/Firesign, so its tracks must NOT be carried.
            Assert.Empty(carried);
        else
            Assert.Equal(new[] { track }, carried);
    }

    [Fact]
    public void NoFailedDirectories_CarriesNothing()
    {
        var track = MakeTrack(TestPaths.Primary("Music", "Amazon", "song.mp3"));

        var carried = LibraryService.SelectTracksUnderFailedDirectories(
            new[] { track },
            new HashSet<Guid>(),
            Array.Empty<string>());

        Assert.Empty(carried);
    }
}
