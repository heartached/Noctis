using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Models;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services;

/// <summary>
/// Lyrics from lrcmux (GitHub #219), a free lyrics API that asks Deezer, KuGou, YouTube Music and
/// Musixmatch and returns the best answer. Its docs (lrcmux.dev/docs, the OpenAPI spec at
/// api.lrcmux.dev/openapi.json, checked 2026-11-09): no key or sign-up, a User-Agent naming the app,
/// "Up to 60 requests per 1m … at most 10 at once", cache hits free; the server is MIT-licensed
/// (github.com/f1nniboy/lrcmux). One request: <c>/get?title=&amp;artist=&amp;duration=</c>, JSON lines
/// with word timings when the source has them. Its match is loose (live 11-09: Dhanush's Tamil "Rowdy
/// Baby" came back as the Telugu version credited to its composer), so the answer is validated
/// against the song like every search hit. Off by default; asked after the other sources.
/// </summary>
public class LrcMuxLyricsService : ILrcMuxLyricsService
{
    private const string BaseUrl = "https://api.lrcmux.dev";
    private const string UserAgent = "Noctis (https://github.com/heartached/Noctis)";
    internal const string ProviderName = "lrcmux";
    internal const int MaxCacheEntries = 256;

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public LrcMuxLyricsService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = $"lrcmux:{artist.Length}:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var url = new StringBuilder($"{BaseUrl}/get?title={Uri.EscapeDataString(trackName.Trim())}");
            if (!LyricsSearchSelector.IsUnknownArtist(artist))
                url.Append("&artist=").Append(Uri.EscapeDataString(artist.Trim()));
            if (durationSeconds > 0)
                url.Append("&duration=").Append(Math.Round(durationSeconds).ToString(CultureInfo.InvariantCulture));

            using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
            request.Headers.Add("User-Agent", UserAgent);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Remember(cacheKey, null);
                return null;
            }
            HttpSafety.EnsureSuccess(response);
            var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
            var result = Parse(json);
            if (result is null
                || !LyricsSearchSelector.MatchesTrack(result, artist, trackName, durationSeconds)
                || LyricsSearchSelector.IsInstrumentalPlaceholder(result.PlainLyrics))
                result = null;
            Remember(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            // Network failure, 429 past the rate limit (busy), 5xx, timeout, malformed JSON.
            // Uncached so a later attempt can succeed.
            throw new LyricsProviderException(ProviderName, ex);
        }
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    /// <summary>The /get JSON as a result: word lines as ELRC, timed lines as LRC, untimed ones as plain text.</summary>
    internal static LrcLibResult? Parse(string json)
    {
        var answer = JsonSerializer.Deserialize<Answer>(json);
        var lines = answer?.Lines?.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (answer is null || lines is null || lines.Count == 0) return null;

        var level = answer.Meta?.Level ?? "none";
        string? synced = null;
        if (level is "word" or "line" && lines.All(l => l.Start is not null))
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append('[').Append(Stamp(line.Start!.Value)).Append(']');
                var words = level == "word" ? line.Words?.Where(w => w.Start is not null && !string.IsNullOrEmpty(w.Text)).ToList() : null;
                if (words is { Count: > 0 })
                {
                    var body = new StringBuilder();
                    foreach (var w in words)
                        body.Append('<').Append(Stamp(w.Start!.Value)).Append('>').Append(w.Text);
                    var end = words[^1].End ?? line.End ?? words[^1].Start!.Value;
                    sb.Append(body.ToString().TrimEnd()).Append('<').Append(Stamp(end)).Append('>');
                }
                else
                {
                    sb.Append(line.Text!.Trim());
                }
            }
            synced = sb.ToString();
        }

        return new LrcLibResult
        {
            TrackName = answer.Track?.Title,
            ArtistName = answer.Track?.Artist,
            AlbumName = answer.Track?.Album,
            Duration = answer.Track?.Duration ?? 0,
            PlainLyrics = string.Join('\n', lines.Select(l => l.Text!.Trim())),
            SyncedLyrics = synced,
        };
    }

    private static string Stamp(long ms) => TimedLyricsBuilder.FormatTimestamp(TimeSpan.FromMilliseconds(ms));

    private sealed class Answer
    {
        [JsonPropertyName("track")] public TrackInfo? Track { get; set; }
        [JsonPropertyName("meta")] public MetaInfo? Meta { get; set; }
        [JsonPropertyName("lines")] public List<Line>? Lines { get; set; }
    }

    private sealed class TrackInfo
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
    }

    private sealed class MetaInfo
    {
        [JsonPropertyName("level")] public string? Level { get; set; }
    }

    private sealed class Line
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("start")] public long? Start { get; set; }
        [JsonPropertyName("end")] public long? End { get; set; }
        [JsonPropertyName("words")] public List<Line>? Words { get; set; }
    }
}
