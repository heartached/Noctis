using System.Text;
using System.Text.Json;

namespace Noctis.Services;

/// <summary>One imported track reference, before matching against the local library.
/// <paramref name="FilePath"/> is set only by m3u imports (absolute when resolvable),
/// letting the matcher try exact path/filename hits before fuzzy text matching.</summary>
public sealed record PlaylistImportEntry(string Title, string Artist, string Album, string FilePath = "")
{
    /// <summary>The length an m3u's #EXTINF gave (null when it gave none, or -1): the
    /// matcher's tie-break between same-named songs.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The playlist line the entry came from (the m3u path line), for an import's
    /// list of lines that matched nothing.</summary>
    public string SourceLine { get; init; } = string.Empty;

    /// <summary>The #EXTINF display text ("Artist - Title"); empty when the line had none.</summary>
    public string DisplayText { get; init; } = string.Empty;
}

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
        var fallbackName = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".m3u" || ext == ".m3u8")
        {
            // Bytes, not ReadAllText: a Latin-1 .m3u (Winamp's, old iTunes exports) read as
            // UTF-8 turned every accented name into U+FFFD and nothing matched by name.
            var m3u = DecodeText(File.ReadAllBytes(filePath));
            string baseDir;
            try { baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty; }
            catch { baseDir = string.Empty; }
            return ParseM3u(m3u, fallbackName, baseDir);
        }

        var text = File.ReadAllText(filePath);
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
        TimeSpan? pendingDuration = null;
        var pendingDisplay = string.Empty;

        foreach (var raw in text.Split('\n'))
        {
            // A BOM left on the first line (text decoded without stripping it) is not part of "#EXTM3U".
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                // "#EXTINF:123,Artist - Title" — display text after the first comma. The
                // length runs from the colon to the first space (attributes such as
                // tvg-id="…" may follow it) or the comma; -1 means unknown.
                var comma = line.IndexOf(',');
                var display = comma >= 0 ? line[(comma + 1)..].Trim() : string.Empty;
                pendingDuration = ParseExtinfSeconds(line, comma);
                (pendingArtist, pendingTitle) = SplitArtistTitle(display);
                pendingDisplay = display;
                continue;
            }
            if (line.StartsWith('#')) continue;

            // Decode file:// URIs and percent-encoding BEFORE any path handling.
            // Matching them raw against library paths produced zero hits, and for entries
            // without #EXTINF the fallback title became e.g. "Song%20Name", which the
            // filename index couldn't match either.
            var path = DecodeM3uEntry(line).Replace('\\', '/');
            try
            {
                // A Windows path ("C:/Music/…") is rooted wherever it was written, even where
                // Path.IsPathRooted says otherwise (Linux, Android): never under baseDir.
                if (!Path.IsPathRooted(path) && !IsDriveRooted(path) && baseDir.Length > 0)
                    path = Path.GetFullPath(Path.Combine(baseDir, path));
            }
            catch { /* malformed entry — keep raw for filename matching */ }

            string title, artist;
            if (pendingTitle.Length > 0)
                (title, artist) = (pendingTitle, pendingArtist);
            else
                (artist, title) = TitleFromFileName(path);
            entries.Add(new PlaylistImportEntry(title, artist, string.Empty, path)
            {
                Duration = pendingDuration,
                SourceLine = line,
                DisplayText = pendingDisplay,
            });
            pendingTitle = string.Empty;
            pendingArtist = string.Empty;
            pendingDuration = null;
            pendingDisplay = string.Empty;
        }

        return new PlaylistImportParseResult(fallbackName, entries);
    }

    /// <summary>
    /// A playlist file's bytes as text: UTF-8 (with or without its BOM; UTF-16 when a BOM
    /// says so), else Latin-1 — an .m3u from Winamp or an old iTunes is in the system code
    /// page, and bytes that are not valid UTF-8 are read one byte per character rather
    /// than dropped.
    /// </summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// M3U8s written by VLC/foobar/web tools carry file:// URIs and/or percent-encoding
    /// ("%20"); matching them raw against library paths produced zero hits. Decodes both
    /// forms; plain paths pass through.
    /// </summary>
    public static string DecodeM3uEntry(string line)
    {
        if (line.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var local = new Uri(line).LocalPath;
                // "file:///C:/Music/a.mp3" read on Linux/Android comes out as "/C:/Music/a.mp3".
                return local.Length > 3 && local[0] == '/' && char.IsAsciiLetter(local[1]) && local[2] == ':' ? local[1..] : local;
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

    /// <summary>"C:/…" or "C:\…": a Windows drive path, on any OS.</summary>
    internal static bool IsDriveRooted(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '/' || path[2] == '\\');

    private static TimeSpan? ParseExtinfSeconds(string line, int comma)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return null;
        var end = comma > colon ? comma : line.Length;
        var span = line.AsSpan(colon + 1, end - colon - 1).Trim();
        var space = span.IndexOf(' ');
        if (space > 0) span = span[..space];
        return double.TryParse(span, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
               && seconds > 0 && seconds < 1_000_000
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>"Artist - Title" → both; text without the dash is a title alone.</summary>
    private static (string Artist, string Title) SplitArtistTitle(string display)
    {
        var dash = display.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0
            ? (display[..dash].Trim(), display[(dash + 3)..].Trim())
            : (string.Empty, display);
    }

    /// <summary>
    /// A title for an entry without #EXTINF, from its file name: a track-number prefix
    /// ("01 ", "01. ", "1-03 - ") goes, and "Artist - Title" splits, so the entry can still be
    /// matched by title and artist when its path matches nothing.
    /// </summary>
    internal static (string Artist, string Title) TitleFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
        var i = 0;
        // Digits, allowing one disc-track dash between them ("1-03").
        while (i < stem.Length && (char.IsAsciiDigit(stem[i])
               || (i > 0 && stem[i] == '-' && i + 1 < stem.Length && char.IsAsciiDigit(stem[i + 1]))))
            i++;
        if (i is > 0 and <= 4 && i < stem.Length)
        {
            var rest = stem[i..];
            var trimmed = rest.TrimStart(' ', '.', '-', '_', ')');
            // A number is a track number only when a separator follows it, and only up to three
            // digits unless a dot or dash says so ("2002 - Song" stays as it is).
            if (trimmed.Length > 0 && trimmed.Length < rest.Length && (i <= 3 || rest[0] is '.' or '-' or '_'))
                stem = trimmed;
        }
        return SplitArtistTitle(stem.Trim());
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

    /// <summary>The first listed artist. Settings' "keep together" names stay whole, matching
    /// the library's own PrimaryArtist: "Tyler, The Creator, Kali Uchis" → "Tyler, The Creator",
    /// not "Tyler" (which then missed the library track's primary artist).</summary>
    private static string PrimaryArtist(string artists)
    {
        if (string.IsNullOrWhiteSpace(artists)) return string.Empty;
        return Noctis.Models.ArtistCredit.SplitAroundProtected(artists, gap => gap.Split(',', ';'))
            .Select(a => a.Trim())
            .FirstOrDefault(a => a.Length > 0) ?? string.Empty;
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
