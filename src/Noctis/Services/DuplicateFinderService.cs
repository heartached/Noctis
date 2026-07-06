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
        await Task.Run(() =>
        {
            foreach (var (id, path) in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (RecycleBin.TryMoveToTrash(path))
                {
                    trashed++;
                    removeIds.Add(id);
                }
                else if (!File.Exists(path))
                {
                    removeIds.Add(id); // stale entry — nothing on disk to keep
                }
            }
        }, ct);

        if (removeIds.Count > 0)
            await _library.RemoveTracksAsync(removeIds);
        return trashed;
    }
}
