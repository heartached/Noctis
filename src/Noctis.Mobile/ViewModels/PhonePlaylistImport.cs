using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>What one imported playlist file came to: its name, the phone's songs it found (in
/// the file's order) and the lines that matched nothing.</summary>
public sealed record PlaylistImportOutcome(string Name, IReadOnlyList<Track> Matched, IReadOnlyList<string> Unmatched)
{
    public int Total => Matched.Count + Unmatched.Count;
}

/// <summary>
/// Playlists ⋯ › Import Playlist, the part that needs no phone: an .m3u/.m3u8 read with the
/// desktop's parser (PlaylistImportParser: #EXTINF, file:// and percent-encoded lines, Windows
/// paths; UTF-8 with or without a BOM, else Latin-1) and matched with the desktop's matcher
/// (FuzzyTrackMatcher): first by the path's file name and the folders above it — a playlist
/// written on a computer names folders the phone does not have — then by title and artist
/// (the length breaking ties).
/// </summary>
public static class PhonePlaylistImport
{
    /// <summary>The file types the import reads.</summary>
    public static bool IsPlaylistFile(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    public static PlaylistImportOutcome Analyze(string fileName, byte[] content, IReadOnlyList<Track> library)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (name.Length == 0) name = fileName.Trim();
        // No folder to resolve relative lines against (the file came from the picker, not a
        // path): they stay relative, and their file name and folders are what match.
        var parsed = PlaylistImportParser.ParseM3u(PlaylistImportParser.DecodeText(content), name, baseDir: string.Empty);
        var matched = new List<Track>();
        var unmatched = new List<string>();
        foreach (var result in FuzzyTrackMatcher.Match(parsed.Entries, library))
        {
            if (result.Match is { } track) matched.Add(track);
            else unmatched.Add(Describe(result.Entry));
        }
        return new PlaylistImportOutcome(parsed.SuggestedName, matched, unmatched);
    }

    /// <summary>An unmatched line as people read it: "Artist - Title" from its #EXTINF, else
    /// the file name the line pointed at.</summary>
    internal static string Describe(PlaylistImportEntry entry)
    {
        if (entry.DisplayText.Length > 0) return entry.DisplayText;
        if (entry.FilePath.Length == 0)
            return entry.Artist.Length > 0 ? $"{entry.Artist} - {entry.Title}" : entry.Title;
        var file = entry.FilePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrEmpty(file) ? entry.SourceLine : file;
    }

    /// <summary>A name no playlist has yet: the file's, else "Name 2", "Name 3"…</summary>
    public static string UniqueName(string name, IEnumerable<string> taken)
    {
        var names = new HashSet<string>(taken, StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(name)) return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!names.Contains(candidate)) return candidate;
        }
    }
}
