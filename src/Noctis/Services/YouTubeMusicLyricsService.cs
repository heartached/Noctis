using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services;

/// <summary>
/// Fetches lyrics from YouTube Music's public InnerTube API (issue #113; no account, checked
/// 2026-10-01): a songs-only search gives the video id, "next" gives the lyrics tab's browse
/// id, and "browse" as the Android Music client returns timed (line-synced) lyrics, or the
/// plain lyrics shelf when a song has no timing. The client versions are pinned; YouTube
/// retires old ones ("Precondition check failed"), which surfaces as a provider error.
/// Returns results as <see cref="LrcLibResult"/> for compatibility with existing lyrics infrastructure.
/// </summary>
public partial class YouTubeMusicLyricsService : IYouTubeMusicLyricsService
{
    private const string BaseUrl = "https://music.youtube.com/youtubei/v1/";
    private const string WebClientVersion = "1.20250101.01.00";
    private const string AndroidClientVersion = "7.27.52";
    // Search filter "Songs" (the YouTube Music web app's own param).
    private const string SongsFilter = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
    private const int MaxCandidates = 5;
    private readonly HttpClient _http;

    // Same bounded memo as NetEaseService. Cleared when full.
    internal const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    [GeneratedRegex(@"^(?:(\d+):)?(\d{1,2}):(\d{2})$")]
    private static partial Regex DurationRegex();

    public YouTubeMusicLyricsService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = $"ytm:{artist.Length}:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var query = LyricsSearchSelector.IsUnknownArtist(artist) ? trackName : $"{artist} {trackName}";
            var searchJson = await PostAsync("search", WebClient(), new() { ["query"] = query, ["params"] = SongsFilter }, ct);
            var song = PickSong(searchJson, artist, trackName, durationSeconds);
            if (song == null)
            {
                Remember(cacheKey, null);
                return null;
            }

            var nextJson = await PostAsync("next", WebClient(), new() { ["videoId"] = song.Value.VideoId }, ct);
            var browseId = FindLyricsBrowseId(nextJson);
            LrcLibResult? result = null;
            if (browseId != null)
            {
                var browseJson = await PostAsync("browse", AndroidClient(), new() { ["browseId"] = browseId }, ct);
                var (synced, plain) = ParseLyrics(browseJson);
                if (synced != null || plain != null)
                {
                    result = new LrcLibResult
                    {
                        TrackName = song.Value.Title,
                        ArtistName = song.Value.Artist,
                        Duration = song.Value.DurationSeconds,
                        SyncedLyrics = synced,
                        PlainLyrics = plain ?? LyricsTextHelper.StripTimestamps(synced),
                    };
                }
            }

            Remember(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException)
        {
            // Network failure, non-success status (a retired client version answers 400),
            // timeout, or malformed body. Uncached so a later attempt can succeed.
            throw new LyricsProviderException("YouTube Music", ex);
        }
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    private static Dictionary<string, object> WebClient() => new()
    {
        ["clientName"] = "WEB_REMIX", ["clientVersion"] = WebClientVersion, ["hl"] = "en", ["gl"] = "US",
    };

    private static Dictionary<string, object> AndroidClient() => new()
    {
        ["clientName"] = "ANDROID_MUSIC", ["clientVersion"] = AndroidClientVersion, ["androidSdkVersion"] = 34, ["hl"] = "en", ["gl"] = "US",
    };

    private async Task<string> PostAsync(string endpoint, Dictionary<string, object> client, Dictionary<string, object> fields, CancellationToken ct)
    {
        fields["context"] = new Dictionary<string, object> { ["client"] = client };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{endpoint}?prettyPrint=false")
        {
            Content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    // ── Response parsing (internal for fixture tests) ──

    /// <summary>
    /// The first of the top songs-search rows that matches the local track. Each row's second
    /// column reads "Artist • Album • m:ss".
    /// </summary>
    internal static (string VideoId, string Title, string Artist, double DurationSeconds)? PickSong(
        string json, string artist, string trackName, double durationSeconds)
    {
        using var doc = JsonDocument.Parse(json);
        var seen = 0;
        foreach (var row in FindAll(doc.RootElement, "musicResponsiveListItemRenderer"))
        {
            if (seen++ >= MaxCandidates) break;
            var videoId = row.TryGetProperty("playlistItemData", out var pid) && pid.TryGetProperty("videoId", out var v)
                          && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.IsNullOrEmpty(videoId) || !row.TryGetProperty("flexColumns", out var cols) ||
                cols.ValueKind != JsonValueKind.Array || cols.GetArrayLength() < 2)
                continue;

            var title = ColumnText(cols[0]);
            var parts = ColumnText(cols[1]).Split(" • ");
            var candidate = new LrcLibResult
            {
                TrackName = title,
                ArtistName = parts[0],
                Duration = ParseDuration(parts[^1]),
            };
            if (LyricsSearchSelector.MatchesTrack(candidate, artist, trackName, durationSeconds))
                return (videoId, title, parts[0], candidate.Duration);
        }
        return null;
    }

    /// <summary>The lyrics tab's browse id ("MPLY…") from a "next" answer; null when the song has no lyrics tab.</summary>
    internal static string? FindLyricsBrowseId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var id in FindAll(doc.RootElement, "browseId"))
        {
            if (id.ValueKind == JsonValueKind.String && id.GetString() is { } s && s.StartsWith("MPLY", StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    /// <summary>
    /// (LRC, plain) from a "browse" answer: timed lyrics become LRC lines (the "♪" filler
    /// lines dropped — the lyrics page draws its own intro placeholder), else the plain shelf.
    /// </summary>
    internal static (string? Synced, string? Plain) ParseLyrics(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var timed in FindAll(doc.RootElement, "timedLyricsData"))
        {
            if (timed.ValueKind != JsonValueKind.Array) continue;
            var sb = new StringBuilder();
            foreach (var line in timed.EnumerateArray())
            {
                var text = line.TryGetProperty("lyricLine", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(text) || text == "♪") continue;
                if (!line.TryGetProperty("cueRange", out var cue) || !cue.TryGetProperty("startTimeMilliseconds", out var startEl)) continue;
                var startText = startEl.ValueKind == JsonValueKind.String ? startEl.GetString() : startEl.GetRawText();
                if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) continue;
                sb.Append('[').Append(TimedLyricsBuilder.FormatTimestamp(TimeSpan.FromMilliseconds(ms))).Append(']').Append(text).Append('\n');
            }
            if (sb.Length > 0) return (sb.ToString().TrimEnd('\n'), null);
        }

        foreach (var shelf in FindAll(doc.RootElement, "musicDescriptionShelfRenderer"))
        {
            if (shelf.TryGetProperty("description", out var description))
            {
                var plain = RunsText(description).Trim();
                if (plain.Length > 0) return (null, plain);
            }
        }
        return (null, null);
    }

    private static string ColumnText(JsonElement column) =>
        column.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var r) && r.TryGetProperty("text", out var text)
            ? RunsText(text)
            : string.Empty;

    private static string RunsText(JsonElement text)
    {
        if (!text.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return string.Empty;
        var sb = new StringBuilder();
        foreach (var run in runs.EnumerateArray())
            if (run.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
        return sb.ToString();
    }

    private static double ParseDuration(string text)
    {
        var m = DurationRegex().Match(text.Trim());
        if (!m.Success) return 0;
        var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return hours * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
               + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Every value of <paramref name="property"/> anywhere under <paramref name="root"/>, depth-first.
    /// InnerTube nests renderers deeply and moves them between releases, so lookups go by name, not path.</summary>
    private static IEnumerable<JsonElement> FindAll(JsonElement root, string property)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.ValueKind == JsonValueKind.Object)
            {
                var children = new List<JsonElement>();
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals(property)) yield return p.Value;
                    else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) children.Add(p.Value);
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                var items = e.EnumerateArray().ToList();
                for (var i = items.Count - 1; i >= 0; i--)
                    if (items[i].ValueKind is JsonValueKind.Object or JsonValueKind.Array) stack.Push(items[i]);
            }
        }
    }
}
