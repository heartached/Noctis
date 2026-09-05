using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>What a pasted TIDAL link points at.</summary>
public enum TidalLinkKind { Playlist, Album }

/// <summary>
/// Pure helpers for importing a TIDAL playlist or album by link: recognises the share URLs
/// and parses the JSON:API documents of <c>openapi.tidal.com/v2</c> into import entries.
/// Every TIDAL v2 call needs a bearer token (see <see cref="TidalAuthService"/>); HTTP lives
/// in <see cref="PlaylistImportService"/>, nothing here touches the network.
/// </summary>
public static partial class TidalPlaylistLink
{
    public const string ApiBase = "https://openapi.tidal.com/v2";

    /// <summary>Hard cap on pages walked so a runaway <c>links.next</c> chain can't spin forever.</summary>
    public const int MaxPages = 100;

    // https://tidal.com/browse/playlist/1b418bb8-90a7-4f87-901d-707993838346?u
    // https://listen.tidal.com/playlist/1b418bb8-…  ·  https://tidal.com/album/302127
    [GeneratedRegex(@"^https?://(?:www\.|listen\.)?tidal\.com/(?:browse/)?(playlist|album)/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>True when <paramref name="text"/> is a TIDAL playlist or album share link.</summary>
    public static bool TryParse(string? text, out TidalLinkKind kind, out string id)
    {
        kind = TidalLinkKind.Playlist;
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = LinkPattern().Match(text.Trim());
        if (!m.Success) return false;
        var isAlbum = m.Groups[1].Value.Equals("album", StringComparison.OrdinalIgnoreCase);
        var idText = m.Groups[2].Value;
        // Playlists are UUIDs, albums numeric — a mismatch is not a link we know.
        if (isAlbum != idText.All(char.IsAsciiDigit)) return false;
        kind = isAlbum ? TidalLinkKind.Album : TidalLinkKind.Playlist;
        id = idText.ToLowerInvariant();
        return true;
    }

    /// <summary>The <c>/playlists/{id}</c> or <c>/albums/{id}</c> record: name only.</summary>
    public static string BuildInfoUrl(TidalLinkKind kind, string id, string countryCode)
        => $"{ApiBase}/{Segment(kind)}/{Uri.EscapeDataString(id)}?countryCode={Uri.EscapeDataString(countryCode)}";

    /// <summary>
    /// First page of items. <paramref name="nested"/> asks for the tracks' artists and albums
    /// in the same document; when the API refuses the dotted include the caller retries with
    /// <c>items</c> only (titles still import, artist matching just gets weaker).
    /// </summary>
    public static string BuildItemsUrl(TidalLinkKind kind, string id, string countryCode, bool nested = true)
    {
        var include = nested ? "items,items.artists,items.albums" : "items";
        return $"{ApiBase}/{Segment(kind)}/{Uri.EscapeDataString(id)}/relationships/items?countryCode={Uri.EscapeDataString(countryCode)}&include={Uri.EscapeDataString(include)}";
    }

    private static string Segment(TidalLinkKind kind) => kind == TidalLinkKind.Album ? "albums" : "playlists";

    /// <summary>The playlist <c>name</c> / album <c>title</c> from an info document; null when absent or malformed.</summary>
    public static string? ParseName(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                return null;
            var name = Str(attrs, "name");
            if (name.Length == 0) name = Str(attrs, "title");
            return name.Length == 0 ? null : name;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One <c>/relationships/items</c> page: the entries it holds (in playlist order) and the
    /// absolute URL of the next page (null on the last one). Tracks come from <c>included</c>;
    /// artist names and album titles are resolved from the same array when they were included.
    /// Album pages carry no per-track album, so <paramref name="albumTitle"/> fills that column.
    /// Videos and anything not found in <c>included</c> are skipped. Malformed JSON yields nothing.
    /// </summary>
    public static (IReadOnlyList<PlaylistImportEntry> Entries, string? Next) ParseItemsPage(string json, string? albumTitle = null)
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

            var artists = new Dictionary<string, string>(StringComparer.Ordinal);
            var albums = new Dictionary<string, string>(StringComparer.Ordinal);
            var tracks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
            {
                foreach (var res in included.EnumerateArray())
                {
                    if (res.ValueKind != JsonValueKind.Object) continue;
                    var id = Str(res, "id");
                    if (id.Length == 0) continue;
                    switch (Str(res, "type"))
                    {
                        case "tracks": tracks[id] = res; break;
                        case "artists": artists[id] = Attr(res, "name"); break;
                        case "albums": albums[id] = Attr(res, "title"); break;
                    }
                }
            }

            foreach (var item in data.EnumerateArray())
            {
                if (Str(item, "type") != "tracks" || !tracks.TryGetValue(Str(item, "id"), out var track)) continue;
                var title = Attr(track, "title");
                if (title.Length == 0) continue;
                var artist = string.Join(", ", RelatedIds(track, "artists")
                    .Select(a => artists.GetValueOrDefault(a, string.Empty))
                    .Where(n => n.Length > 0));
                var album = albumTitle
                    ?? RelatedIds(track, "albums").Select(a => albums.GetValueOrDefault(a, string.Empty)).FirstOrDefault(t => t.Length > 0)
                    ?? string.Empty;
                entries.Add(new PlaylistImportEntry(title, artist, album));
            }

            string? next = null;
            if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
            {
                var n = Str(links, "next");
                if (n.Length > 0)
                {
                    // TIDAL returns "/playlists/{id}/relationships/items?page[cursor]=…" relative to the API root.
                    next = n.StartsWith('/') ? ApiBase + n : n;
                    // Only follow TIDAL's own API — a hostile "next" must not turn this into an open fetch
                    // that carries the user's bearer token elsewhere.
                    if (!next.StartsWith(ApiBase + "/", StringComparison.OrdinalIgnoreCase)) next = null;
                }
            }
            return (entries, next);
        }
        catch (JsonException) { return (entries, null); }
    }

    /// <summary>
    /// Gathers the whole playlist/album through <paramref name="fetch"/> (URL → body, null on
    /// HTTP failure): the info document for the name, then every items page until
    /// <c>links.next</c> runs out or <see cref="MaxPages"/> is hit. If the first nested-include
    /// request fails the walk restarts with a plain <c>items</c> include. The name falls back
    /// to a generic one when the info document is missing so the import still goes through.
    /// </summary>
    public static async Task<PlaylistImportParseResult> FetchAllAsync(
        TidalLinkKind kind, string id, string countryCode,
        Func<string, CancellationToken, Task<string?>> fetch, CancellationToken ct)
    {
        var info = await fetch(BuildInfoUrl(kind, id, countryCode), ct).ConfigureAwait(false);
        var title = info is null ? null : ParseName(info);
        var albumTitle = kind == TidalLinkKind.Album ? title : null;

        var entries = new List<PlaylistImportEntry>();
        string? url = BuildItemsUrl(kind, id, countryCode, nested: true);
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var body = await fetch(url, ct).ConfigureAwait(false);
            if (body is null)
            {
                if (page == 0 && url.Contains("items.artists", StringComparison.Ordinal))
                {
                    url = BuildItemsUrl(kind, id, countryCode, nested: false);
                    page--;
                    continue;
                }
                break;
            }
            var (pageEntries, next) = ParseItemsPage(body, albumTitle);
            entries.AddRange(pageEntries);
            url = next;
        }

        var name = string.IsNullOrWhiteSpace(title)
            ? (kind == TidalLinkKind.Album ? "TIDAL album" : "TIDAL playlist")
            : title;
        return new PlaylistImportParseResult(name, entries);
    }

    private static IEnumerable<string> RelatedIds(JsonElement resource, string relationship)
    {
        if (!resource.TryGetProperty("relationships", out var rels) || rels.ValueKind != JsonValueKind.Object ||
            !rels.TryGetProperty(relationship, out var rel) || rel.ValueKind != JsonValueKind.Object ||
            !rel.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var d in data.EnumerateArray())
        {
            var id = Str(d, "id");
            if (id.Length > 0) yield return id;
        }
    }

    private static string Attr(JsonElement resource, string prop)
        => resource.TryGetProperty("attributes", out var attrs) ? Str(attrs, prop) : string.Empty;

    private static string Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
