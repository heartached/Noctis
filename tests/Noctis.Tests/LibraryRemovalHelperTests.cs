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

    // ── Removed-tracks listing (Settings → Library restore surface) ──

    [Fact]
    public void SelectRemovedEntries_ListsOnlyFilesStillOnDisk()
    {
        // A kept file whose exclusion is still active is restorable; one deleted
        // (or moved) since removal is not — it drops out of the list.
        var kept = TestPaths.Primary("music", "kept.flac");
        var gone = TestPaths.Primary("music", "gone.flac");

        var entries = LibraryRemovalHelper.SelectRemovedEntries(
            new[] { kept, gone },
            p => p == kept);

        var entry = Assert.Single(entries);
        Assert.Equal(kept, entry.FilePath);
    }

    [Fact]
    public void SelectRemovedEntries_SkipsBlankAndDuplicatePaths()
    {
        // ExcludedFilePaths is treated case-insensitively everywhere in
        // LibraryService — the restore list must not show one file twice.
        var path = TestPaths.Primary("music", "a.flac");

        var entries = LibraryRemovalHelper.SelectRemovedEntries(
            new[] { "", "   ", path, path.ToUpperInvariant() },
            _ => true);

        Assert.Single(entries);
    }

    [Fact]
    public void SelectRemovedEntries_BuildsDisplayFieldsFromPath()
    {
        // The library entry (and its metadata) is gone, so the row shows the
        // file name without extension plus the folder it lives in.
        var path = TestPaths.Primary("music", "Artist", "Album", "01 song.flac");

        var entries = LibraryRemovalHelper.SelectRemovedEntries(new[] { path }, _ => true);

        var entry = Assert.Single(entries);
        Assert.Equal("01 song", entry.Title);
        Assert.Equal(TestPaths.Primary("music", "Artist", "Album"), entry.Folder);
        Assert.Equal(path, entry.FilePath);
    }

    [Fact]
    public void SelectRemovedEntries_SortsByTitle()
    {
        var entries = LibraryRemovalHelper.SelectRemovedEntries(
            new[]
            {
                TestPaths.Primary("music", "b side.mp3"),
                TestPaths.Primary("music", "Anthem.flac"),
                TestPaths.Primary("music", "chorus.ogg"),
            },
            _ => true);

        Assert.Equal(new[] { "Anthem", "b side", "chorus" }, entries.Select(e => e.Title));
    }

    [Fact]
    public void SelectRemovedEntries_WithRealFiles_ListsExistingOnly()
    {
        // Round trip of the on-disk half of restore: removal kept the file and
        // excluded its path — the list offers exactly that file back.
        var dir = MakeTempDir();
        try
        {
            var kept = Path.Combine(dir, "01 song.flac");
            File.WriteAllText(kept, "audio");
            var gone = Path.Combine(dir, "02 gone.flac");

            var entries = LibraryRemovalHelper.SelectRemovedEntries(new[] { kept, gone }, File.Exists);

            var entry = Assert.Single(entries);
            Assert.Equal(kept, entry.FilePath);
            Assert.Equal("01 song", entry.Title);
            Assert.Equal(dir, entry.Folder);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Whole-folder recycle (one restorable bin item per fully-removed album) ──

    [Fact]
    public void GroupByDirectory_GroupsFilesByParentFolder()
    {
        var album = TestPaths.Primary("music", "Album");
        var grouped = LibraryRemovalHelper.GroupByDirectory(new[]
        {
            Path.Combine(album, "01.flac"),
            Path.Combine(album, "02.flac"),
            TestPaths.Primary("music", "Other", "03.flac"),
        });

        Assert.Equal(2, grouped.Count);
        Assert.Equal(2, grouped[album].Count);
        Assert.Single(grouped[TestPaths.Primary("music", "Other")]);
    }

    [Fact]
    public void QualifiesForWholeFolderTrash_WhenAllAudioIsBeingRemoved()
    {
        // Removing the album's only audio: cover art and the track's own lyric
        // sidecar ride along, so the folder can go to the bin as ONE item — a
        // single Explorer "Restore" brings the whole album back.
        var dir = MakeTempDir();
        try
        {
            var audio = Path.Combine(dir, "01 song.m4a");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(Path.Combine(dir, "cover.jpg"), "img");
            File.WriteAllText(Path.Combine(dir, "01 song.lrc"), "lrc");

            Assert.True(LibraryRemovalHelper.QualifiesForWholeFolderTrash(
                dir, new[] { audio }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void QualifiesForWholeFolderTrash_NotWhenOtherAudioRemains()
    {
        var dir = MakeTempDir();
        try
        {
            var removing = Path.Combine(dir, "01 song.m4a");
            File.WriteAllText(removing, "audio");
            File.WriteAllText(Path.Combine(dir, "02 staying.mp3"), "audio");

            Assert.False(LibraryRemovalHelper.QualifiesForWholeFolderTrash(
                dir, new[] { removing }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void QualifiesForWholeFolderTrash_NotWithSubdirectories()
    {
        var dir = MakeTempDir();
        try
        {
            var removing = Path.Combine(dir, "01 song.m4a");
            File.WriteAllText(removing, "audio");
            Directory.CreateDirectory(Path.Combine(dir, "Disc 2"));

            Assert.False(LibraryRemovalHelper.QualifiesForWholeFolderTrash(
                dir, new[] { removing }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("playlist.m3u")]  // user-authored playlist
    [InlineData("notes.txt")]     // .txt that is NOT the removed track's sidecar
    public void QualifiesForWholeFolderTrash_NotWithUserAuthoredFiles(string keeper)
    {
        var dir = MakeTempDir();
        try
        {
            var removing = Path.Combine(dir, "01 song.m4a");
            File.WriteAllText(removing, "audio");
            File.WriteAllText(Path.Combine(dir, keeper), "data");

            Assert.False(LibraryRemovalHelper.QualifiesForWholeFolderTrash(
                dir, new[] { removing }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void QualifiesForWholeFolderTrash_NotForProtectedDirs()
    {
        // A configured music root full of loose tracks must never be binned whole,
        // even when every one of them is part of the removal.
        var dir = MakeTempDir();
        try
        {
            var removing = Path.Combine(dir, "01 song.m4a");
            File.WriteAllText(removing, "audio");

            Assert.False(LibraryRemovalHelper.QualifiesForWholeFolderTrash(
                dir, new[] { removing },
                new HashSet<string>(new[] { dir }, StringComparer.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TrashWholeDirectoryWithRetries_RetriesWhileHandleHeld()
    {
        // Same race as per-file trash: the player releases the removed track's
        // handle asynchronously, and a directory move is blocked while any handle
        // is open beneath it.
        var dir = MakeTempDir();
        try
        {
            var audio = Path.Combine(dir, "01 song.m4a");
            File.WriteAllText(audio, "audio");

            var attempts = 0;
            var trashed = await LibraryRemovalHelper.TrashWholeDirectoryWithRetriesAsync(
                dir, new[] { audio },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                d =>
                {
                    if (++attempts < 3) return false; // handle still open
                    Directory.Delete(d, true);
                    return true;
                },
                new[] { 0, 1, 1, 1 });

            Assert.True(trashed);
            Assert.Equal(3, attempts);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TrashWholeDirectoryWithRetries_BailsOutWhenFolderDoesNotQualify()
    {
        var dir = MakeTempDir();
        try
        {
            var removing = Path.Combine(dir, "01 song.m4a");
            File.WriteAllText(removing, "audio");
            File.WriteAllText(Path.Combine(dir, "02 staying.mp3"), "audio");

            var attempts = 0;
            var trashed = await LibraryRemovalHelper.TrashWholeDirectoryWithRetriesAsync(
                dir, new[] { removing },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                _ => { attempts++; return true; },
                new[] { 0, 1 });

            Assert.False(trashed);
            Assert.Equal(0, attempts); // never even attempted — falls back to per-file
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TrashLocalFilesCore_RecyclesFullyRemovedAlbumAsOneItem()
    {
        // Both tracks of the album are being removed: the folder (audio + cover +
        // sidecar inside) must land in the bin as ONE item, with no separate
        // per-file trashes — restoring that single item restores everything.
        var root = MakeTempDir();
        try
        {
            var album = Path.Combine(root, "Album");
            Directory.CreateDirectory(album);
            var a1 = Path.Combine(album, "01.m4a");
            var a2 = Path.Combine(album, "02.m4a");
            File.WriteAllText(a1, "audio");
            File.WriteAllText(a2, "audio");
            File.WriteAllText(Path.Combine(album, "cover.jpg"), "img");
            File.WriteAllText(Path.Combine(album, "01.lrc"), "lrc");

            var fileTrashes = new List<string>();
            var dirTrashes = new List<string>();
            await LibraryRemovalHelper.TrashLocalFilesCoreAsync(
                new[] { a1, a2 },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                p => { fileTrashes.Add(p); File.Delete(p); return true; },
                d => { dirTrashes.Add(d); Directory.Delete(d, true); return true; },
                new[] { 0 }, new[] { 0 });

            Assert.Empty(fileTrashes);
            Assert.Equal(album, Assert.Single(dirTrashes));
            Assert.False(Directory.Exists(album));
            Assert.True(Directory.Exists(root)); // protected root untouched
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrashLocalFilesCore_PartialAlbumRemovalStaysPerFile()
    {
        var root = MakeTempDir();
        try
        {
            var album = Path.Combine(root, "Album");
            Directory.CreateDirectory(album);
            var removing = Path.Combine(album, "01.m4a");
            File.WriteAllText(removing, "audio");
            File.WriteAllText(Path.Combine(album, "02 staying.m4a"), "audio");
            File.WriteAllText(Path.Combine(album, "cover.jpg"), "img");

            var fileTrashes = new List<string>();
            var dirTrashes = new List<string>();
            await LibraryRemovalHelper.TrashLocalFilesCoreAsync(
                new[] { removing },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                p => { fileTrashes.Add(p); File.Delete(p); return true; },
                d => { dirTrashes.Add(d); Directory.Delete(d, true); return true; },
                new[] { 0 }, new[] { 0 });

            Assert.Equal(new[] { removing }, fileTrashes);
            Assert.Empty(dirTrashes);
            Assert.True(Directory.Exists(album)); // still holds the other track
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrashLocalFilesCore_FolderTrashFailureFallsBackToPerFile()
    {
        // The directory move can stay blocked past every retry (indexer holding the
        // fresh cover, shell handles, …). The audio must then still be trashed
        // per-file — a stuck folder must never turn the removal into a no-op.
        var root = MakeTempDir();
        try
        {
            var album = Path.Combine(root, "Album");
            Directory.CreateDirectory(album);
            var audio = Path.Combine(album, "01.m4a");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(Path.Combine(album, "cover.jpg"), "img");

            var fileTrashes = new List<string>();
            await LibraryRemovalHelper.TrashLocalFilesCoreAsync(
                new[] { audio },
                new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase),
                p => { fileTrashes.Add(p); File.Delete(p); return true; },
                _ => false, // directory move permanently blocked
                new[] { 0 }, new[] { 0 });

            Assert.Equal(new[] { audio }, fileTrashes);
            Assert.False(File.Exists(audio));
            Assert.True(Directory.Exists(album)); // folder stuck, but audio is gone
        }
        finally { Directory.Delete(root, true); }
    }
}
