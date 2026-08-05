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
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var track in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var seconds = (int)Math.Round(track.Duration.TotalSeconds);
            sb.AppendLine($"#EXTINF:{seconds},{SanitizeExtinfField(track.Artist)} - {SanitizeExtinfField(track.Title)}");
            sb.AppendLine(PortablePath(baseDir, track.FilePath));
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(false), ct);
    }

    // Tag text goes into a line-oriented format: a CR/LF embedded in an Artist/
    // Title tag would inject attacker-chosen lines (including entry paths) into
    // the exported playlist. A leading '#' is stripped so a tag can't pose as a
    // directive either. Titles with newlines are already malformed for m3u.
    private static string SanitizeExtinfField(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty).Replace("\n", string.Empty).TrimStart('#');

    /// <summary>
    /// Relative to the playlist's own folder with forward slashes whenever a
    /// relative form exists, so the file keeps working when the library moves
    /// between machines/OSes (Syncthing, NAS shares, other players). Absolute
    /// only as a fallback (different drive/root).
    /// </summary>
    internal static string PortablePath(string baseDir, string trackPath)
    {
        try
        {
            var full = Path.GetFullPath(trackPath);
            if (baseDir.Length == 0) return full;
            var relative = Path.GetRelativePath(baseDir, full);
            return Path.IsPathRooted(relative) ? full : relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return trackPath; // invalid characters — write as stored
        }
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

            var candidate = DecodeM3uEntry(line);
            if (!Path.IsPathRooted(candidate))
                candidate = Path.GetFullPath(Path.Combine(playlistDir, candidate));

            if (pathLookup.TryGetValue(NormalizePath(candidate), out var track))
                resolved.Add(track);
        }

        return resolved;
    }

    // M3U8s written by VLC/foobar/web tools carry file:// URIs and/or
    // percent-encoding ("%20"); matching them raw against library paths
    // produced zero hits. Decode both forms; plain paths pass through.
    //
    // Internal so PlaylistImportParser — the parser the *live* import dialog actually
    // uses — can share it. This logic lived only here, in ImportM3uAsync, which has no
    // callers; the live path did a bare Replace('\\','/') + Path.Combine, so a
    // VLC/foobar-exported .m3u8 fell through to fuzzy matching and entries without
    // #EXTINF got titles like "Song%20Name" that the filename index couldn't match either.
    internal static string DecodeM3uEntryPublic(string line) => DecodeM3uEntry(line);

    private static string DecodeM3uEntry(string line)
    {
        if (line.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new Uri(line).LocalPath;
            }
            catch (UriFormatException)
            {
                // Fall through and treat it as a literal path.
            }
        }

        if (line.Contains('%'))
        {
            try
            {
                return Uri.UnescapeDataString(line);
            }
            catch (UriFormatException)
            {
                // Literal % in a real filename — keep as-is.
            }
        }

        return line;
    }

    private static string NormalizePath(string value) =>
        value.Replace('/', '\\').Trim();
}

