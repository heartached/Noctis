using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Connector contract for local/server media sources.
/// </summary>
public interface IMediaSourceConnector
{
    SourceType SourceType { get; }
    string Name { get; }

    Task<bool> ValidateConnectionAsync(SourceConnection connection, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> ScanAsync(SourceConnection connection, CancellationToken ct = default);
    Task<Stream?> OpenTrackStreamAsync(SourceConnection connection, Track track, CancellationToken ct = default);
    Task<bool> DownloadTrackAsync(SourceConnection connection, Track track, string destinationPath, CancellationToken ct = default);
}

