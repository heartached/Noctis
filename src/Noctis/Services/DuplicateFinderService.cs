using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Duplicate detection + deletion over the local library. Grouping is delegated to the
/// pure <see cref="DuplicateMatcher"/>; this service owns the side effects (file deletion
/// and library removal).
/// </summary>
public sealed class DuplicateFinderService : IDuplicateFinderService
{
    private readonly ILibraryService _library;

    public DuplicateFinderService(ILibraryService library) => _library = library;

    public Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        int durationToleranceSeconds = DuplicateMatcher.DefaultDurationToleranceSeconds,
        CancellationToken ct = default)
    {
        var snapshot = _library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        return Task.Run(() => DuplicateMatcher.FindDuplicates(snapshot, durationToleranceSeconds), ct);
    }

    public async Task<int> DeleteAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
    {
        if (trackIds is null || trackIds.Count == 0) return 0;

        var idSet = new HashSet<Guid>(trackIds);
        var targets = _library.Tracks
            .Where(t => idSet.Contains(t.Id) && !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => (t.Id, t.FilePath))
            .ToList();

        // Only drop tracks from the library when their file was actually trashed
        // (or is already gone from disk). Removing on a failed trash left the file
        // in place but permanently excluded from future scans — a silent vanish.
        var trashed = 0;
        var removeIds = new List<Guid>(targets.Count);
        var trashedPaths = new List<string>(targets.Count);
        await Task.Run(() =>
        {
            // One "delete permanently?" No per drive, not one prompt per duplicate.
            var (trashFile, _) = LibraryRemovalHelper.SkipDeclinedTrash(
                RecycleBin.TryMoveToTrash, RecycleBin.TryMoveDirectoryToTrash);
            foreach (var (id, path) in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (trashFile(path))
                {
                    trashed++;
                    removeIds.Add(id);
                    trashedPaths.Add(path);
                }
                else if (!File.Exists(path))
                {
                    removeIds.Add(id); // stale entry — nothing on disk to keep
                }
            }

            // Take each deleted copy's lyric sidecars with it. LibraryRemovalHelper has
            // handled this for the normal removal path all along; the duplicate finder
            // never called it, so every deleted duplicate left its .lrc/.ttml/.txt behind.
            if (trashedPaths.Count > 0)
                LibraryRemovalHelper.TrashSidecarFiles(trashedPaths, trashFile);
        }, ct);

        if (trashedPaths.Count > 0)
        {
            try { RemoveEmptiedFolders(trashedPaths, await LibraryRemovalHelper.GetProtectedRootsAsync()); }
            catch (Exception ex) { DebugLog.Write("Duplicates", $"empty folder cleanup skipped: {ex.Message}"); }
        }

        if (removeIds.Count > 0)
            await _library.RemoveTracksAsync(removeIds);
        return trashed;
    }

    /// <summary>
    /// Removes the folder a deleted duplicate leaves behind when nothing at all is left in
    /// it, then its emptied parents. Only a completely empty folder goes; music folders, the
    /// well-known user folders and drive roots never do. Never throws.
    /// </summary>
    internal static void RemoveEmptiedFolders(IEnumerable<string> deletedPaths, HashSet<string> protectedRoots)
    {
        try
        {
            foreach (var start in deletedPaths.Select(Path.GetDirectoryName)
                         .Where(d => !string.IsNullOrEmpty(d))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start!));
                while (!string.IsNullOrEmpty(dir)
                       && !protectedRoots.Contains(dir)
                       && Path.GetDirectoryName(dir) is { Length: > 0 } parent   // not a drive root
                       && Directory.Exists(dir)
                       && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, recursive: false);
                    dir = Path.TrimEndingDirectorySeparator(parent);
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Duplicates", $"empty folder cleanup stopped: {ex.Message}");
        }
    }
}
