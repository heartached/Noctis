using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.Views;

namespace Noctis.Helpers;

/// <summary>
/// Shared "Remove from Library" flow: prompts the user whether to keep files on
/// disk or move them to the OS trash, then removes the tracks from the library.
/// </summary>
public static class LibraryRemovalHelper
{
    /// <summary>
    /// Prompts and applies the user's choice for the given tracks:
    /// Cancel → nothing happens; Keep Files → tracks removed, files left on disk;
    /// Recycle Bin/Trash → local files trashed, then tracks removed.
    /// Returns true when the tracks were removed (caller should update its UI).
    /// </summary>
    public static async Task<bool> RemoveWithPromptAsync(ILibraryService library, IReadOnlyList<Track> tracks)
    {
        if (library == null || tracks == null || tracks.Count == 0) return false;

        var choice = await RemoveFromLibraryDialog.ShowAsync(tracks.Count);
        if (choice == RemoveFromLibraryChoice.Cancel) return false;

        // Snapshot protected roots BEFORE removal: RemoveTracksAsync drops now-empty
        // folders from the configured MusicFolders, and a root scrubbed that way must
        // still be protected from the folder cleanup below.
        var protectedRoots = choice == RemoveFromLibraryChoice.Trash
            ? await GetProtectedRootsAsync()
            : null;

        // Remove from the library FIRST: removal fires LibraryUpdated, which makes the
        // player stop/advance off a removed track and release its file handle. Trashing
        // before removal always ran against that still-open handle, and Windows refuses
        // to recycle a file opened without delete sharing (ERROR_SHARING_VIOLATION) —
        // the file silently stayed on disk while the track vanished from the library.
        await library.RemoveTracksAsync(tracks.Select(t => t.Id));

        if (choice == RemoveFromLibraryChoice.Trash)
            await TrashLocalFilesAsync(tracks, protectedRoots);

        return true;
    }

    /// <summary>
    /// Moves the tracks' files to the OS trash off the UI thread. Network/remote
    /// sources (SMB, WebDAV, Navidrome, …) are skipped — only
    /// <see cref="SourceType.Local"/> tracks own a deletable local file.
    /// When a removal empties a folder of audio entirely (only cover art, the
    /// tracks' own lyric sidecars and OS detritus would remain), the folder is
    /// recycled as ONE bin item with the audio still inside, so a single Explorer
    /// "Restore" brings the whole album back — trashing file then folder separately
    /// produced two sibling bin entries, and restoring just the folder looked like
    /// the audio had been stripped out of it.
    /// Everything else is trashed per file, with retries: the player releases a
    /// removed track's handle asynchronously (UI-thread post → worker stop), so the
    /// first attempt can race the release and lose — and the same open handle
    /// blocks a whole-directory move, so the folder ladder waits it out too.
    /// The per-file path then sweeps orphaned lyric sidecars and leftover-only
    /// folders exactly as before.
    /// </summary>
    public static Task TrashLocalFilesAsync(IEnumerable<Track> tracks, ISet<string>? protectedRoots = null)
    {
        var paths = SelectTrashablePaths(tracks);
        if (paths.Count == 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            var protectedDirs = protectedRoots ?? await GetProtectedRootsAsync().ConfigureAwait(false);
            await TrashLocalFilesCoreAsync(
                paths, protectedDirs,
                RecycleBin.TryMoveToTrash, RecycleBin.TryMoveDirectoryToTrash,
                TrashRetryDelaysMs, FolderTrashRetryDelaysMs).ConfigureAwait(false);
        });
    }

    /// <summary>Trash orchestration with injectable trash/delay seams; internal for tests.</summary>
    internal static async Task TrashLocalFilesCoreAsync(
        IReadOnlyList<string> paths, ISet<string> protectedDirs,
        Func<string, bool> tryTrashFile, Func<string, bool> tryTrashDirectory,
        IReadOnlyList<int> fileRetryDelaysMs, IReadOnlyList<int> folderRetryDelaysMs)
    {
        // Whole-folder pass first; anything that doesn't qualify (or whose folder
        // move stays blocked past the ladder) falls back to the per-file path below.
        var perFile = new List<string>();
        var trashedWhole = new List<string>();
        foreach (var (dir, dirPaths) in GroupByDirectory(paths))
        {
            if (dir.Length > 0 && await TrashWholeDirectoryWithRetriesAsync(
                    dir, dirPaths, protectedDirs, tryTrashDirectory, folderRetryDelaysMs).ConfigureAwait(false))
                trashedWhole.Add(dir);
            else
                perFile.AddRange(dirPaths);
        }

        var done = await TrashWithRetriesAsync(
            perFile,
            // "Done" = trashed, or nothing left on disk to trash.
            p => !File.Exists(p) || tryTrashFile(p),
            fileRetryDelaysMs).ConfigureAwait(false);

        TrashSidecarFiles(done, tryTrashFile);
        // Whole-trashed folders join the sweep already gone, so it cascades straight
        // to their now-emptied parents (the artist folder above the binned album).
        await CleanupEmptiedFoldersAsync(
            trashedWhole.Concat(done.Select(Path.GetDirectoryName).OfType<string>()),
            protectedDirs, tryTrashDirectory, folderRetryDelaysMs).ConfigureAwait(false);
    }

    /// <summary>Removal paths grouped by their normalized parent directory;
    /// unparseable paths land in the <c>""</c> bucket, which is always per-file.</summary>
    internal static Dictionary<string, List<string>> GroupByDirectory(IEnumerable<string> paths)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            string dir;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path));
                dir = string.IsNullOrEmpty(parent) ? string.Empty : Path.TrimEndingDirectorySeparator(parent);
            }
            catch { dir = string.Empty; }
            if (!groups.TryGetValue(dir, out var list)) groups[dir] = list = new List<string>();
            list.Add(path);
        }
        return groups;
    }

    /// <summary>Waits before each re-attempt; ~1.75s total worst case.</summary>
    private static readonly int[] TrashRetryDelaysMs = { 0, 250, 500, 1000 };

    /// <summary>Returns the paths that were successfully trashed (or already gone).</summary>
    internal static async Task<List<string>> TrashWithRetriesAsync(
        IReadOnlyList<string> paths, Func<string, bool> tryTrash, IReadOnlyList<int> retryDelaysMs)
    {
        var pending = new List<string>(paths);
        var done = new List<string>(paths.Count);
        foreach (var delayMs in retryDelaysMs)
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            pending.RemoveAll(p =>
            {
                if (!tryTrash(p)) return false;
                done.Add(p);
                return true;
            });
            if (pending.Count == 0) return done;
        }
        foreach (var p in pending)
            DebugLogger.Error(DebugLogger.Category.Error, "Library.TrashFailed", p);
        return done;
    }

    /// <summary>Lyric sidecars living next to a track as
    /// <c>Path.ChangeExtension(track, ext)</c>: synced .lrc/.ttml and plain .txt.</summary>
    private static readonly string[] SidecarExtensions = { ".lrc", ".ttml", ".txt" };

    /// <summary>Trashes each trashed audio file's same-basename lyric sidecars.</summary>
    internal static void TrashSidecarFiles(IEnumerable<string> trashedAudioPaths, Func<string, bool> tryTrash)
    {
        foreach (var audio in trashedAudioPaths)
        {
            foreach (var ext in SidecarExtensions)
            {
                string? sidecar;
                try { sidecar = Path.ChangeExtension(audio, ext); }
                catch { continue; }
                if (!string.IsNullOrWhiteSpace(sidecar) && File.Exists(sidecar))
                    tryTrash(sidecar);
            }
        }
    }

    // Leftover types allowed to ride along when an emptied folder is trashed:
    // artwork, per-track lyric sidecars, and OS detritus. User-authored files
    // (.m3u/.m3u8 playlists, .cue sheets, .nfo/.txt/.log notes) deliberately
    // keep the folder alive — sweeping them trashed real user data unprompted.
    private static readonly HashSet<string> DisposableLeftoverExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".lrc", ".ttml", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

    private static readonly HashSet<string> DisposableLeftoverNames = new(StringComparer.OrdinalIgnoreCase)
    { "Thumbs.db", "desktop.ini", ".DS_Store" };

    internal static bool IsDisposableLeftover(string fileName) =>
        DisposableLeftoverNames.Contains(fileName)
        || DisposableLeftoverExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>Per-directory re-attempts for the folder sweep. A directory move is
    /// blocked while ANY handle is open beneath it — the shell right after moving a
    /// child to the bin, the search indexer / thumbnailer chewing on a fresh cover
    /// image, or the app's own disposal timing — and those can outlive a short
    /// window (observed twice in the field), so the tail here is generous (~5.5s).</summary>
    private static readonly int[] FolderTrashRetryDelaysMs = { 0, 250, 750, 1500, 3000 };

    /// <summary>
    /// Trashes each directory that held removed audio once nothing meaningful is left
    /// in it (no subfolders, only disposable leftovers), then walks up trashing
    /// parents that qualify the same way (album folder, then its emptied artist
    /// folder). Never touches configured music roots, well-known user folders, or
    /// drive roots. Fail-closed: any doubt leaves the folder in place.
    /// </summary>
    internal static async Task CleanupEmptiedFoldersAsync(
        IEnumerable<string> directories, ISet<string> protectedDirs,
        Func<string, bool> tryTrashDirectory, IReadOnlyList<int>? retryDelaysMs = null)
    {
        var delays = retryDelaysMs ?? FolderTrashRetryDelaysMs;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in directories)
        {
            string dir;
            try { dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)); }
            catch { continue; }
            if (!seen.Add(dir)) continue;

            var current = dir;
            for (var depth = 0; depth < 3; depth++)
            {
                if (!await TrashDirectoryWithRetriesAsync(current, protectedDirs, tryTrashDirectory, delays)
                        .ConfigureAwait(false))
                    break;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent)) break;
                current = parent;
            }
        }
    }

    private static async Task<bool> TrashDirectoryWithRetriesAsync(
        string dir, ISet<string> protectedDirs, Func<string, bool> tryTrashDirectory, IReadOnlyList<int> delays)
    {
        var qualified = false;
        foreach (var delayMs in delays)
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            // A trash that reported failure can still have landed — "already gone"
            // counts as success so the cascade continues to the parent.
            if (!Directory.Exists(dir)) return true;
            if (!QualifiesForTrash(dir, protectedDirs)) continue;
            qualified = true;
            if (tryTrashDirectory(dir)) return true;
        }
        // Only a folder that qualified but wouldn't move is worth reporting — a folder
        // with real content in it was correctly left alone.
        if (qualified)
            DebugLogger.Error(DebugLogger.Category.Error, "Library.FolderTrashFailed", dir);
        return false;
    }

    private static bool QualifiesForTrash(string dir, ISet<string> protectedDirs)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (protectedDirs.Contains(dir)) return false;
            if (string.Equals(Path.GetPathRoot(dir), dir, StringComparison.OrdinalIgnoreCase)) return false;

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(entry)) return false;                        // still holds subfolders
                if (!IsDisposableLeftover(Path.GetFileName(entry))) return false; // something worth keeping
            }
            return true;
        }
        catch
        {
            return false; // fail closed
        }
    }

    /// <summary>
    /// True when trashing <paramref name="removingPaths"/> would leave nothing
    /// meaningful in <paramref name="dir"/>: no subfolders, and every other entry
    /// is either one of the removed tracks' lyric sidecars or a disposable
    /// leftover. Such a folder is recycled whole (audio still inside) instead of
    /// file-by-file. Same protections as <see cref="QualifiesForTrash"/>; fail closed.
    /// </summary>
    internal static bool QualifiesForWholeFolderTrash(
        string dir, IReadOnlyCollection<string> removingPaths, ISet<string> protectedDirs)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (protectedDirs.Contains(dir)) return false;
            if (string.Equals(Path.GetPathRoot(dir), dir, StringComparison.OrdinalIgnoreCase)) return false;

            // The removed audio and its same-basename lyric sidecars ride along
            // inside the binned folder (and get restored with it).
            var goes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in removingPaths)
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch { continue; }
                goes.Add(full);
                foreach (var ext in SidecarExtensions)
                {
                    try { goes.Add(Path.ChangeExtension(full, ext)); }
                    catch { /* no sidecar variant for an unparseable path */ }
                }
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(entry)) return false;                        // still holds subfolders
                if (goes.Contains(Path.GetFullPath(entry))) continue;             // being removed anyway
                if (!IsDisposableLeftover(Path.GetFileName(entry))) return false; // something worth keeping
            }
            return true;
        }
        catch
        {
            return false; // fail closed — the per-file path takes over
        }
    }

    /// <summary>Whole-folder variant of the trash retry ladder. Re-checks
    /// qualification every round (disk state can change while waiting out the
    /// player's handle release) and counts "already gone" as success. Returns
    /// false to hand the folder's files to the per-file fallback.</summary>
    internal static async Task<bool> TrashWholeDirectoryWithRetriesAsync(
        string dir, IReadOnlyCollection<string> removingPaths, ISet<string> protectedDirs,
        Func<string, bool> tryTrashDirectory, IReadOnlyList<int> retryDelaysMs)
    {
        foreach (var delayMs in retryDelaysMs)
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            if (!Directory.Exists(dir)) return true; // landed despite a failure report
            if (!QualifiesForWholeFolderTrash(dir, removingPaths, protectedDirs)) return false;
            if (tryTrashDirectory(dir)) return true;
        }
        return false;
    }

    /// <summary>Directories the folder cleanup must never trash: the configured music
    /// folders plus well-known user folders (Music, Downloads, Desktop, …).</summary>
    /// <remarks>
    /// Public so the file organizer can apply the same protection — its own empty-dir
    /// cleanup had none, so organizing loose files out of a configured root deleted
    /// the root itself.
    /// </remarks>
    public static async Task<HashSet<string>> GetProtectedRootsAsync()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))); }
            catch { /* unparseable path — nothing to protect */ }
        }

        try
        {
            var persistence = App.Services?.GetService<IPersistenceService>();
            if (persistence != null)
            {
                var settings = await persistence.LoadSettingsAsync().ConfigureAwait(false);
                foreach (var folder in settings.MusicFolders)
                    Add(folder);
            }
        }
        catch { /* settings unavailable — the well-known folders below still apply */ }

        foreach (var special in new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
        })
            Add(Environment.GetFolderPath(special));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        return roots;
    }

    /// <summary>Local, non-empty, de-duplicated file paths eligible for trashing.</summary>
    internal static IReadOnlyList<string> SelectTrashablePaths(IEnumerable<Track> tracks) =>
        tracks
            .Where(t => t.SourceType == SourceType.Local && !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => t.FilePath)
            .Distinct()
            .ToList();

    /// <summary>
    /// The removed-with-"Keep Files" entries that Settings → Library lists for
    /// restore: <see cref="AppSettings.ExcludedFilePaths"/> whose file is still on
    /// disk. Entries whose file has since been deleted or moved are omitted —
    /// there is nothing left to restore, and the exclusion list itself is pruned
    /// on the next removal (see LibraryService.ExcludeFilePathsAndCleanFoldersAsync).
    /// </summary>
    public static async Task<IReadOnlyList<RemovedTrackEntry>> GetRemovedEntriesAsync(IPersistenceService persistence)
    {
        var settings = await persistence.LoadSettingsAsync().ConfigureAwait(false);
        var paths = settings.ExcludedFilePaths;
        // One File.Exists per entry is disk I/O — keep it off the caller's (UI) thread.
        return await Task.Run(() => SelectRemovedEntries(paths, File.Exists)).ConfigureAwait(false);
    }

    /// <summary>Pure core of <see cref="GetRemovedEntriesAsync"/>; internal for tests.</summary>
    internal static List<RemovedTrackEntry> SelectRemovedEntries(
        IEnumerable<string> excludedFilePaths, Func<string, bool> fileExists) =>
        excludedFilePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(fileExists)
            .Select(p => new RemovedTrackEntry(p))
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
