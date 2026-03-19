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
    Task EnforceLimitsAsync(CancellationToken ct = default);
}

