using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noctis.Services;

/// <summary>One finished play for a scrobbling service: when it started (Unix seconds) and what it was.</summary>
public sealed record ScrobbleEntry(string Artist, string Title, string? Album, int DurationSeconds, long Timestamp);

/// <summary>How a send ended. Retry: nothing was recorded, try again later (offline, 5xx, 429, Last.fm
/// "try again"); Rejected: the service answered and will never take these (bad data), so they are
/// dropped; SessionInvalid: the stored login is dead, the user must sign in again.</summary>
public enum ScrobbleSendResult { Ok, Retry, Rejected, SessionInvalid }

/// <summary>
/// Last.fm's scrobbling HTTP calls without any app state, for the phone (web auth, batch scrobble,
/// now playing, profile). The desktop's LastFmService keeps its own single-play path.
/// </summary>
public sealed class LastFmScrobbleClient
{
    /// <summary>Last.fm takes at most 50 scrobbles per track.scrobble call.</summary>
    public const int MaxBatch = 50;
    private const string AuthBase = "https://www.last.fm/api/auth/";

    private readonly HttpClient _http;

    public LastFmScrobbleClient(HttpClient http) => _http = http;

    /// <summary>Step 1 of web auth: a request token, or null when Last.fm could not be reached.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync(Signed(new() { ["method"] = "auth.getToken" }), ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Step 2: the page the user approves Noctis on.</summary>
    public static string AuthUrl(string token) => $"{AuthBase}?api_key={LastFmApi.ApiKey}&token={Uri.EscapeDataString(token)}";

    /// <summary>Step 3, after the user approved: the session key and user name, or null while not approved.</summary>
    public async Task<(string SessionKey, string UserName)?> GetSessionAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync(Signed(new() { ["method"] = "auth.getSession", ["token"] = token }), ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("session", out var s)) return null;
            var key = s.GetProperty("key").GetString();
            var name = s.GetProperty("name").GetString();
            return string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name) ? null : (key, name);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The account's scrobble count for the Services row; null when unknown.</summary>
    public async Task<long?> GetPlayCountAsync(string userName, CancellationToken ct = default)
    {
        try
        {
            var url = $"{LastFmApi.ApiBase}?method=user.getinfo&user={Uri.EscapeDataString(userName)}&api_key={LastFmApi.ApiKey}&format=json";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("user", out var u) && u.TryGetProperty("playcount", out var p)
                   && long.TryParse(p.GetString(), out var n) ? n : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>track.scrobble for up to <see cref="MaxBatch"/> plays in one call.</summary>
    public async Task<ScrobbleSendResult> ScrobbleAsync(string sessionKey, IReadOnlyList<ScrobbleEntry> plays, CancellationToken ct = default)
    {
        if (plays.Count == 0) return ScrobbleSendResult.Ok;
        if (plays.Count > MaxBatch) throw new ArgumentException($"At most {MaxBatch} plays per call", nameof(plays));
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "track.scrobble",
            ["api_key"] = LastFmApi.ApiKey,
            ["sk"] = sessionKey,
        };
        for (var i = 0; i < plays.Count; i++)
        {
            p[$"artist[{i}]"] = plays[i].Artist;
            p[$"track[{i}]"] = plays[i].Title;
            p[$"timestamp[{i}]"] = plays[i].Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(plays[i].Album)) p[$"album[{i}]"] = plays[i].Album!;
            if (plays[i].DurationSeconds > 0) p[$"duration[{i}]"] = plays[i].DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return await PostAsync(p, ct).ConfigureAwait(false);
    }

    /// <summary>track.updateNowPlaying; best effort, a failure is not worth a retry.</summary>
    public Task<ScrobbleSendResult> UpdateNowPlayingAsync(string sessionKey, ScrobbleEntry play, CancellationToken ct = default)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "track.updateNowPlaying",
            ["api_key"] = LastFmApi.ApiKey,
            ["sk"] = sessionKey,
            ["artist"] = play.Artist,
            ["track"] = play.Title,
        };
        if (!string.IsNullOrWhiteSpace(play.Album)) p["album"] = play.Album!;
        if (play.DurationSeconds > 0) p["duration"] = play.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return PostAsync(p, ct);
    }

    private async Task<ScrobbleSendResult> PostAsync(SortedDictionary<string, string> p, CancellationToken ct)
    {
        Sign(p);
        p["format"] = "json";
        try
        {
            using var resp = await _http.PostAsync(LastFmApi.ApiBase, new FormUrlEncodedContent(p), ct).ConfigureAwait(false);
            var body = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
            if (TryReadError(body, out var code)) return Classify(code);
            if (resp.IsSuccessStatusCode) return ScrobbleSendResult.Ok;
            return (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500 ? ScrobbleSendResult.Retry : ScrobbleSendResult.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return ScrobbleSendResult.Retry;
        }
    }

    // Last.fm's error codes: 9 invalid session key; 11 service offline, 16 temporary error, 29 rate
    // limit exceeded: try again; anything else (bad parameters, ...) will not get better.
    private static ScrobbleSendResult Classify(int code) => code switch
    {
        9 or 4 or 26 => ScrobbleSendResult.SessionInvalid,
        11 or 16 or 29 => ScrobbleSendResult.Retry,
        _ => ScrobbleSendResult.Rejected,
    };

    private static bool TryReadError(string body, out int code)
    {
        code = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("error", out var e) && e.TryGetInt32(out code);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(SortedDictionary<string, string> p, CancellationToken ct)
    {
        p["format"] = "json";
        var url = new StringBuilder(LastFmApi.ApiBase).Append('?');
        foreach (var kv in p) url.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value)).Append('&');
        using var resp = await _http.GetAsync(url.ToString().TrimEnd('&'), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false));
    }

    private static SortedDictionary<string, string> Signed(Dictionary<string, string> extra)
    {
        var p = new SortedDictionary<string, string>(extra, StringComparer.Ordinal) { ["api_key"] = LastFmApi.ApiKey };
        Sign(p);
        return p;
    }

    /// <summary>api_sig: MD5 of every parameter (name then value, sorted by name) plus the shared secret;
    /// "format" and "callback" are never signed.</summary>
    private static void Sign(SortedDictionary<string, string> p)
    {
        var sb = new StringBuilder();
        foreach (var kv in p) sb.Append(kv.Key).Append(kv.Value);
        sb.Append(LastFmApi.ApiSecret);
        p["api_sig"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>ListenBrainz over the user's token (official server), for the phone: validate, batch
/// "import" listens, "playing_now".</summary>
public sealed class ListenBrainzScrobbleClient
{
    /// <summary>Plays per submit-listens call (the server takes up to 1000).</summary>
    public const int MaxBatch = 50;
    private const string ApiUrl = "https://api.listenbrainz.org";

    private readonly HttpClient _http;

    public ListenBrainzScrobbleClient(HttpClient http) => _http = http;

    /// <summary>The token's user name; null with <paramref name="reason"/> "invalid" or "unreachable".</summary>
    public async Task<(string? UserName, string? Reason)> ValidateAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}/1/validate-token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", token.Trim());
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, "invalid");
            if (!resp.IsSuccessStatusCode) return (null, "unreachable");
            using var doc = JsonDocument.Parse(await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("valid", out var v) || v.ValueKind != JsonValueKind.True)
                return (null, "invalid");
            return root.TryGetProperty("user_name", out var u) && u.GetString() is { Length: > 0 } name ? (name, null) : (null, "invalid");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return (null, "unreachable");
        }
    }

    public Task<ScrobbleSendResult> SubmitAsync(string token, IReadOnlyList<ScrobbleEntry> plays, CancellationToken ct = default)
        => plays.Count == 0 ? Task.FromResult(ScrobbleSendResult.Ok) : PostAsync(token, plays.Count == 1 ? "single" : "import", plays, ct);

    public Task<ScrobbleSendResult> UpdateNowPlayingAsync(string token, ScrobbleEntry play, CancellationToken ct = default)
        => PostAsync(token, "playing_now", new[] { play }, ct);

    private async Task<ScrobbleSendResult> PostAsync(string token, string listenType, IReadOnlyList<ScrobbleEntry> plays, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/1/submit-listens")
            {
                Content = new StringContent(BuildPayload(listenType, plays), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", token.Trim());
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return ScrobbleSendResult.Ok;
            return resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => ScrobbleSendResult.SessionInvalid,
                HttpStatusCode.TooManyRequests => ScrobbleSendResult.Retry,
                >= HttpStatusCode.InternalServerError => ScrobbleSendResult.Retry,
                _ => ScrobbleSendResult.Rejected,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return ScrobbleSendResult.Retry;
        }
    }

    internal static string BuildPayload(string listenType, IReadOnlyList<ScrobbleEntry> plays)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("listen_type", listenType);
            w.WriteStartArray("payload");
            foreach (var p in plays)
            {
                w.WriteStartObject();
                if (listenType != "playing_now") w.WriteNumber("listened_at", p.Timestamp);
                w.WriteStartObject("track_metadata");
                w.WriteString("artist_name", p.Artist);
                w.WriteString("track_name", p.Title);
                if (!string.IsNullOrWhiteSpace(p.Album)) w.WriteString("release_name", p.Album);
                w.WriteStartObject("additional_info");
                if (p.DurationSeconds > 0) w.WriteNumber("duration_ms", p.DurationSeconds * 1000L);
                w.WriteString("submission_client", "Noctis");
                w.WriteString("media_player", "Noctis Mobile");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
