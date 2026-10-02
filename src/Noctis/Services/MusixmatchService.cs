using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.Lyrics;

namespace Noctis.Services;

/// <summary>
/// Fetches lyrics from Musixmatch's app API with an anonymous user token (issue #113; no
/// account). Checked 2026-10-01: with the "mac-ios-v2.0" app id, token.get hands out a token
/// and macro.subtitles.get / track.richsync.get return the real lyrics; the desktop app id
/// ("web-desktop-app-v1.0") was answered with decoy lyrics of an unrelated song — which is
/// also why every match is validated against the local track before it is used.
/// Returns results as <see cref="LrcLibResult"/> for compatibility with existing lyrics infrastructure.
/// </summary>
public class MusixmatchService : IMusixmatchService
{
    private const string BaseUrl = "https://apic.musixmatch.com/ws/1.1/";
    private const string AppId = "mac-ios-v2.0";
    private readonly HttpClient _http;

    // One anonymous token, kept across restarts and renewed when the API answers 401. Kept
    // because token.get is what Musixmatch rate-limits: after a burst of token requests it
    // answered 401 "captcha" while a token it had already issued kept working (2026-10-01).
    // After a refused token.get, no new one is asked for until the back-off passes.
    internal static readonly TimeSpan TokenRefusedBackoff = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly string? _tokenPath;
    private string? _token;
    private DateTime _tokenRetryAfterUtc;

    // Same bounded memo as NetEaseService. Cleared when full.
    internal const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public MusixmatchService(HttpClient httpClient)
        : this(httpClient, Path.Combine(AppPaths.DataRoot, "musixmatch_token")) { }

    /// <param name="tokenPath">Where the token is kept between runs; null keeps it in memory only.</param>
    internal MusixmatchService(HttpClient httpClient, string? tokenPath)
    {
        _http = httpClient;
        _tokenPath = tokenPath;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = $"mxm:{artist.Length}:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var query = $"macro.subtitles.get?format=json&subtitle_format=lrc&app_id={AppId}" +
                        (LyricsSearchSelector.IsUnknownArtist(artist) ? "" : $"&q_artist={Uri.EscapeDataString(artist)}") +
                        $"&q_track={Uri.EscapeDataString(trackName)}" +
                        (durationSeconds > 0 ? $"&q_duration={Math.Round(durationSeconds).ToString(CultureInfo.InvariantCulture)}" : "");
            var macroJson = await GetWithTokenAsync(query, ct);
            var match = ParseMacro(macroJson, artist, trackName, durationSeconds);
            if (match == null)
            {
                Remember(cacheKey, null);
                return null;
            }

            var (result, commonTrackId, hasRichsync) = match.Value;
            if (hasRichsync && !result.Instrumental)
            {
                // Word timings are a bonus over the line-synced subtitle already in hand:
                // a failed richsync call keeps that rather than failing the whole lookup.
                try
                {
                    var richJson = await GetWithTokenAsync(
                        $"track.richsync.get?format=json&app_id={AppId}&commontrack_id={commonTrackId}", ct);
                    if (ParseRichsync(richJson) is { } elrc)
                        result.SyncedLyrics = elrc;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                               or InvalidOperationException && !ct.IsCancellationRequested)
                {
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "Musixmatch:RichsyncFailed", ex.Message);
                }
            }

            var final = result.HasLyrics || result.Instrumental ? result : null;
            Remember(cacheKey, final);
            return final;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException)
        {
            // Network failure, non-success status, timeout, malformed body, or a token the
            // API keeps refusing. Uncached so a later attempt can succeed.
            throw new LyricsProviderException("Musixmatch", ex);
        }
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    /// <summary>Calls the API with the user token, renewing it once on a 401.</summary>
    private async Task<string> GetWithTokenAsync(string pathAndQuery, CancellationToken ct)
    {
        string? rejected = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(rejected, ct);
            var json = await GetStringAsync($"{BaseUrl}{pathAndQuery}&usertoken={Uri.EscapeDataString(token)}", ct);
            if (HeaderStatus(json) != 401) return json;
            rejected = token;
        }
        throw new HttpRequestException("Musixmatch refused the user token (401: expired, or a captcha when rate limited).");
    }

    /// <summary>The current token, or a new one when there is none or <paramref name="rejected"/> is it.</summary>
    private async Task<string> GetTokenAsync(string? rejected, CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            _token ??= ReadSavedToken();
            // A concurrent search may already have replaced the rejected token.
            if (_token != null && _token != rejected) return _token;

            if (DateTime.UtcNow < _tokenRetryAfterUtc)
                throw new HttpRequestException("Musixmatch refused a new user token recently (captcha); waiting before asking again.");

            var json = await GetStringAsync($"{BaseUrl}token.get?format=json&app_id={AppId}", ct);
            var token = ParseToken(json);
            if (token == null)
            {
                _token = null;
                SaveToken(null);
                _tokenRetryAfterUtc = DateTime.UtcNow + TokenRefusedBackoff;
                throw new HttpRequestException($"Musixmatch gave no user token (status {HeaderStatus(json)}).");
            }

            _token = token;
            SaveToken(token);
            return token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private string? ReadSavedToken()
    {
        try
        {
            if (_tokenPath == null || !File.Exists(_tokenPath)) return null;
            var saved = File.ReadAllText(_tokenPath).Trim();
            return saved.Length > 0 ? saved : null;
        }
        catch { return null; }
    }

    /// <summary>Best effort: a token that can't be kept is simply asked for again next run.</summary>
    private void SaveToken(string? token)
    {
        try
        {
            if (_tokenPath == null) return;
            if (token == null) { File.Delete(_tokenPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
            File.WriteAllText(_tokenPath, token);
        }
        catch { }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    // ── Response parsing (internal for fixture tests) ──

    internal static int HeaderStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Message(doc.RootElement) is { } message ? Status(message) : 0;
    }

    internal static string? ParseToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (Message(doc.RootElement) is not { } message || Status(message) != 200) return null;
        return Body(message) is { } body && body.TryGetProperty("user_token", out var t) && t.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(t.GetString())
            ? t.GetString()
            : null;
    }

    /// <summary>
    /// The matched track's lyrics from a macro.subtitles.get answer, or null when nothing
    /// matched or the match is a different song (title / artist / duration ±5 s).
    /// </summary>
    internal static (LrcLibResult Result, long CommonTrackId, bool HasRichsync)? ParseMacro(
        string json, string artist, string trackName, double durationSeconds)
    {
        using var doc = JsonDocument.Parse(json);
        if (Message(doc.RootElement) is not { } message || Status(message) != 200) return null;
        if (Body(message) is not { } body || !body.TryGetProperty("macro_calls", out var calls)) return null;

        if (Call(calls, "matcher.track.get") is not { } matcher || Status(matcher) != 200 ||
            Body(matcher) is not { } matcherBody || !matcherBody.TryGetProperty("track", out var track))
            return null;

        var result = new LrcLibResult
        {
            TrackName = Str(track, "track_name"),
            ArtistName = Str(track, "artist_name"),
            AlbumName = Str(track, "album_name"),
            Duration = Num(track, "track_length"),
            Instrumental = Num(track, "instrumental") == 1,
        };
        if (!LyricsSearchSelector.MatchesTrack(result, artist, trackName, durationSeconds))
            return null;

        if (Call(calls, "track.subtitles.get") is { } subs && Status(subs) == 200 && Body(subs) is { } subsBody &&
            subsBody.TryGetProperty("subtitle_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("subtitle", out var subtitle) || Num(subtitle, "restricted") == 1) continue;
                var lrc = Str(subtitle, "subtitle_body");
                if (!string.IsNullOrWhiteSpace(lrc) && LrcParser.ContainsTimestamp(lrc))
                {
                    result.SyncedLyrics = lrc;
                    break;
                }
            }
        }

        if (Call(calls, "track.lyrics.get") is { } lyrics && Status(lyrics) == 200 && Body(lyrics) is { } lyricsBody &&
            lyricsBody.TryGetProperty("lyrics", out var lyricsObj) && Num(lyricsObj, "restricted") != 1)
        {
            var plain = Str(lyricsObj, "lyrics_body");
            if (!string.IsNullOrWhiteSpace(plain)) result.PlainLyrics = plain.Trim();
        }

        if (result.PlainLyrics == null && result.SyncedLyrics != null)
            result.PlainLyrics = LyricsTextHelper.StripTimestamps(result.SyncedLyrics);

        return (result, (long)Num(track, "commontrack_id"), Num(track, "has_richsync") == 1);
    }

    /// <summary>ELRC from a track.richsync.get answer; null when there is none.</summary>
    internal static string? ParseRichsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (Message(doc.RootElement) is not { } message || Status(message) != 200) return null;
        if (Body(message) is not { } body || !body.TryGetProperty("richsync", out var richsync) ||
            Num(richsync, "restricted") == 1)
            return null;
        var richBody = Str(richsync, "richsync_body");
        return string.IsNullOrWhiteSpace(richBody) ? null : MusixmatchLyrics.RichsyncToElrc(richBody);
    }

    private static JsonElement? Message(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
            ? m
            : null;

    private static JsonElement? Call(JsonElement calls, string name) =>
        calls.ValueKind == JsonValueKind.Object && calls.TryGetProperty(name, out var c) ? Message(c) : null;

    private static int Status(JsonElement message) =>
        message.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Object ? (int)Num(h, "status_code") : 0;

    /// <summary>The message body when it is an object (a miss answers an empty array or nothing).</summary>
    private static JsonElement? Body(JsonElement message) =>
        message.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? b : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
}
