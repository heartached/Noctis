using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.Lyrics;

namespace Noctis.Services;

/// <summary>
/// Fetches word-synced lyrics from Kugou's public endpoints (issue #113; no account, checked
/// 2026-10-01): a song search gives the file hash, the lyrics search lists lyric candidates for
/// that hash, and the download returns KRC, converted by <see cref="KrcLyrics"/> to ELRC.
/// Searching by hash (not by keyword) matters: the keyword search's top candidate for
/// "Adele - Hello" belonged to a compilation master and ran 2 s late.
/// Returns results as <see cref="LrcLibResult"/> for compatibility with existing lyrics infrastructure.
/// </summary>
public class KugouLyricsService : IKugouLyricsService
{
    private const string SongSearchUrl = "https://mobileservice.kugou.com/api/v3/search/song";
    private const string LyricsSearchUrl = "https://krcs.kugou.com/search";
    private const string DownloadUrl = "https://lyrics.kugou.com/download";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
    private readonly HttpClient _http;

    // Same bounded memo as NetEaseService: found lyrics are written to a sidecar, so this
    // only saves repeat lookups within a short window. Cleared when full.
    internal const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public KugouLyricsService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = $"kugou:{artist.Length}:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var song = await FindSongAsync(artist, trackName, durationSeconds, ct);
            if (song == null)
            {
                Remember(cacheKey, null);
                return null;
            }

            var candidate = await FindLyricsCandidateAsync(song.Value.Hash, song.Value.DurationSeconds, ct);
            if (candidate == null)
            {
                Remember(cacheKey, null);
                return null;
            }

            var elrc = await DownloadElrcAsync(candidate.Value.Id, candidate.Value.AccessKey, ct);
            var result = elrc == null ? null : new LrcLibResult
            {
                TrackName = song.Value.Name,
                ArtistName = song.Value.Singer,
                Duration = song.Value.DurationSeconds,
                SyncedLyrics = elrc,
                PlainLyrics = LyricsTextHelper.StripTimestamps(elrc),
            };
            Remember(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or FormatException or InvalidDataException or InvalidOperationException)
        {
            // Network failure, non-success status, timeout, malformed JSON / base64 / KRC.
            // Uncached so a later attempt can succeed.
            throw new LyricsProviderException("Kugou", ex);
        }
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    /// <summary>The song search hit that matches the local track (closest duration wins), or null.</summary>
    private async Task<(string Hash, string Name, string Singer, double DurationSeconds)?> FindSongAsync(
        string artist, string trackName, double durationSeconds, CancellationToken ct)
    {
        var keyword = LyricsSearchSelector.IsUnknownArtist(artist) ? trackName : $"{artist} {trackName}";
        var json = await GetStringAsync(
            $"{SongSearchUrl}?format=json&keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize=20&showtype=1", ct);
        return PickSong(json, artist, trackName, durationSeconds);
    }

    internal static (string Hash, string Name, string Singer, double DurationSeconds)? PickSong(
        string json, string artist, string trackName, double durationSeconds)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array)
            return null;

        (string Hash, string Name, string Singer, double DurationSeconds)? best = null;
        var bestDelta = double.MaxValue;
        foreach (var song in info.EnumerateArray())
        {
            var hash = Str(song, "hash");
            if (string.IsNullOrEmpty(hash)) continue;
            var candidate = new LrcLibResult
            {
                TrackName = Str(song, "songname"),
                ArtistName = Str(song, "singername"),
                Duration = Num(song, "duration"),
            };
            if (!LyricsSearchSelector.MatchesTrack(candidate, artist, trackName, durationSeconds)) continue;

            var delta = durationSeconds > 0 && candidate.Duration > 0 ? Math.Abs(candidate.Duration - durationSeconds) : 0;
            if (best != null && delta >= bestDelta) continue;
            best = (hash, candidate.TrackName!, candidate.ArtistName!, candidate.Duration);
            bestDelta = delta;
        }
        return best;
    }

    /// <summary>The highest-scored lyrics candidate Kugou lists for the song hash, or null.</summary>
    private async Task<(string Id, string AccessKey)?> FindLyricsCandidateAsync(string hash, double durationSeconds, CancellationToken ct)
    {
        var durationMs = Math.Round(durationSeconds * 1000).ToString(CultureInfo.InvariantCulture);
        var json = await GetStringAsync(
            $"{LyricsSearchUrl}?ver=1&man=yes&client=mobi&keyword=&duration={durationMs}&hash={Uri.EscapeDataString(hash)}", ct);
        return PickCandidate(json);
    }

    internal static (string Id, string AccessKey)? PickCandidate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var c in candidates.EnumerateArray())
        {
            var id = Str(c, "id");
            var key = Str(c, "accesskey");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key))
                return (id, key);
        }
        return null;
    }

    private async Task<string?> DownloadElrcAsync(string id, string accessKey, CancellationToken ct)
    {
        var json = await GetStringAsync(
            $"{DownloadUrl}?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=krc&charset=utf8", ct);
        return ParseDownload(json);
    }

    /// <summary>The download's base64 KRC → ELRC; null when the download carries no lyrics.</summary>
    internal static string? ParseDownload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var content = Str(doc.RootElement, "content");
        if (string.IsNullOrWhiteSpace(content)) return null;
        return KrcLyrics.ToElrc(KrcLyrics.Decrypt(Convert.FromBase64String(content)));
    }

    /// <summary>A string or number property as text (Kugou mixes the two for ids).</summary>
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
}
