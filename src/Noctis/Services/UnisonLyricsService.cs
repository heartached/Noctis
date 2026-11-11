using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.Lyrics;

namespace Noctis.Services;

/// <summary>
/// Fetches community lyrics from Unison, the Better Lyrics database (Discord request, nutf!xx).
/// Its docs (docs.betterlyrics.org/unison, checked 2026-10-17): "Reading from it needs no key and
/// no sign-in", "Reads are rate limited to 120 requests per minute per IP"; the corpus is ODbL and
/// a player that displays it only needs to attribute it ("Lyrics from Unison"). Two requests: the
/// search <c>/lyrics/search?q=</c> lists versions with title, artist and duration but no lyrics,
/// then <c>/lyrics/{id}</c> is the chosen one, as TTML, LRC or plain text.
/// Returns results as <see cref="LrcLibResult"/> for compatibility with existing lyrics infrastructure.
/// </summary>
public class UnisonLyricsService : IUnisonLyricsService
{
    private const string BaseUrl = "https://unison.boidu.dev";
    private const string UserAgent = "Noctis (https://github.com/heartached/Noctis)";
    private readonly HttpClient _http;

    // Same bounded memo as KugouLyricsService; also keeps repeat plays well inside the
    // 120-requests-a-minute read limit.
    internal const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public UnisonLyricsService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = $"unison:{artist.Length}:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var query = LyricsSearchSelector.IsUnknownArtist(artist) ? trackName.Trim() : $"{artist.Trim()} {trackName.Trim()}";
            var search = await GetStringAsync($"{BaseUrl}/lyrics/search?q={Uri.EscapeDataString(query)}&limit=20", ct);
            var version = search == null ? null : PickVersion(search, artist, trackName, durationSeconds);
            if (version == null)
            {
                Remember(cacheKey, null);
                return null;
            }

            var entry = await GetStringAsync($"{BaseUrl}/lyrics/{version.Value.Id.ToString(CultureInfo.InvariantCulture)}", ct);
            var result = entry == null ? null : ParseEntry(entry);
            // The entry itself carries no duration; the search hit's is the matched one.
            if (result is { Duration: <= 0 }) result.Duration = version.Value.DurationSeconds;
            Remember(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidDataException or InvalidOperationException)
        {
            // Network failure, non-success status (429 past the read limit too), timeout,
            // malformed JSON / TTML. Uncached so a later attempt can succeed.
            throw new LyricsProviderException("Unison", ex);
        }
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    /// <summary>The body, or null on 404 — Unison's "Lyrics not found" (a definitive miss).</summary>
    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        HttpSafety.EnsureSuccess(response);
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    /// <summary>
    /// The id (and duration) of the search hit that is the local track, or null. Every hit must match by title,
    /// artist and duration (±5 s; Unison keys versions on a YouTube video, whose length can run
    /// past the song's); among those an exact title beats a cut that merely contains it, then
    /// word sync beats line sync beats plain, then the closer duration, then the search's own
    /// order (best voted first).
    /// </summary>
    internal static (long Id, double DurationSeconds)? PickVersion(string json, string artist, string trackName, double durationSeconds)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        (long Id, double DurationSeconds)? best = null;
        (int Exact, int Sync, double Delta) bestKey = default;
        foreach (var hit in data.EnumerateArray())
        {
            if (hit.ValueKind != JsonValueKind.Object
                || !hit.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0)
                continue;
            if (hit.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            var candidate = new LrcLibResult
            {
                TrackName = Str(hit, "song"),
                ArtistName = Str(hit, "artist"),
                Duration = Num(hit, "duration"),
            };
            if (!LyricsSearchSelector.MatchesTrack(candidate, artist, trackName, durationSeconds)) continue;

            var key = (
                Exact: string.Equals(candidate.TrackName!.Trim(), trackName.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                Sync: Str(hit, "syncType") switch { "richsync" => 2, "linesync" => 1, _ => 0 },
                Delta: durationSeconds > 0 && candidate.Duration > 0 ? Math.Abs(candidate.Duration - durationSeconds) : 0);
            if (best != null && !IsBetter(key, bestKey)) continue;
            best = (id, candidate.Duration);
            bestKey = key;
        }
        return best;
    }

    private static bool IsBetter((int Exact, int Sync, double Delta) a, (int Exact, int Sync, double Delta) b) =>
        a.Exact != b.Exact ? a.Exact > b.Exact
        : a.Sync != b.Sync ? a.Sync > b.Sync
        : a.Delta < b.Delta;

    /// <summary><c>/lyrics/{id}</c> → a result; null when it carries no lyrics.</summary>
    internal static LrcLibResult? ParseEntry(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        var lyrics = Str(data, "lyrics");
        if (string.IsNullOrWhiteSpace(lyrics)) return null;

        string? synced = null;
        string? plain;
        switch (Str(data, "format"))
        {
            case "ttml":
                var converted = OnlineTtml.Convert(lyrics)
                    ?? throw new InvalidDataException("Unison sent TTML that does not parse.");
                synced = converted.Synced;
                plain = converted.Plain;
                break;
            case "lrc":
                synced = lyrics;
                plain = LyricsTextHelper.StripTimestamps(lyrics);
                break;
            default:
                plain = lyrics;
                break;
        }

        return new LrcLibResult
        {
            TrackName = Str(data, "song"),
            ArtistName = Str(data, "artist"),
            AlbumName = Str(data, "album"),
            Duration = Num(data, "duration"),
            SyncedLyrics = synced,
            PlainLyrics = plain,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
}
