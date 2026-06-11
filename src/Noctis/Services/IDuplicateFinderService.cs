namespace Noctis.Services;

/// <summary>
/// Finds duplicate tracks in the local library and deletes user-selected copies.
/// Deletion is preview-driven: the caller decides exactly which track IDs to remove.
/// </summary>
public interface IDuplicateFinderService
{
    /// <summary>Scans the local library for duplicate groups off the UI thread.</summary>
    Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        int durationToleranceSeconds = DuplicateMatcher.DefaultDurationToleranceSeconds,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the given tracks' files (to the recycle bin on Windows, permanently elsewhere)
    /// and removes them from the library. Returns the number of files deleted.
    /// </summary>
    Task<int> DeleteAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default);
}
