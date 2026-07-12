using System.Text;
using System.Text.Json;

namespace Noctis.Services;

/// <summary>One imported track reference, before matching against the local library.
/// <paramref name="FilePath"/> is set only by m3u imports (absolute when resolvable),
/// letting the matcher try exact path/filename hits before fuzzy text matching.</summary>
public sealed record PlaylistImportEntry(string Title, string Artist, string Album, string FilePath = "");

/// <summary>Parsed import file: a suggested playlist name plus its track entries.</summary>
public sealed record PlaylistImportParseResult(string SuggestedName, IReadOnlyList<PlaylistImportEntry> Entries);

/// <summary>
/// Pure parsing of playlist files. Supports Exportify-style CSV, TuneMyMusic-style JSON
/// (plus generic CSV/JSON with Title/Artist/Album fields), and m3u/m3u8. Column and
/// key matching is case-insensitive; multiple artists collapse to the primary (first) artist.
/// </summary>
public static class PlaylistImportParser
{
    public static PlaylistImportParseResult Parse(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var fallbackName = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".m3u" || ext == ".m3u8")
        {
            string baseDir;
            try { baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty; }
            catch { baseDir = string.Empty; }
            return ParseM3u(text, fallbackName, baseDir);
        }

        return ext == ".json" || LooksLikeJson(text)
            ? ParseJson(text, fallbackName)
            : ParseCsv(text, fallbackName);
    }

    // ── M3U / M3U8 ──

    /// <summary>
    /// Relative entries resolve against <paramref name="baseDir"/> (the playlist's own
    /// folder), so a playlist exported with relative paths works no matter which machine
    /// it was written on. #EXTINF display text ("Artist - Title") is kept as the fuzzy
    /// fallback for when the path no longer matches anything.
    /// </summary>
    public static PlaylistImportParseResult ParseM3u(string text, string fallbackName, string baseDir)
    {
        var entries = new List<PlaylistImportEntry>();
        var pendingTitle = string.Empty;
        var pendingArtist = string.Empty;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                // "#EXTINF:123,Artist - Title" — display text after the first comma.
                var comma = line.IndexOf(',');
                var display = comma >= 0 ? line[(comma + 1)..].Trim() : string.Empty;
                var dash = display.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0)
                {
                    pendingArtist = display[..dash].Trim();
                    pendingTitle = display[(dash + 3)..].Trim();
                }
                else
                {
                    pendingArtist = string.Empty;
                    pendingTitle = display;
                }
                continue;
            }
            if (line.StartsWith('#')) continue;

            var path = line.Replace('\\', '/');
            try
            {
                if (!Path.IsPathRooted(path) && baseDir.Length > 0)
                    path = Path.GetFullPath(Path.Combine(baseDir, path));
            }
            catch { /* malformed entry — keep raw for filename matching */ }

            var title = pendingTitle.Length > 0
                ? pendingTitle
                : Path.GetFileNameWithoutExtension(path);
            entries.Add(new PlaylistImportEntry(title, pendingArtist, string.Empty, path));
            pendingTitle = string.Empty;
            pendingArtist = string.Empty;
        }

        return new PlaylistImportParseResult(fallbackName, entries);
    }

    private static bool LooksLikeJson(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    // ── CSV (Exportify / generic) ──

    public static PlaylistImportParseResult ParseCsv(string text, string fallbackName)
    {
        var rows = TokenizeCsv(text);
        var entries = new List<PlaylistImportEntry>();
        if (rows.Count < 2) return new PlaylistImportParseResult(fallbackName, entries);

        var header = rows[0];
        var titleIdx = FindColumn(header, "Track Name", "Title", "Name");
        var artistIdx = FindColumn(header, "Artist Name(s)", "Artist Name", "Artist", "Artists");
        var albumIdx = FindColumn(header, "Album Name", "Album");
        if (titleIdx < 0) return new PlaylistImportParseResult(fallbackName, entries);

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var title = Get(row, titleIdx);
            if (string.IsNullOrWhiteSpace(title)) continue;
            entries.Add(new PlaylistImportEntry(
                title.Trim(),
                PrimaryArtist(Get(row, artistIdx)),
                Get(row, albumIdx).Trim()));
        }

        return new PlaylistImportParseResult(fallbackName, entries);
    }

    private static int FindColumn(string[] header, params string[] names)
    {
        foreach (var name in names)
            for (var i = 0; i < header.Length; i++)
                if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }

    private static string Get(string[] row, int idx) => idx >= 0 && idx < row.Length ? row[idx] : string.Empty;

    private static string PrimaryArtist(string artists)
    {
        if (string.IsNullOrWhiteSpace(artists)) return string.Empty;
        var first = artists.Split(',', ';')[0];
        return first.Trim();
    }

    /// <summary>RFC-4180 tokenizer: handles quoted fields with embedded commas, quotes, and newlines.</summary>
    private static List<string[]> TokenizeCsv(string text)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case ',': record.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        record.Add(field.ToString()); field.Clear();
                        rows.Add(record.ToArray()); record = new List<string>();
                        break;
                    default: field.Append(c); break;
                }
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            rows.Add(record.ToArray());
        }
        return rows;
    }

    // ── JSON (TuneMyMusic / generic) ──

    public static PlaylistImportParseResult ParseJson(string text, string fallbackName)
    {
        var entries = new List<PlaylistImportEntry>();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var name = fallbackName;
        JsonElement tracks;

        if (root.ValueKind == JsonValueKind.Array)
        {
            tracks = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            name = FirstString(root, "playlistName", "playlist", "name", "title") ?? fallbackName;
            if (!TryGetArray(root, out tracks, "tracks", "songs", "items"))
                return new PlaylistImportParseResult(name, entries);
        }
        else
        {
            return new PlaylistImportParseResult(name, entries);
        }

        foreach (var t in tracks.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            var title = FirstString(t, "title", "name", "track", "trackName", "song") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title)) continue;
            var artist = FirstString(t, "artist", "artists", "artistName", "artist_name") ?? string.Empty;
            var album = FirstString(t, "album", "albumName", "album_name") ?? string.Empty;
            entries.Add(new PlaylistImportEntry(title.Trim(), PrimaryArtist(artist), album.Trim()));
        }

        return new PlaylistImportParseResult(name, entries);
    }

    private static bool TryGetArray(JsonElement obj, out JsonElement arr, params string[] keys)
    {
        foreach (var key in keys)
            foreach (var prop in obj.EnumerateObject())
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.Array)
                {
                    arr = prop.Value;
                    return true;
                }
        arr = default;
        return false;
    }

    private static string? FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            foreach (var prop in obj.EnumerateObject())
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                    // Some exports use an array of artist objects/strings.
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String) return item.GetString();
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                var n = FirstString(item, "name", "title");
                                if (!string.IsNullOrWhiteSpace(n)) return n;
                            }
                        }
                    }
                }
        return null;
    }
}
