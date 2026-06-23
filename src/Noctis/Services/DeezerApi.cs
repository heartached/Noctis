using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Noctis.Services;

/// <summary>
/// Pure URL building and JSON parsing for the Deezer public API (no auth required).
/// HTTP side effects live in <see cref="DeezerMetadataService"/>. Kept side-effect-free
/// so parsing/URL logic is unit-testable without the network.
/// </summary>
public static class DeezerApi
{
    /// <summary>
    /// Builds a Deezer track-search URL scoped by artist + title (+ album when present),
    /// using Deezer's advanced query syntax (<c>artist:"…" track:"…" album:"…"</c>).
    /// </summary>
    public static string BuildSearchUrl(string artist, string title, string album)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add($"artist:\"{Sanitize(artist)}\"");
        if (!string.IsNullOrWhiteSpace(title)) parts.Add($"track:\"{Sanitize(title)}\"");
        if (!string.IsNullOrWhiteSpace(album)) parts.Add($"album:\"{Sanitize(album)}\"");
        var query = string.Join(" ", parts);
        return $"https://api.deezer.com/search?q={Uri.EscapeDataString(query)}&limit=5";
    }

    // Quotes delimit Deezer's field filters, so strip embedded quotes from the term.
    private static string Sanitize(string s) => s.Replace("\"", " ").Trim();

    public static string BuildTrackUrl(long trackId) => $"https://api.deezer.com/track/{trackId}";
    public static string BuildAlbumUrl(long albumId) => $"https://api.deezer.com/album/{albumId}";

    /// <summary>Returns the id of the first track in a Deezer <c>/search</c> payload, or null.</summary>
    public static long? FirstTrackId(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return null;
            var first = data[0];
            return first.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                ? id.GetInt64() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Per-track fields parsed from a Deezer <c>/track/{id}</c> payload.</summary>
    public sealed record DeezerTrack(
        long AlbumId, string Title, string Artist, string? AlbumArtist,
        int? TrackNumber, int? DiscNumber, int? Bpm, string? Isrc, int? AlbumYear);

    /// <summary>Album-level fields parsed from a Deezer <c>/album/{id}</c> payload.</summary>
    public sealed record DeezerAlbum(
        string Title, string? AlbumArtist, string? Genre, int? TrackCount, int? Year);

    public static DeezerTrack? ParseTrack(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("title", out _))
                return null;

            var albumId = root.TryGetProperty("album", out var al) && al.TryGetProperty("id", out var aid)
                          && aid.ValueKind == JsonValueKind.Number ? aid.GetInt64() : 0;
            var title = GetString(root, "title");
            var artist = root.TryGetProperty("artist", out var ar) ? GetString(ar, "name") : "";
            string? albumArtist = root.TryGetProperty("contributors", out var c)
                                  && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0
                ? GetString(c[0], "name") : null;
            int? trackNo = GetInt(root, "track_position");
            int? discNo = GetInt(root, "disk_number");
            int? bpm = root.TryGetProperty("bpm", out var b) && b.ValueKind == JsonValueKind.Number
                ? (int?)Math.Round(b.GetDouble()) : null;
            if (bpm is 0) bpm = null;
            var isrc = GetStringOrNull(root, "isrc");

            // Prefer the release date carried on the nested album object — this is the original
            // release Deezer shows in its UI. The standalone /album/{id} endpoint can report a
            // later re-delivery date for re-released editions (e.g. "Bonus Track Version").
            int? albumYear = null;
            if (root.TryGetProperty("album", out var albEl) && albEl.ValueKind == JsonValueKind.Object)
            {
                var rel = GetStringOrNull(albEl, "release_date");
                if (rel is { Length: >= 4 } && int.TryParse(rel.AsSpan(0, 4), out var ay)) albumYear = ay;
            }

            return new DeezerTrack(albumId, title, artist, albumArtist, trackNo, discNo, bpm, isrc, albumYear);
        }
        catch (JsonException) { return null; }
    }

    public static DeezerAlbum? ParseAlbum(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("title", out _))
                return null;

            var title = GetString(root, "title");
            string? albumArtist = root.TryGetProperty("artist", out var ar) ? GetStringOrNull(ar, "name") : null;
            string? genre = root.TryGetProperty("genres", out var g) && g.TryGetProperty("data", out var gd)
                            && gd.ValueKind == JsonValueKind.Array && gd.GetArrayLength() > 0
                ? GetStringOrNull(gd[0], "name") : null;
            int? trackCount = GetInt(root, "nb_tracks");
            int? year = null;
            var releaseDate = GetStringOrNull(root, "release_date");
            if (releaseDate is { Length: >= 4 } && int.TryParse(releaseDate[..4], out var y)) year = y;

            return new DeezerAlbum(title, albumArtist, genre, trackCount, year);
        }
        catch (JsonException) { return null; }
    }

    private static int? GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string? GetStringOrNull(JsonElement el, string prop)
    {
        var s = GetString(el, prop);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Parses a Deezer <c>/search</c> response into tag suggestions (best first). Deezer's
    /// search payload carries title, artist.name and album.title; release year is not present
    /// in this payload, so Year is null — redundancy only needs title/artist/album to fill blanks.
    /// </summary>
    public static IReadOnlyList<TagSuggestion> ParseSearch(string json)
    {
        var list = new List<TagSuggestion>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in data.EnumerateArray())
            {
                var title = GetString(item, "title");
                var artist = item.TryGetProperty("artist", out var a) ? GetString(a, "name") : "";
                var album = item.TryGetProperty("album", out var al) ? GetString(al, "title") : "";
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) continue;
                list.Add(new TagSuggestion(title, artist, album, null, 0.0, "Deezer"));
            }
        }
        catch (JsonException) { /* malformed payload → no suggestions */ }

        return list;
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
