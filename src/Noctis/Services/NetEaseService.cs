using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public NetEaseService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds)
    {
        var cacheKey = $"netease:{artist}|{trackName}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            // Step 1: Search for the song
            var songId = await FindSongIdAsync(artist, trackName, durationSeconds);
            if (songId == null)
            {
                _cache[cacheKey] = null;
                return null;
            }

            // Step 2: Fetch lyrics for the song
            var result = await FetchLyricsAsync(songId.Value, artist, trackName);
            _cache[cacheKey] = result;
            return result;
        }
        catch (Exception)
        {
            // HttpRequestException, TaskCanceledException (timeout), JsonException, etc.
            return null;
        }
    }

    private async Task<long?> FindSongIdAsync(string artist, string trackName, double durationSeconds)
    {
        var query = $"{trackName} {artist}";

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

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var searchResult = JsonSerializer.Deserialize<NetEaseSearchResponse>(json);

        var songs = searchResult?.Result?.Songs;
        if (songs == null || songs.Count == 0) return null;

        // Try to match by duration (within 3 seconds tolerance) and artist name
        var durationMs = (long)(durationSeconds * 1000);
        var bestMatch = songs.FirstOrDefault(s =>
            Math.Abs(s.Duration - durationMs) < 3000 &&
            ArtistMatches(s.Artists, artist));

        // Fall back to artist match only
        bestMatch ??= songs.FirstOrDefault(s => ArtistMatches(s.Artists, artist));

        // Fall back to first result
        bestMatch ??= songs[0];

        return bestMatch.Id;
    }

    private static bool ArtistMatches(List<NetEaseArtist>? artists, string targetArtist)
    {
        if (artists == null || artists.Count == 0) return false;
        return artists.Any(a =>
            !string.IsNullOrEmpty(a.Name) &&
            a.Name.Contains(targetArtist, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<LrcLibResult?> FetchLyricsAsync(long songId, string artist, string trackName)
    {
        var url = $"{LyricsUrl}?id={songId}&lv=1&kv=1&tv=-1";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        request.Headers.Add("Referer", "https://music.163.com/");

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var lyricsResponse = JsonSerializer.Deserialize<NetEaseLyricsResponse>(json);

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
            PlainLyrics = StripTimestamps(syncedLyrics) ?? plainLyrics
        };
    }

    /// <summary>
    /// Removes LRC timestamps from lyrics text to produce plain lyrics.
    /// </summary>
    private static string? StripTimestamps(string? lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent)) return null;

        var lines = lrcContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var plainLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) { plainLines.Add(""); continue; }

            // Remove all [mm:ss.xx] timestamps from start of line
            var text = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]\s*", "");

            // Skip metadata tags like [ar:], [ti:], etc.
            if (text.StartsWith('[') && text.Contains(':')) continue;

            if (!string.IsNullOrWhiteSpace(text))
                plainLines.Add(text);
        }

        var result = string.Join("\n", plainLines).Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
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
    }

    private class NetEaseLyricContent
    {
        [JsonPropertyName("lyric")]
        public string? Lyric { get; set; }
    }
}
