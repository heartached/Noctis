using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Fetches lyrics from NetEase Cloud Music (music.163.com) public API.
/// Returns results as <see cref="LrcLibResult"/> for compatibility with existing lyrics infrastructure.
/// </summary>
public class NetEaseService : INetEaseService
{
    private const string SearchUrl = "https://music.163.com/api/search/get/web";
    private const string LyricsUrl = "https://music.163.com/api/song/lyric";
    private readonly HttpClient _http;

    // One entry per distinct track looked up this session (a hit holds the whole LRC
    // twice: synced + stripped plain). It used to grow for the life of the process; the
    // lyrics a hit fetched are written to a sidecar, so a replay reads them locally and
    // this memo only saves repeat lookups within a short window. Capped the same way as
    // DominantColorExtractor's caches: cleared when full.
    internal const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    internal int CacheCount => _cache.Count;

    public NetEaseService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = $"netease:{artist.Length}:{artist}|{trackName}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            // Step 1: Search for the song
            var songId = await FindSongIdAsync(artist, trackName, durationSeconds, ct);
            if (songId == null)
            {
                Remember(cacheKey, null);
                return null;
            }

            // Step 2: Fetch lyrics for the song
            var result = await FetchLyricsAsync(songId.Value, artist, trackName, ct);
            Remember(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancel (track skip) — propagate silently.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Network failure, non-success status, timeout, or malformed body —
            // distinct from a definitive miss (cached null above), and uncached
            // so a later attempt can succeed.
            throw new LyricsProviderException("NetEase", ex);
        }
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    private async Task<long?> FindSongIdAsync(string artist, string trackName, double durationSeconds, CancellationToken ct)
    {
        // "Unknown Artist" is the library's placeholder: searching for it poisons the results.
        var query = LyricsSearchSelector.IsUnknownArtist(artist) ? trackName : $"{trackName} {artist}";

        using var request = new HttpRequestMessage(HttpMethod.Post, SearchUrl);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        request.Headers.Add("Referer", "https://music.163.com/");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["s"] = query,
            ["type"] = "1",       // 1 = song search
            ["limit"] = "10",
            ["offset"] = "0"
        });

        using var response = await _http.SendAsync(request, ct);
        // A non-success status is a provider error, not a miss — a genuine
        // "no results" from NetEase is a 200 with an empty songs list.
        response.EnsureSuccessStatusCode();

        var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);

        // For IPs outside mainland China this endpoint answers 200 with
        // "abroad": true and "result" as an opaque encrypted hex STRING instead
        // of the songs object. The provider answered — that is a definitive
        // miss, not a transfer error.
        using (var probe = JsonDocument.Parse(json))
        {
            if (probe.RootElement.ValueKind == JsonValueKind.Object &&
                probe.RootElement.TryGetProperty("result", out var resultEl) &&
                resultEl.ValueKind == JsonValueKind.String)
                return null;
        }

        var searchResult = JsonSerializer.Deserialize<NetEaseSearchResponse>(json);

        var songs = searchResult?.Result?.Songs;
        if (songs == null || songs.Count == 0) return null;

        return PickSongId(songs, artist, trackName, durationSeconds);
    }

    /// <summary>
    /// The search hit that is the local song: its title and artist must match (as every other
    /// source checks, <see cref="LyricsSearchSelector.MatchesTrack"/>), the closest length within
    /// 3 s first. The search's first hit used to be taken whatever it was (GitHub #162): a song
    /// NetEase doesn't have got another song's lyrics.
    /// </summary>
    private static long? PickSongId(List<NetEaseSong> songs, string artist, string trackName, double durationSeconds)
    {
        var matching = songs.Where(s => LyricsSearchSelector.MatchesTrack(new LrcLibResult
        {
            TrackName = s.Name,
            ArtistName = string.Join(", ", s.Artists?.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)) ?? []),
        }, artist, trackName, 0)).ToList();

        var durationMs = (long)(durationSeconds * 1000);
        var bestMatch = matching.FirstOrDefault(s => Math.Abs(s.Duration - durationMs) < 3000)
                        ?? matching.FirstOrDefault();
        return bestMatch?.Id;
    }

    private async Task<LrcLibResult?> FetchLyricsAsync(long songId, string artist, string trackName, CancellationToken ct)
    {
        var url = $"{LyricsUrl}?id={songId}&lv=1&kv=1&tv=-1";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        request.Headers.Add("Referer", "https://music.163.com/");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
        var lyricsResponse = JsonSerializer.Deserialize<NetEaseLyricsResponse>(json);

        // NetEase marks an instrumental song with "nolyric", or with lyrics that only say
        // "纯音乐，请欣赏" ("instrumental, please enjoy"): an answer, not lyrics (GitHub #162).
        if (lyricsResponse is { NoLyric: true }
            || LyricsSearchSelector.IsInstrumentalPlaceholder(lyricsResponse?.Lrc?.Lyric ?? lyricsResponse?.Klyric?.Lyric))
            return new LrcLibResult { TrackName = trackName, ArtistName = artist, Instrumental = true };

        var syncedLyrics = lyricsResponse?.Lrc?.Lyric;
        var plainLyrics = lyricsResponse?.Klyric?.Lyric;

        // NetEase "lrc" field contains timestamped lyrics; if it has timestamps, it's synced
        var hasSyncedContent = !string.IsNullOrWhiteSpace(syncedLyrics) &&
                               syncedLyrics.Contains('[') &&
                               syncedLyrics.Contains(':');

        if (!hasSyncedContent && string.IsNullOrWhiteSpace(plainLyrics))
        {
            // No useful lyrics content
            if (string.IsNullOrWhiteSpace(syncedLyrics)) return null;
        }

        // Map to LrcLibResult for compatibility
        return new LrcLibResult
        {
            TrackName = trackName,
            ArtistName = artist,
            SyncedLyrics = hasSyncedContent ? syncedLyrics : null,
            PlainLyrics = !string.IsNullOrWhiteSpace(syncedLyrics)
                ? LyricsTextHelper.StripTimestamps(syncedLyrics)
                : plainLyrics
        };
    }

    // ── JSON response models ──

    private class NetEaseSearchResponse
    {
        [JsonPropertyName("result")]
        public NetEaseSearchResult? Result { get; set; }
    }

    private class NetEaseSearchResult
    {
        [JsonPropertyName("songs")]
        public List<NetEaseSong>? Songs { get; set; }
    }

    private class NetEaseSong
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("duration")]
        public long Duration { get; set; }

        [JsonPropertyName("artists")]
        public List<NetEaseArtist>? Artists { get; set; }
    }

    private class NetEaseArtist
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class NetEaseLyricsResponse
    {
        [JsonPropertyName("lrc")]
        public NetEaseLyricContent? Lrc { get; set; }

        [JsonPropertyName("klyric")]
        public NetEaseLyricContent? Klyric { get; set; }

        [JsonPropertyName("tlyric")]
        public NetEaseLyricContent? Tlyric { get; set; }

        [JsonPropertyName("nolyric")]
        public bool NoLyric { get; set; }
    }

    private class NetEaseLyricContent
    {
        [JsonPropertyName("lyric")]
        public string? Lyric { get; set; }
    }
}
