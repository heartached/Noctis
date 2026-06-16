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
