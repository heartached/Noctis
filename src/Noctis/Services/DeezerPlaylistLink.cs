using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>What a pasted Deezer link points at.</summary>
public enum DeezerLinkKind { Playlist, Album }

/// <summary>
/// Pure helpers for importing a Deezer playlist or album by link: recognises the public
/// share URLs and parses the keyless <c>api.deezer.com</c> JSON into import entries.
/// Deezer's catalogue endpoints (<c>/playlist/{id}</c>, <c>/album/{id}</c> and their
/// <c>/tracks</c> pages) need no token, so this works for any public playlist. HTTP lives in
/// <see cref="PlaylistImportService"/>; nothing here touches the network.
/// </summary>
public static partial class DeezerPlaylistLink
{
    /// <summary>Tracks fetched per page (Deezer's maximum for these endpoints).</summary>
    public const int PageSize = 100;

    /// <summary>Hard cap on pages walked so a runaway <c>next</c> chain can't spin forever.</summary>
    public const int MaxPages = 50;

    // https://www.deezer.com/en/playlist/3155776842  ·  https://deezer.com/album/302127?utm=…
    [GeneratedRegex(@"^https?://(?:www\.)?deezer\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?(playlist|album)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>True when <paramref name="text"/> is a Deezer playlist or album share link.</summary>
    public static bool TryParse(string? text, out DeezerLinkKind kind, out long id)
    {
        kind = DeezerLinkKind.Playlist;
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = LinkPattern().Match(text.Trim());
        if (!m.Success || !long.TryParse(m.Groups[2].Value, out id)) return false;
        kind = m.Groups[1].Value.Equals("album", StringComparison.OrdinalIgnoreCase)
            ? DeezerLinkKind.Album
            : DeezerLinkKind.Playlist;
        return true;
    }

    /// <summary>The <c>/playlist/{id}</c> or <c>/album/{id}</c> record: title and cover only.</summary>
    public static string BuildInfoUrl(DeezerLinkKind kind, long id)
        => $"https://api.deezer.com/{Segment(kind)}/{id}";

    /// <summary>One page of tracks: <c>/playlist/{id}/tracks?limit=100&amp;index=N</c>.</summary>
    public static string BuildTracksUrl(DeezerLinkKind kind, long id, int index = 0)
        => $"https://api.deezer.com/{Segment(kind)}/{id}/tracks?limit={PageSize}&index={index}";

    private static string Segment(DeezerLinkKind kind) => kind == DeezerLinkKind.Album ? "album" : "playlist";

    /// <summary>The playlist/album title from an info record; null when absent or malformed.</summary>
    public static string? ParseTitle(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out _)) return null;
            return root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One <c>/tracks</c> page: the entries it holds and the URL of the next page (null on the
    /// last one). Album pages carry no per-track album object, so <paramref name="albumTitle"/>
    /// fills that column. Malformed JSON yields no entries and no next page.
    /// </summary>
    public static (IReadOnlyList<PlaylistImportEntry> Entries, string? Next) ParseTracksPage(string json, string? albumTitle = null)
    {
        var entries = new List<PlaylistImportEntry>();
        if (string.IsNullOrWhiteSpace(json)) return (entries, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (entries, null);

            foreach (var t in data.EnumerateArray())
            {
                var title = Str(t, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;
                var artist = t.TryGetProperty("artist", out var ar) ? Str(ar, "name") : string.Empty;
                var album = t.TryGetProperty("album", out var al) ? Str(al, "title") : (albumTitle ?? string.Empty);
                entries.Add(new PlaylistImportEntry(title, artist, album));
            }

            var next = root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            // Only follow Deezer's own API — a hostile "next" must not turn this into an open fetch.
            if (next is not null && !next.StartsWith("https://api.deezer.com/", StringComparison.OrdinalIgnoreCase))
                next = null;
            return (entries, next);
        }
        catch (JsonException) { return (entries, null); }
    }

    /// <summary>
    /// Gathers the whole playlist/album through <paramref name="fetch"/> (URL → body, null on
    /// HTTP failure): the info record for the name, then every <c>/tracks</c> page until
    /// <c>next</c> runs out or <see cref="MaxPages"/> is hit. The name falls back to a
    /// generic one when the info record is missing so the import still goes through.
    /// </summary>
    public static async Task<PlaylistImportParseResult> FetchAllAsync(
        DeezerLinkKind kind, long id, Func<string, CancellationToken, Task<string?>> fetch, CancellationToken ct)
    {
        var info = await fetch(BuildInfoUrl(kind, id), ct).ConfigureAwait(false);
        var title = info is null ? null : ParseTitle(info);
        var albumTitle = kind == DeezerLinkKind.Album ? title : null;

        var entries = new List<PlaylistImportEntry>();
        string? url = BuildTracksUrl(kind, id);
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var body = await fetch(url, ct).ConfigureAwait(false);
            if (body is null) break;
            var (pageEntries, next) = ParseTracksPage(body, albumTitle);
            entries.AddRange(pageEntries);
            url = next;
        }

        var name = string.IsNullOrWhiteSpace(title)
            ? (kind == DeezerLinkKind.Album ? "Deezer album" : "Deezer playlist")
            : title;
        return new PlaylistImportParseResult(name, entries);
    }

    private static string Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
