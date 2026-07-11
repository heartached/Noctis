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
    /// Failed paths are retried on a short schedule: the player releases a removed
    /// track's handle asynchronously (UI-thread post → worker stop), so the first
    /// attempt can race the release and lose.
    /// Once the audio is gone, its orphaned .lrc sidecar follows it, and folders left
    /// holding nothing but leftovers (cover art, lyrics) are trashed too — otherwise
    /// the album folder lingers in Explorer and reads as a failed removal.
    /// </summary>
    public static Task TrashLocalFilesAsync(IEnumerable<Track> tracks, ISet<string>? protectedRoots = null)
    {
        var paths = SelectTrashablePaths(tracks);
        if (paths.Count == 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            var done = await TrashWithRetriesAsync(
                paths,
                // "Done" = trashed, or nothing left on disk to trash.
                p => !File.Exists(p) || RecycleBin.TryMoveToTrash(p),
                TrashRetryDelaysMs).ConfigureAwait(false);

            TrashSidecarFiles(done, RecycleBin.TryMoveToTrash);
            await CleanupEmptiedFoldersAsync(
                done.Select(Path.GetDirectoryName).OfType<string>(),
                protectedRoots ?? await GetProtectedRootsAsync().ConfigureAwait(false),
                RecycleBin.TryMoveDirectoryToTrash).ConfigureAwait(false);
        });
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

    /// <summary>Lyric sidecars the app writes next to a track as
    /// <c>Path.ChangeExtension(track, ext)</c>: synced .lrc and plain .txt.</summary>
    private static readonly string[] SidecarExtensions = { ".lrc", ".txt" };

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

    // Leftover types allowed to ride along when an emptied folder is trashed: artwork,
    // lyrics/playlist text, and OS detritus. Any other file keeps the folder alive.
    private static readonly HashSet<string> DisposableLeftoverExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".lrc", ".txt", ".nfo", ".cue", ".log", ".m3u", ".m3u8", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

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

    /// <summary>Directories the folder cleanup must never trash: the configured music
    /// folders plus well-known user folders (Music, Downloads, Desktop, …).</summary>
    private static async Task<HashSet<string>> GetProtectedRootsAsync()
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
}
