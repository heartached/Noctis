using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.LocalApi;

namespace Noctis.Services;

/// <summary>
/// Local API mode of <see cref="WebRemoteServer"/>: the versioned /api/v1 JSON surface
/// and its SSE stream. Reference: docs/LOCAL-API.md. Every read of player / library
/// state goes through the UI-thread marshal and only snapshots; slow work (library
/// search, artwork file reads) runs on the thread pool afterwards, so no request ever
/// holds the UI thread for more than a list copy.
/// </summary>
public sealed partial class WebRemoteServer
{
    internal const int SearchDefaultLimit = 20;
    internal const int SearchMaxLimit = 50;
    internal const int QueueDefaultLimit = 100;
    internal const int QueueMaxLimit = 500;
    internal const int QueueAddMaxIds = 500;
    private const int MaxArtworkBytes = 20 * 1024 * 1024;

    private readonly object _authGate = new();
    private readonly Queue<long> _authFailures = new();
    private long _lockedUntilTicks;

    private readonly record struct ApiResult(int Status, string ContentType, byte[] Body, string? ExtraHeaders = null);

    private static ApiResult Json(int status, object payload) =>
        new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(LocalApiDto.Serialize(payload)));

    private static ApiResult Error(int status, string code, string message, string? extraHeaders = null) =>
        Json(status, LocalApiDto.Error(code, message)) with { ExtraHeaders = extraHeaders };

    private const string CorsHeaders =
        "Access-Control-Allow-Origin: *\r\n" +
        "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
        "Access-Control-Allow-Headers: Authorization, Content-Type\r\n" +
        "Access-Control-Max-Age: 600\r\n";

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        _ => "Internal Server Error",
    };

    private static async Task WriteAsync(NetworkStream stream, ApiResult r, CancellationToken ct)
    {
        var header =
            $"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\n" +
            (r.Body.Length > 0 || r.Status != 204 ? $"Content-Type: {r.ContentType}\r\n" : "") +
            $"Content-Length: {r.Body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            CorsHeaders +
            (r.ExtraHeaders ?? "") +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        if (r.Body.Length > 0) await stream.WriteAsync(r.Body, ct);
    }

    private async Task HandleLocalApiAsync(NetworkStream stream, LineReader reader, string method, string target,
        Dictionary<string, string> headers, CancellationToken readCt, CancellationToken ct)
    {
        ApiResult result;
        try
        {
            result = await ProcessLocalApiAsync(stream, reader, method, target, headers, readCt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return; // body never arrived inside the read window — just drop
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "LocalApi.Request", ex.Message);
            result = Error(500, "internal_error", "The request failed inside Noctis.");
        }
        if (result.Status == 0) return; // response already written (event stream)
        await WriteAsync(stream, result, ct);
    }

    private async Task<ApiResult> ProcessLocalApiAsync(NetworkStream stream, LineReader reader, string method,
        string target, Dictionary<string, string> headers, CancellationToken readCt, CancellationToken ct)
    {
        // DNS rebinding: a web page on evil.example resolved to 127.0.0.1 still sends
        // its own name as Host. The token blocks it anyway; this refuses it earlier.
        if (headers.TryGetValue("Host", out var host) && !IsLoopbackHost(host))
            return Error(403, "forbidden_host", "The Local API only answers requests addressed to 127.0.0.1 or localhost.");

        if (method == "OPTIONS")
            return new ApiResult(204, "text/plain", Array.Empty<byte>()); // CORS preflight carries no credentials

        var qIndex = target.IndexOf('?');
        var path = (qIndex < 0 ? target : target[..qIndex]).TrimEnd('/');
        var query = ParseQuery(target);

        if (!path.StartsWith("/api/v1/", StringComparison.Ordinal))
            return Error(404, "not_found", "Unknown route. The Local API lives under /api/v1/.");

        var retryAfter = LockoutSecondsRemaining();
        if (retryAfter > 0)
            return Error(429, "rate_limited", "Too many failed authentication attempts. Try again later.",
                $"Retry-After: {retryAfter}\r\n");

        if (!IsLocalAuthorized(headers, query))
        {
            RecordAuthFailure();
            return Error(401, "unauthorized",
                "Missing or wrong token. Send 'Authorization: Bearer <token>' or '?token=<token>' (see local-api.json).",
                "WWW-Authenticate: Bearer realm=\"Noctis\"\r\n");
        }

        var route = path["/api/v1".Length..];
        var allowed = AllowedMethod(route);
        if (allowed == null)
            return Error(404, "not_found", $"Unknown route '{path}'.");
        if (method != allowed)
            return Error(405, "method_not_allowed", $"Use {allowed} for {path}.", $"Allow: {allowed}, OPTIONS\r\n");

        JsonElement? body = null;
        if (method == "POST")
        {
            if (headers.ContainsKey("Transfer-Encoding"))
                return Error(411, "length_required", "Send the body with a Content-Length (chunked bodies aren't accepted).");
            var length = 0;
            if (headers.TryGetValue("Content-Length", out var cl) &&
                (!int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0))
                return Error(400, "bad_request", "Invalid Content-Length.");
            if (length > _local!.MaxBodyBytes)
                return Error(413, "payload_too_large", $"Request bodies are limited to {_local.MaxBodyBytes} bytes.");
            if (length > 0)
            {
                var bytes = await reader.ReadBytesAsync(length, readCt);
                if (bytes == null) return Error(400, "bad_request", "The request body ended early.");
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    body = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return Error(400, "bad_request", "The request body is not valid JSON.");
                }
            }
        }

        if (route == "/events")
        {
            var wantsLyrics = query.TryGetValue("lyrics", out var ly) && (ly == "1" || ly.Equals("true", StringComparison.OrdinalIgnoreCase));
            var sseHeader =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "X-Accel-Buffering: no\r\n" +
                CorsHeaders +
                "Connection: close\r\n\r\n";
            var ran = await _hub!.RunAsync(stream, sseHeader, wantsLyrics, ct);
            return ran
                ? default // written by the hub
                : Error(503, "too_many_streams", $"At most {_hub.MaxStreams} event streams can be open at once.",
                    "Retry-After: 5\r\n");
        }

        return await RouteLocalAsync(route, query, body);
    }

    /// <summary>The one method a known route accepts, or null for an unknown route.</summary>
    private static string? AllowedMethod(string route)
    {
        switch (route)
        {
            case "/status":
            case "/now-playing":
            case "/queue":
            case "/library/search":
            case "/lyrics/current":
            case "/events":
            case "/artwork/current":
                return "GET";
            case "/playback/play":
            case "/playback/pause":
            case "/playback/toggle":
            case "/playback/next":
            case "/playback/previous":
            case "/playback/seek":
            case "/playback/volume":
            case "/playback/shuffle":
            case "/playback/repeat":
            case "/queue/add":
                return "POST";
        }
        return route.StartsWith("/artwork/", StringComparison.Ordinal) ? "GET" : null;
    }

    private async Task<ApiResult> RouteLocalAsync(string route, Dictionary<string, string> query, JsonElement? body)
    {
        switch (route)
        {
            case "/status":
                return Json(200, await OnUi(() => new
                {
                    app = "Noctis",
                    appVersion = _local!.AppVersion ?? AppVersion(),
                    apiVersion = LocalApiDto.ApiVersion,
                    state = LocalApiDto.StateName(_player.State),
                    playing = _player.State == PlaybackState.Playing,
                    hasTrack = _player.CurrentTrack != null,
                }));

            case "/now-playing":
                return Json(200, await OnUi(() => LocalApiDto.NowPlaying(_player)));

            case "/queue":
            {
                var limit = ParseLimit(query, QueueDefaultLimit, QueueMaxLimit);
                if (limit == null) return Error(400, "bad_request", $"limit must be 1-{QueueMaxLimit}.");
                return Json(200, await OnUi(() =>
                {
                    var total = _player.UpNext.Count;
                    return new
                    {
                        current = _player.CurrentTrack is { } c ? LocalApiDto.Track(c) : null,
                        upNext = _player.UpNext.Take(limit.Value).Select(LocalApiDto.Track).ToList(),
                        upNextTotal = total,
                        truncated = total > limit.Value,
                    };
                }));
            }

            case "/library/search":
                return await SearchAsync(query);

            case "/lyrics/current":
                return Json(200, await OnUi<object>(() =>
                {
                    var track = _player.CurrentTrack;
                    var lyrics = track != null ? _local!.Lyrics?.Snapshot() : null;
                    if (track == null || lyrics == null || lyrics.TrackId != track.Id)
                        return new { trackId = track?.Id, available = false };
                    return new
                    {
                        trackId = track.Id,
                        available = true,
                        synced = lyrics.Synced,
                        wordLevel = lyrics.WordLevel,
                        lines = lyrics.Lines,
                        text = lyrics.PlainText,
                    };
                }));

            case "/playback/play":
                return await ControlAsync(() => { if (_player.State != PlaybackState.Playing) _player.PlayPauseCommand.Execute(null); });
            case "/playback/pause":
                return await ControlAsync(() => { if (_player.State == PlaybackState.Playing) _player.PlayPauseCommand.Execute(null); });
            case "/playback/toggle":
                return await ControlAsync(() => _player.PlayPauseCommand.Execute(null));
            case "/playback/next":
                return await ControlAsync(() => _player.NextCommand.Execute(null));
            case "/playback/previous":
                return await ControlAsync(() => _player.PreviousCommand.Execute(null));

            case "/playback/seek":
            {
                if (!TryGetNumber(body, "positionMs", out var ms) || ms < 0)
                    return Error(400, "bad_request", "Body must be {\"positionMs\": <number ≥ 0>}.");
                var hasTrack = await OnUi(() => _player.CurrentTrack != null);
                if (!hasTrack) return Error(409, "nothing_playing", "There is no current track to seek in.");
                return await ControlAsync(() => _player.SeekTo(TimeSpan.FromMilliseconds(ms)));
            }

            case "/playback/volume":
            {
                if (!TryGetNumber(body, "volume", out var v) || v < 0 || v > 100)
                    return Error(400, "bad_request", "Body must be {\"volume\": <0-100>}.");
                var vol = (int)Math.Round(v);
                return await ControlAsync(() => _player.Volume = vol);
            }

            case "/playback/shuffle":
            {
                bool? enabled = null;
                if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("enabled", out var en))
                {
                    if (en.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Error(400, "bad_request", "\"enabled\" must be true or false (or omit it to toggle).");
                    enabled = en.GetBoolean();
                }
                return await ControlAsync(() =>
                {
                    if (enabled == null || enabled != _player.IsShuffleEnabled)
                        _player.ToggleShuffleCommand.Execute(null);
                });
            }

            case "/playback/repeat":
            {
                RepeatMode? mode = null;
                if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("mode", out var m))
                {
                    mode = m.ValueKind == JsonValueKind.String ? m.GetString()?.ToLowerInvariant() switch
                    {
                        "off" => RepeatMode.Off,
                        "all" => RepeatMode.All,
                        "one" => RepeatMode.One,
                        _ => null,
                    } : null;
                    if (mode == null)
                        return Error(400, "bad_request", "\"mode\" must be \"off\", \"all\" or \"one\" (or omit it to cycle).");
                }
                return await ControlAsync(() =>
                {
                    // Through the command, not the property, so its side effects (AutoMix
                    // cancel, logging) match a click. At most three steps round the cycle.
                    if (mode == null) { _player.CycleRepeatCommand.Execute(null); return; }
                    for (var i = 0; i < 3 && _player.RepeatMode != mode; i++)
                        _player.CycleRepeatCommand.Execute(null);
                });
            }

            case "/queue/add":
                return await QueueAddAsync(body);

            case "/artwork/current":
                return await ArtworkAsync(null);
        }

        if (route.StartsWith("/artwork/", StringComparison.Ordinal))
        {
            var id = route["/artwork/".Length..];
            return Guid.TryParse(id, out var trackId)
                ? await ArtworkAsync(trackId)
                : Error(404, "not_found", "Artwork URLs take a track id.");
        }

        return Error(404, "not_found", "Unknown route.");
    }

    private async Task<ApiResult> ControlAsync(Action action) =>
        Json(200, await OnUi(() =>
        {
            action();
            return new { ok = true, playback = LocalApiDto.Playback(_player) };
        }));

    private static bool TryGetNumber(JsonElement? body, string name, out double value)
    {
        value = 0;
        return body is { ValueKind: JsonValueKind.Object } b
            && b.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.Number
            && p.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static int? ParseLimit(Dictionary<string, string> query, int fallback, int max)
    {
        if (!query.TryGetValue("limit", out var raw) || raw.Length == 0) return fallback;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= max
            ? n
            : null;
    }

    private static string AppVersion()
    {
        var v = UpdateService.CurrentVersion;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ── search ──

    private async Task<ApiResult> SearchAsync(Dictionary<string, string> query)
    {
        query.TryGetValue("q", out var q);
        q = q?.Trim() ?? "";
        if (q.Length == 0 || q.Length > 200)
            return Error(400, "bad_request", "q is required (1-200 characters).");
        var limit = ParseLimit(query, SearchDefaultLimit, SearchMaxLimit);
        if (limit == null) return Error(400, "bad_request", $"limit must be 1-{SearchMaxLimit}.");
        var library = _local!.Library;
        if (library == null) return Json(200, new { query = q, tracks = Array.Empty<object>(), albums = Array.Empty<object>(), artists = Array.Empty<object>() });

        // Copy the list references on the UI thread (cheap), match on the pool.
        var (tracks, albums, artists) = await OnUi(() => (library.Tracks.ToArray(), library.Albums.ToArray(), library.Artists.ToArray()));
        var result = await Task.Run(() => Search(q, limit.Value, tracks, albums, artists));
        return Json(200, result);
    }

    internal static object Search(string q, int limit, IReadOnlyList<Track> tracks, IReadOnlyList<Album> albums, IReadOnlyList<Artist> artists)
    {
        var tokens = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(SearchText.Normalize).Where(t => t.Length > 0).Distinct().ToArray();
        if (tokens.Length == 0)
            return new { query = q, tracks = Array.Empty<object>(), albums = Array.Empty<object>(), artists = Array.Empty<object>() };
        var whole = SearchText.Normalize(q);

        static int Rank(string key, string whole) =>
            key == whole ? 0 : key.StartsWith(whole, StringComparison.Ordinal) ? 1 : key.Contains(whole, StringComparison.Ordinal) ? 2 : 3;

        bool All(params string[] keys) =>
            tokens.All(t => keys.Any(k => k.Contains(t, StringComparison.Ordinal)));

        var trackHits = tracks
            .Where(t => All(t.SearchTitleKey, t.SearchArtistKey, t.SearchAlbumKey))
            .OrderBy(t => Rank(t.SearchTitleKey, whole))
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(LocalApiDto.Track)
            .ToList();

        var albumHits = albums
            .Where(a => All(a.SearchNameKey, a.SearchArtistKey))
            .OrderBy(a => Rank(a.SearchNameKey, whole))
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(LocalApiDto.Album)
            .ToList();

        var artistHits = artists
            .Select(a => (a, key: SearchText.Normalize(a.Name)))
            .Where(x => All(x.key))
            .OrderBy(x => Rank(x.key, whole))
            .ThenBy(x => x.a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => LocalApiDto.Artist(x.a))
            .ToList();

        return new { query = q, limit, tracks = trackHits, albums = albumHits, artists = artistHits };
    }

    // ── queue ──

    private async Task<ApiResult> QueueAddAsync(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b
            || !b.TryGetProperty("trackIds", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            return Error(400, "bad_request", "Body must be {\"trackIds\": [\"<id>\", ...], \"mode\": \"next\"|\"end\"}.");

        var mode = "end";
        if (b.TryGetProperty("mode", out var modeEl))
        {
            mode = modeEl.ValueKind == JsonValueKind.String ? modeEl.GetString()?.ToLowerInvariant() ?? "" : "";
            if (mode is not ("next" or "end"))
                return Error(400, "bad_request", "\"mode\" must be \"next\" or \"end\".");
        }

        if (idsEl.GetArrayLength() is 0 or > QueueAddMaxIds)
            return Error(400, "bad_request", $"trackIds must hold 1-{QueueAddMaxIds} ids.");
        var ids = new List<Guid>();
        foreach (var el in idsEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String || !Guid.TryParse(el.GetString(), out var id))
                return Error(400, "bad_request", "Every trackIds entry must be a track id string.");
            ids.Add(id);
        }

        var library = _local!.Library;
        return Json(200, await OnUi(() =>
        {
            var found = new List<Track>();
            var missing = new List<Guid>();
            foreach (var id in ids)
            {
                if (library?.GetTrackById(id) is { } t) found.Add(t);
                else missing.Add(id);
            }
            if (found.Count > 0)
            {
                if (mode == "next")
                {
                    // AddNext inserts at the front: go backwards so the batch keeps its order.
                    for (var i = found.Count - 1; i >= 0; i--) _player.AddNext(found[i]);
                }
                else
                {
                    _player.AddRangeToQueue(found);
                }
            }
            return new { ok = true, added = found.Count, notFound = missing, upNextCount = _player.UpNext.Count };
        }));
    }

    // ── artwork ──

    private async Task<ApiResult> ArtworkAsync(Guid? trackId)
    {
        // Resolve on the UI thread: the id → track lookup covers the live queue (which can
        // hold files opened from outside the library) before the library index.
        var paths = await OnUi<(string? Own, string? Album)>(() =>
        {
            Track? track;
            if (trackId == null)
                track = _player.CurrentTrack;
            else if (_player.CurrentTrack?.Id == trackId)
                track = _player.CurrentTrack;
            else
                track = _player.UpNext.FirstOrDefault(t => t.Id == trackId)
                        ?? _player.History.FirstOrDefault(t => t.Id == trackId)
                        ?? _local!.Library?.GetTrackById(trackId.Value);
            if (track == null) return (null, null);
            // Same precedence as the player bar: the track's own embedded cover, else the album's.
            return (track.AlbumArtworkPath, _local!.Persistence?.GetArtworkPath(track.AlbumId));
        });

        var image = await Task.Run(() => ReadImage(paths.Own) ?? ReadImage(paths.Album));
        return image is { } img
            ? new ApiResult(200, img.ContentType, img.Bytes)
            : Error(404, "no_artwork", trackId == null ? "The current track has no artwork." : "No artwork for that track.");
    }

    /// <summary>Reads an artwork file the app itself resolved (never a caller-supplied
    /// path), and only when it is a recognised image under the size cap.</summary>
    private static (string ContentType, byte[] Bytes)? ReadImage(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxArtworkBytes) return null;
            var bytes = File.ReadAllBytes(path);
            var type = SniffImageType(bytes);
            return type == null ? null : (type, bytes);
        }
        catch
        {
            return null;
        }
    }

    internal static string? SniffImageType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return "image/png";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return "image/webp";
        if (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return "image/gif";
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return "image/bmp";
        return null;
    }

    // ── auth ──

    private bool IsLocalAuthorized(Dictionary<string, string> headers, Dictionary<string, string> query)
    {
        string? candidate = null;
        if (headers.TryGetValue("Authorization", out var auth) &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            candidate = auth["Bearer ".Length..].Trim();
        else if (query.TryGetValue("token", out var t))
            candidate = t;
        return TokenMatches(candidate, Token);
    }

    /// <summary>Constant-time comparison that also hides the token's length (both sides
    /// are hashed to 32 bytes first).</summary>
    internal static bool TokenMatches(string? candidate, string token)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(token)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private int LockoutSecondsRemaining()
    {
        lock (_authGate)
        {
            var remaining = _lockedUntilTicks - Environment.TickCount64;
            return remaining > 0 ? (int)Math.Ceiling(remaining / 1000.0) : 0;
        }
    }

    /// <summary>Every client is on this PC, so the limit is global rather than per address:
    /// N failures inside the window lock authentication for a while. A 256-bit token
    /// can't be guessed anyway; this keeps a misbehaving script from hammering the log.</summary>
    private void RecordAuthFailure()
    {
        var o = _local!;
        lock (_authGate)
        {
            var now = Environment.TickCount64;
            _authFailures.Enqueue(now);
            while (_authFailures.Count > 0 && now - _authFailures.Peek() > (long)o.AuthFailureWindow.TotalMilliseconds)
                _authFailures.Dequeue();
            if (_authFailures.Count >= o.AuthFailureLimit)
            {
                _lockedUntilTicks = now + (long)o.AuthLockout.TotalMilliseconds;
                _authFailures.Clear();
                DebugLogger.Info(DebugLogger.Category.State, "LocalApi.AuthLockout", $"seconds={o.AuthLockout.TotalSeconds:0}");
            }
        }
    }

    /// <summary>Host header names that address this machine's loopback interface.</summary>
    internal static bool IsLoopbackHost(string host)
    {
        host = host.Trim();
        string name;
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0) return false;
            name = host[1..close];
        }
        else
        {
            var colon = host.IndexOf(':');
            name = colon < 0 ? host : host[..colon];
        }
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(name, out var ip) && IsLoopbackAddress(ip));
    }
}
