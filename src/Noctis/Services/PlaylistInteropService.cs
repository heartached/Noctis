using System.Text;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Deterministic m3u/m3u8 import/export.
/// </summary>
public sealed class PlaylistInteropService : IPlaylistInteropService
{
    public async Task ExportM3uAsync(string filePath, IEnumerable<Track> tracks, CancellationToken ct = default)
    {
        var ordered = tracks.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var track in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var seconds = (int)Math.Round(track.Duration.TotalSeconds);
            sb.AppendLine($"#EXTINF:{seconds},{track.Artist} - {track.Title}");
            sb.AppendLine(track.FilePath);
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(false), ct);
    }

    public async Task<IReadOnlyList<Track>> ImportM3uAsync(string filePath, IReadOnlyList<Track> libraryTracks, CancellationToken ct = default)
    {
        var pathLookup = libraryTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath))
            .GroupBy(t => NormalizePath(t.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var playlistDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var lines = await File.ReadAllLinesAsync(filePath, ct);
        var resolved = new List<Track>(lines.Length);

        foreach (var raw in lines)
        {
            ct.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

            var candidate = line;
            if (!Path.IsPathRooted(candidate))
                candidate = Path.GetFullPath(Path.Combine(playlistDir, candidate));

            if (pathLookup.TryGetValue(NormalizePath(candidate), out var track))
                resolved.Add(track);
        }

        return resolved;
    }

    private static string NormalizePath(string value) =>
        value.Replace('/', '\\').Trim();
}

