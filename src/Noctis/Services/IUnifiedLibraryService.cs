using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Unified view over local + remote media sources.
/// </summary>
public interface IUnifiedLibraryService
{
    Task<IReadOnlyList<Track>> GetUnifiedTracksAsync(CancellationToken ct = default);
    Task RefreshRemoteSourcesAsync(CancellationToken ct = default);
}

