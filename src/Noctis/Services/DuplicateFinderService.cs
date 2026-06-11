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
        var paths = _library.Tracks
            .Where(t => idSet.Contains(t.Id))
            .Select(t => t.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var deleted = await Task.Run(() =>
        {
            var n = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (TryDelete(path)) n++;
            }
            return n;
        }, ct);

        await _library.RemoveTracksAsync(trackIds);
        return deleted;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            if (OperatingSystem.IsWindows())
            {
                // Recycle bin keeps an undo path for the user; non-Windows has no
                // standard recycle API, so those platforms delete permanently.
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                File.Delete(path);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
