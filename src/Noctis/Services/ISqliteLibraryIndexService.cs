using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// SQLite-backed index used for large-library durability and query speed.
/// </summary>
public interface ISqliteLibraryIndexService
{
    Task InitializeAsync(CancellationToken ct = default);
    Task MigrateFromJsonIfEmptyAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
    Task UpsertTracksAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
    Task DeleteTracksAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

