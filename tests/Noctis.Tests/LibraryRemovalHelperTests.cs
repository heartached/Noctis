using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class LibraryRemovalHelperTests
{
    private static Track Local(string path) => new() { FilePath = path, SourceType = SourceType.Local };
    private static Track Remote(string path, SourceType type) => new() { FilePath = path, SourceType = type };

    [Fact]
    public void SelectTrashablePaths_KeepsLocalFiles()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(@"C:\music\a.flac"),
            Local(@"C:\music\b.mp3"),
        });

        Assert.Equal(new[] { @"C:\music\a.flac", @"C:\music\b.mp3" }, paths);
    }

    [Fact]
    public void SelectTrashablePaths_SkipsRemoteSources()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(@"C:\music\a.flac"),
            Remote(@"\\nas\share\b.flac", SourceType.Smb),
            Remote("http://host/c.flac", SourceType.Navidrome),
        });

        Assert.Equal(new[] { @"C:\music\a.flac" }, paths);
    }

    [Fact]
    public void SelectTrashablePaths_SkipsEmptyPaths()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(""),
            Local("   "),
            Local(@"C:\music\a.flac"),
        });

        Assert.Equal(new[] { @"C:\music\a.flac" }, paths);
    }

    [Fact]
    public void SelectTrashablePaths_DeduplicatesPaths()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(@"C:\music\a.flac"),
            Local(@"C:\music\a.flac"),
        });

        Assert.Single(paths);
    }

    [Fact]
    public async Task TrashWithRetries_RetriesUntilHandleReleases()
    {
        // The player releases a removed track's handle asynchronously, so the first
        // trash attempt can fail with a sharing violation. It must be retried.
        var attempts = 0;
        await LibraryRemovalHelper.TrashWithRetriesAsync(
            new[] { @"C:\music\playing.flac" },
            _ => ++attempts >= 3,   // fails twice (handle still open), then succeeds
            new[] { 0, 1, 1, 1 });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task TrashWithRetries_StopsAfterFirstSuccessPerPath()
    {
        var attemptsByPath = new Dictionary<string, int>();
        await LibraryRemovalHelper.TrashWithRetriesAsync(
            new[] { @"C:\music\a.flac", @"C:\music\b.flac" },
            p =>
            {
                attemptsByPath[p] = attemptsByPath.GetValueOrDefault(p) + 1;
                return p.EndsWith("a.flac") || attemptsByPath[p] >= 2; // a: instant, b: second try
            },
            new[] { 0, 1, 1 });

        Assert.Equal(1, attemptsByPath[@"C:\music\a.flac"]);
        Assert.Equal(2, attemptsByPath[@"C:\music\b.flac"]);
    }

    [Fact]
    public async Task TrashWithRetries_GivesUpAfterScheduleExhausted()
    {
        var attempts = 0;
        var done = await LibraryRemovalHelper.TrashWithRetriesAsync(
            new[] { @"C:\music\stuck.flac" },
            _ => { attempts++; return false; },   // permanently locked
            new[] { 0, 1, 1 });

        Assert.Equal(3, attempts);   // one attempt per schedule slot, then stop — never throws
        Assert.Empty(done);
    }

    [Fact]
    public async Task TrashWithRetries_ReportsDonePaths()
    {
        var done = await LibraryRemovalHelper.TrashWithRetriesAsync(
            new[] { @"C:\music\a.flac", @"C:\music\stuck.flac" },
            p => p.EndsWith("a.flac"),
            new[] { 0, 1 });

        Assert.Equal(new[] { @"C:\music\a.flac" }, done);
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-removal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TrashSidecarFiles_TrashesMatchingLyricSidecars()
    {
        var dir = MakeTempDir();
        try
        {
            var lrc = Path.Combine(dir, "01 song.lrc");
            var txt = Path.Combine(dir, "01 song.txt");
            File.WriteAllText(lrc, "[00:01.00] hi");
            File.WriteAllText(txt, "hi");

            var trashed = new List<string>();
            LibraryRemovalHelper.TrashSidecarFiles(
                new[] { Path.Combine(dir, "01 song.flac"), Path.Combine(dir, "02 other.flac") },
                p => { trashed.Add(p); return true; });

            // Both existing sidecars (synced .lrc, plain .txt) are trashed;
            // the track without any is skipped.
            Assert.Equal(new[] { lrc, txt }, trashed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CleanupEmptiedFolders_TrashesLeftoverOnlyFolderAndEmptiedParent()
    {
        // Downloads-like protected root > artist > album(cover + lrc): the album and
        // the then-empty artist folder are trashed; the protected root survives.
        var root = MakeTempDir();
        try
        {
            var artist = Path.Combine(root, "Artist");
            var album = Path.Combine(artist, "Album");
            Directory.CreateDirectory(album);
            File.WriteAllText(Path.Combine(album, "cover.png"), "img");
            File.WriteAllText(Path.Combine(album, "01 song.lrc"), "lrc");

            var trashed = new List<string>();
            await LibraryRemovalHelper.CleanupEmptiedFoldersAsync(
                new[] { album },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                d => { trashed.Add(d); Directory.Delete(d, true); return true; },
                new[] { 0 });

            Assert.Equal(new[] { album, artist }, trashed);
            Assert.True(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CleanupEmptiedFolders_RetriesTransientDirectoryTrashFailure()
    {
        // Observed in the field: the album folder lands in the bin but the shell
        // still holds a transient handle, so the emptied artist folder's one-shot
        // trash fails and it lingers. A retry must finish the sweep — including when
        // a trash actually landed but reported failure ("already gone" = success).
        var root = MakeTempDir();
        try
        {
            var artist = Path.Combine(root, "Artist");
            var album = Path.Combine(artist, "Album");
            Directory.CreateDirectory(album);
            File.WriteAllText(Path.Combine(album, "cover.png"), "img");

            var calls = new List<string>();
            await LibraryRemovalHelper.CleanupEmptiedFoldersAsync(
                new[] { album },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                d =>
                {
                    calls.Add(d);
                    Directory.Delete(d, true);
                    // Album: trash lands but reports failure. Artist: fails once
                    // (still on disk), succeeds on the retry.
                    if (d == album) return false;
                    if (d == artist && calls.Count(c => c == artist) == 1)
                    {
                        Directory.CreateDirectory(artist); // simulate "trash failed, folder still there"
                        return false;
                    }
                    return true;
                },
                new[] { 0, 1, 1 });

            Assert.False(Directory.Exists(artist));
            Assert.Equal(2, calls.Count(c => c == artist));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("keep.zip")]     // unknown file type
    [InlineData("track.mp3")]    // remaining audio
    public async Task CleanupEmptiedFolders_LeavesFolderWithMeaningfulContent(string keeper)
    {
        var root = MakeTempDir();
        try
        {
            var album = Path.Combine(root, "Album");
            Directory.CreateDirectory(album);
            File.WriteAllText(Path.Combine(album, "cover.png"), "img");
            File.WriteAllText(Path.Combine(album, keeper), "data");

            var trashed = new List<string>();
            await LibraryRemovalHelper.CleanupEmptiedFoldersAsync(
                new[] { album },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                d => { trashed.Add(d); return true; },
                new[] { 0 });

            Assert.Empty(trashed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CleanupEmptiedFolders_LeavesFolderWithSubdirectories()
    {
        var root = MakeTempDir();
        try
        {
            var artist = Path.Combine(root, "Artist");
            Directory.CreateDirectory(Path.Combine(artist, "Other Album"));

            var trashed = new List<string>();
            await LibraryRemovalHelper.CleanupEmptiedFoldersAsync(
                new[] { artist },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                d => { trashed.Add(d); return true; },
                new[] { 0 });

            Assert.Empty(trashed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CleanupEmptiedFolders_NeverTouchesProtectedDirs()
    {
        var root = MakeTempDir();
        try
        {
            // The emptied folder itself is protected (e.g. a configured music root).
            var trashed = new List<string>();
            await LibraryRemovalHelper.CleanupEmptiedFoldersAsync(
                new[] { root },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                d => { trashed.Add(d); return true; },
                new[] { 0 });

            Assert.Empty(trashed);
        }
        finally { Directory.Delete(root, true); }
    }
}
