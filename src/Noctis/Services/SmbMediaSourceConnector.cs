using System.Security.Cryptography;
using System.Text;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// SMB connector: validates UNC/mapped paths and performs direct filesystem scans.
/// Uses IMetadataService to read tags so remote shares appear with full metadata.
/// </summary>
public sealed class SmbMediaSourceConnector : IMediaSourceConnector
{
    private readonly IMetadataService _metadata;

    public SmbMediaSourceConnector(IMetadataService metadata) => _metadata = metadata;

    public SourceType SourceType => SourceType.Smb;
    public string Name => "SMB";

    public Task<bool> ValidateConnectionAsync(SourceConnection connection, CancellationToken ct = default)
    {
        var path = connection.BaseUriOrPath;
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult(false);
        return Task.FromResult(Directory.Exists(path));
    }

    public Task<IReadOnlyList<Track>> ScanAsync(SourceConnection connection, CancellationToken ct = default)
    {
        var root = connection.BaseUriOrPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());

        var files = EnumerateAudioFiles(root);
        var tracks = new List<Track>();

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            var track = _metadata.ReadTrackMetadata(file);
            if (track == null) continue;

            // Mark source identity for downstream unified library
            track.SourceType = SourceType.Smb;
            track.SourceConnectionId = connection.Id.ToString("N");
            track.SourceTrackId = ComputeFileHash(file);
            tracks.Add(track);
        }

        return Task.FromResult<IReadOnlyList<Track>>(tracks);
    }

    public Task<Stream?> OpenTrackStreamAsync(SourceConnection connection, Track track, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
            return Task.FromResult<Stream?>(null);
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

    private static IEnumerable<string> EnumerateAudioFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            IEnumerable<string> dirs;
            IEnumerable<string> files;
            try
            {
                dirs = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var dir in dirs)
                stack.Push(dir);

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (MetadataService.SupportedExtensions.Contains(ext))
                    yield return file;
            }
        }
    }

    private static string ComputeFileHash(string filePath)
    {
        var normalized = filePath.Replace('/', '\\').ToLowerInvariant();
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
