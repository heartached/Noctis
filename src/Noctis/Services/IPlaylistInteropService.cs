using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Deterministic m3u/m3u8 import/export helpers.
/// </summary>
public interface IPlaylistInteropService
{
    Task ExportM3uAsync(string filePath, IEnumerable<Track> tracks, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> ImportM3uAsync(string filePath, IReadOnlyList<Track> libraryTracks, CancellationToken ct = default);
}

