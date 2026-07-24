using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Stream cache and offline pinning contract.
/// </summary>
public interface IOfflineCacheService
{
    Task<string?> ResolvePlaybackPathAsync(Track track, CancellationToken ct = default);
    Task PinAsync(Track track, Stream sourceStream, CancellationToken ct = default);
    Task UnpinAsync(Track track, CancellationToken ct = default);
    /// <summary>
    /// Prunes dead entries and evicts unpinned files (least-recently-updated first)
    /// until the cache fits <paramref name="limitMb"/>. Pass 0 to prune only.
    /// </summary>
    Task EnforceLimitsAsync(int limitMb = 0, CancellationToken ct = default);
}

