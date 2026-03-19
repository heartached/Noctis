using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Local filesystem connector placeholder for unified source orchestration.
/// Local scanning remains owned by LibraryService.
/// </summary>
public sealed class LocalMediaSourceConnector : IMediaSourceConnector
{
    public SourceType SourceType => SourceType.Local;
    public string Name => "Local Files";

    public Task<bool> ValidateConnectionAsync(SourceConnection connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUriOrPath))
            return Task.FromResult(false);
        return Task.FromResult(Directory.Exists(connection.BaseUriOrPath));
    }

    public Task<IReadOnlyList<Track>> ScanAsync(SourceConnection connection, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());
    }

    public Task<Stream?> OpenTrackStreamAsync(SourceConnection connection, Track track, CancellationToken ct = default)
    {
        if (!File.Exists(track.FilePath)) return Task.FromResult<Stream?>(null);
        Stream stream = File.Open(track.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> DownloadTrackAsync(SourceConnection connection, Track track, string destinationPath, CancellationToken ct = default)
    {
        if (!File.Exists(track.FilePath)) return Task.FromResult(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(track.FilePath, destinationPath, true);
        return Task.FromResult(true);
    }
}

