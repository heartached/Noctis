using System.Text.Json;

namespace Noctis.Services;

/// <summary>A proposed set of tags for a track, from a fingerprint or text lookup.</summary>
public sealed record TagSuggestion(
    string Title,
    string Artist,
    string Album,
    int? Year,
    double Confidence,
    string Source,
    string? AlbumArtist = null,
    string? Genre = null,
    int? TrackNumber = null,
    int? TrackCount = null,
    int? DiscNumber = null,
    int? Bpm = null,
    string? Isrc = null);

/// <summary>
/// Pure helpers for the MusicBrainz recording-search API (the text-search fallback path).
/// </summary>
public static class MusicBrainzApi
{
    public const string RecordingSearchBase = "https://musicbrainz.org/ws/2/recording";

    public static string BuildRecordingSearchUrl(string artist, string title, string? album)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) clauses.Add($"recording:\"{Escape(title)}\"");
        if (!string.IsNullOrWhiteSpace(artist)) clauses.Add($"artist:\"{Escape(artist)}\"");
        if (!string.IsNullOrWhiteSpace(album)) clauses.Add($"release:\"{Escape(album)}\"");
        var query = string.Join(" AND ", clauses);
        return $"{RecordingSearchBase}?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"");

    public static IReadOnlyList<TagSuggestion> ParseRecordingSearch(string json)
    {
        var list = new List<TagSuggestion>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recordings", out var recordings) ||
            recordings.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var rec in recordings.EnumerateArray())
        {
            var title = GetString(rec, "title");
            var artist = JoinArtistCredit(rec);
            var (album, year) = FirstRelease(rec);
            var score = rec.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetDouble() / 100.0 : 0;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) continue;
            list.Add(new TagSuggestion(title, artist, album, year, score, "MusicBrainz"));
        }

        return list.OrderByDescending(t => t.Confidence).ToList();
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string JoinArtistCredit(JsonElement rec)
    {
        if (!rec.TryGetProperty("artist-credit", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return "";
        var names = arr.EnumerateArray()
            .Select(a => GetString(a, "name"))
            .Where(n => !string.IsNullOrWhiteSpace(n));
        return string.Join(", ", names);
    }

    private static (string Album, int? Year) FirstRelease(JsonElement rec)
    {
        if (!rec.TryGetProperty("releases", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ("", null);
        foreach (var rel in arr.EnumerateArray())
        {
            var title = GetString(rel, "title");
            int? year = null;
            var date = GetString(rel, "date");
            if (date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y)) year = y;
            if (!string.IsNullOrWhiteSpace(title)) return (title, year);
        }
        return ("", null);
    }
}
