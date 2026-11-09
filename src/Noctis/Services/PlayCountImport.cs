using System.Globalization;
using System.Net;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Services;

// GitHub #160: one-way import of play counts from Last.fm / ListenBrainz.
//
// Source: the user's full listening history, one scrobble/listen at a time
// (Last.fm user.getRecentTracks, ListenBrainz /1/user/{name}/listens), not the
// pre-aggregated top-tracks/stats endpoints, because only the per-listen history carries
// the album each play was on: the same song on several releases gets each release's own
// count. Read-only GETs; nothing is ever sent back, and nothing goes through the player,
// so no scrobble, no play-history event (Stats, Wrapped, Home "recently played" stay
// as they were): the result is written only to Track.PlayCount as a count adjustment.

/// <summary>Where play counts are imported from.</summary>
public enum PlayCountImportSource { LastFm, ListenBrainz }

/// <summary>Fetch progress: listens read so far, and the total when the service says (0 = unknown).</summary>
public readonly record struct PlayCountFetchProgress(int Listens, int Total);

/// <summary>Why an import stopped.</summary>
public enum PlayCountImportError { UserNotFound, PrivateHistory, Unreachable, BadReply }

/// <summary>A failure the user can act on (unknown user, private history, service down).</summary>
public sealed class PlayCountImportException(PlayCountImportError kind, string? detail = null, Exception? inner = null)
    : Exception(detail ?? kind.ToString(), inner)
{
    public PlayCountImportError Kind { get; } = kind;
}

/// <summary>
/// Reads a user's listening history and totals it per artist/title/album. Polite: one request
/// at a time with a pause between pages, ListenBrainz's rate-limit headers honoured, and
/// transient failures (network, 429, 5xx, Last.fm "try again" codes) retried with back-off.
/// </summary>
public sealed class PlayCountHistoryFetcher
{
    internal const int LastFmPageSize = 200;      // user.getRecentTracks maximum
    internal const int ListenBrainzPageSize = 1000; // MAX_ITEMS_PER_GET
    internal const int MaxAttempts = 4;
    internal static readonly TimeSpan PagePause = TimeSpan.FromMilliseconds(250); // ≤ 4 req/s (Last.fm asks ≤ 5)

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    public PlayCountHistoryFetcher(HttpClient http,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<List<SourcePlayCount>> FetchAsync(PlayCountImportSource source, string user,
        string? listenBrainzApiUrl, string? listenBrainzToken,
        IProgress<PlayCountFetchProgress>? progress, CancellationToken ct)
        => source == PlayCountImportSource.LastFm
            ? FetchLastFmAsync(user, progress, ct)
            : FetchListenBrainzAsync(listenBrainzApiUrl ?? ListenBrainzService.DefaultApiUrl, user, listenBrainzToken, progress, ct);

    // ── Last.fm ──

    /// <summary>
    /// Pages user.getRecentTracks (200 a page, newest first). The window is pinned with
    /// to=&lt;start time&gt; so a scrobble arriving mid-import can't shift the pages (which would
    /// count one listen twice). The "now playing" entry has no date and is not a scrobble.
    /// </summary>
    public async Task<List<SourcePlayCount>> FetchLastFmAsync(string user,
        IProgress<PlayCountFetchProgress>? progress, CancellationToken ct)
    {
        var counts = new Counter();
        await ReadLastFmAsync(user, null, _now().ToUnixTimeSeconds(),
            (_, artist, title, album) => counts.Add(artist, title, album), progress, ct);
        return counts.ToList();
    }

    /// <summary>
    /// Every listen in [<paramref name="fromTs"/>, <paramref name="toTs"/>) (unix seconds; no
    /// <paramref name="fromTs"/> = the whole history), newest first, handed to
    /// <paramref name="onListen"/> as (time, artist, title, album); it returns whether the listen
    /// counted (blank artist/title don't). Used by the incremental sync (<see cref="PlayCountSync"/>).
    /// </summary>
    internal Task ReadListensAsync(PlayCountImportSource source, string user, string? listenBrainzApiUrl,
        string? listenBrainzToken, long? fromTs, long toTs, Func<long, string, string, string, bool> onListen,
        IProgress<PlayCountFetchProgress>? progress, CancellationToken ct)
        => source == PlayCountImportSource.LastFm
            ? ReadLastFmAsync(user, fromTs, toTs, onListen, progress, ct)
            : ReadListenBrainzAsync(listenBrainzApiUrl ?? ListenBrainzService.DefaultApiUrl, user, listenBrainzToken,
                fromTs, toTs, onListen, progress, ct);

    /// <summary>The current time as the services count it (unix seconds).</summary>
    internal long NowUnix() => _now().ToUnixTimeSeconds();

    // Last.fm's from is inclusive and to exclusive (checked against the live API: a scrobble at
    // t is returned for from=t&to=t+1 and not for from=t-1&to=t).
    private async Task ReadLastFmAsync(string user, long? fromTs, long toTs,
        Func<long, string, string, string, bool> onListen, IProgress<PlayCountFetchProgress>? progress, CancellationToken ct)
    {
        var listens = 0;
        var totalPages = 1;
        for (var page = 1; page <= totalPages; page++)
        {
            if (page > 1) await _delay(PagePause, ct);
            var url = $"{LastFmApi.ApiBase}?method=user.getrecenttracks&user={Uri.EscapeDataString(user)}" +
                      $"&limit={LastFmPageSize}&page={page}" +
                      (fromTs is { } f ? $"&from={f.ToString(CultureInfo.InvariantCulture)}" : "") +
                      $"&to={toTs.ToString(CultureInfo.InvariantCulture)}&api_key={LastFmApi.ApiKey}&format=json";
            using var doc = await GetJsonAsync(url, null, isLastFm: true, ct);
            if (!doc.RootElement.TryGetProperty("recenttracks", out var recent))
                throw new PlayCountImportException(PlayCountImportError.BadReply, "no recenttracks");

            var total = 0;
            if (recent.TryGetProperty("@attr", out var attr))
            {
                totalPages = Math.Max(1, ReadInt(attr, "totalPages"));
                total = ReadInt(attr, "total");
            }

            if (recent.TryGetProperty("track", out var tracks))
            {
                // Last.fm's JSON turns a one-item list into a bare object.
                IEnumerable<JsonElement> items = tracks.ValueKind == JsonValueKind.Array
                    ? tracks.EnumerateArray()
                    : tracks.ValueKind == JsonValueKind.Object ? new[] { tracks } : Array.Empty<JsonElement>();
                foreach (var t in items)
                {
                    if (!t.TryGetProperty("date", out var date)) continue; // now playing
                    var ts = date.ValueKind == JsonValueKind.Object ? ReadLong(date, "uts") : 0;
                    if (fromTs is { } from && ts > 0 && ts < from) continue;
                    if (ts >= toTs) continue;
                    var artist = ReadText(t, "artist");
                    var title = ReadString(t, "name");
                    var album = ReadText(t, "album");
                    if (onListen(ts, artist, title, album)) listens++;
                }
            }
            progress?.Report(new PlayCountFetchProgress(listens, total));
        }
    }

    // ── ListenBrainz ──

    /// <summary>
    /// Pages /1/user/{name}/listens (1000 a call, newest first) backwards with max_ts. max_ts
    /// is exclusive and several listens can share a second, so the next page asks for
    /// max_ts = oldest + 1 and drops the listens at that second it already has. The total for
    /// the progress bar comes from /listen-count (optional; missing on some self-hosted servers).
    /// </summary>
    public async Task<List<SourcePlayCount>> FetchListenBrainzAsync(string apiUrl, string user, string? token,
        IProgress<PlayCountFetchProgress>? progress, CancellationToken ct)
    {
        var counts = new Counter();
        await ReadListenBrainzAsync(apiUrl, user, token, null, null,
            (_, artist, title, album) => counts.Add(artist, title, album), progress, ct);
        return counts.ToList();
    }

    /// <summary>The paging above; with <paramref name="fromTs"/> it stops at the first page that
    /// reaches back past it (the API's min_ts can't be combined with paging backwards), and
    /// listens at or after <paramref name="toTs"/> are left out (the first page asks for
    /// max_ts = toTs).</summary>
    private async Task ReadListenBrainzAsync(string apiUrl, string user, string? token, long? fromTs, long? toTs,
        Func<long, string, string, string, bool> onListen, IProgress<PlayCountFetchProgress>? progress, CancellationToken ct)
    {
        var root = (ListenBrainzService.NormalizeApiUrl(apiUrl) ?? ListenBrainzService.DefaultApiUrl).TrimEnd('/');
        var userPath = Uri.EscapeDataString(user);

        var total = 0;
        try
        {
            using var countDoc = await GetJsonAsync($"{root}/1/user/{userPath}/listen-count", token, isLastFm: false, ct);
            if (countDoc.RootElement.TryGetProperty("payload", out var p)) total = ReadInt(p, "count");
        }
        catch (PlayCountImportException ex) when (ex.Kind != PlayCountImportError.UserNotFound)
        {
            // Progress without a total.
        }

        var listens = 0;
        long? maxTs = toTs is { } pin ? pin - 1 : null; // each request asks for max_ts = maxTs + 1 (exclusive)
        var boundary = new HashSet<string>(StringComparer.Ordinal); // listens already read at the boundary second
        while (true)
        {
            if (maxTs != null) await _delay(PagePause, ct);
            var url = $"{root}/1/user/{userPath}/listens?count={ListenBrainzPageSize}" +
                      (maxTs is { } m ? $"&max_ts={m + 1}" : "");
            using (var doc = await GetJsonAsync(url, token, isLastFm: false, ct))
            {
                if (!doc.RootElement.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("listens", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new PlayCountImportException(PlayCountImportError.BadReply, "no payload.listens");

                long? oldest = null;
                var fresh = 0;
                var atOldest = new List<string>();
                foreach (var l in items.EnumerateArray())
                {
                    var ts = l.TryGetProperty("listened_at", out var tsNode) && tsNode.ValueKind == JsonValueKind.Number
                        ? tsNode.GetInt64() : 0;
                    var meta = l.TryGetProperty("track_metadata", out var md) ? md : default;
                    var artist = meta.ValueKind == JsonValueKind.Object ? ReadString(meta, "artist_name") : "";
                    var title = meta.ValueKind == JsonValueKind.Object ? ReadString(meta, "track_name") : "";
                    var album = meta.ValueKind == JsonValueKind.Object ? ReadString(meta, "release_name") : "";

                    var key = ts.ToString(CultureInfo.InvariantCulture) + "\u001f" + artist + "\u001f" + title + "\u001f" + album;
                    if (maxTs == ts && boundary.Contains(key)) continue;

                    fresh++;
                    if ((fromTs is not { } from || ts >= from) && (toTs is not { } to || ts < to) &&
                        onListen(ts, artist, title, album))
                        listens++;
                    if (oldest == null || ts < oldest)
                    {
                        oldest = ts;
                        atOldest.Clear();
                    }
                    if (ts == oldest) atOldest.Add(key);
                }

                progress?.Report(new PlayCountFetchProgress(listens, Math.Max(total, listens)));
                // No new listen on this page = the end (also stops a page made only of
                // already-read listens sharing one second from looping forever).
                if (fresh == 0 || oldest == null) break;
                if (fromTs is { } stop && oldest < stop) break; // reached back past the window
                if (oldest == maxTs) boundary.UnionWith(atOldest);
                else
                {
                    boundary.Clear();
                    boundary.UnionWith(atOldest);
                }
                maxTs = oldest;
            }
        }
    }

    // ── HTTP ──

    private async Task<JsonDocument> GetJsonAsync(string url, string? lbToken, bool isLastFm, CancellationToken ct)
    {
        Exception? last = null;
        TimeSpan? wait = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Back-off before a retry: the server's Retry-After when it gave one, else 2, 4, 8 s.
            if (attempt > 1)
                await _delay(wait ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            wait = null;
            ct.ThrowIfCancellationRequested();

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(lbToken))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", lbToken);
                using var resp = await _http.SendAsync(req, ct);
                var body = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);

                if (!isLastFm && resp.StatusCode == HttpStatusCode.NotFound)
                    throw new PlayCountImportException(PlayCountImportError.UserNotFound);

                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(body); } catch (JsonException) { }

                if (isLastFm && doc != null && doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var errNode))
                {
                    var code = errNode.ValueKind == JsonValueKind.Number ? errNode.GetInt32() : 0;
                    doc.Dispose();
                    switch (code)
                    {
                        case 6: throw new PlayCountImportException(PlayCountImportError.UserNotFound);
                        case 17: throw new PlayCountImportException(PlayCountImportError.PrivateHistory);
                        case 8 or 11 or 16 or 29: // operation failed / offline / temporary / rate limit
                            last = new HttpRequestException($"Last.fm error {code}");
                            continue;
                        default: throw new PlayCountImportException(PlayCountImportError.BadReply, $"Last.fm error {code}");
                    }
                }

                if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
                {
                    doc?.Dispose();
                    last = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                    wait = RetryAfter(resp);
                    continue;
                }
                if (!resp.IsSuccessStatusCode || doc == null)
                {
                    doc?.Dispose();
                    throw new PlayCountImportException(
                        (int)resp.StatusCode is 401 or 403 ? PlayCountImportError.PrivateHistory : PlayCountImportError.BadReply,
                        $"HTTP {(int)resp.StatusCode}");
                }

                // ListenBrainz: wait out the window before the quota runs dry, not after a 429.
                if (!isLastFm && ReadHeaderInt(resp, "X-RateLimit-Remaining") is <= 1 &&
                    ReadHeaderInt(resp, "X-RateLimit-Reset-In") is { } resetIn and > 0)
                    await _delay(TimeSpan.FromSeconds(Math.Min(resetIn, 60)), ct);

                return doc;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (PlayCountImportException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                last = ex; // network trouble or the HttpClient timeout: retry
            }
        }
        throw new PlayCountImportException(PlayCountImportError.Unreachable, last?.Message, last);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } d) return d < TimeSpan.FromSeconds(60) ? d : TimeSpan.FromSeconds(60);
        return ReadHeaderInt(resp, "X-RateLimit-Reset-In") is { } s and > 0
            ? TimeSpan.FromSeconds(Math.Min(s, 60)) : null;
    }

    private static int? ReadHeaderInt(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var values) &&
           int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int ReadInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var n)) return 0;
        if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var i)) return i;
        return n.ValueKind == JsonValueKind.String &&
               int.TryParse(n.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    private static long ReadLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var n)) return 0;
        if (n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var i)) return i;
        return n.ValueKind == JsonValueKind.String &&
               long.TryParse(n.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    private static string ReadString(JsonElement e, string name)
        => e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";

    /// <summary>Last.fm's {"#text": "...", "mbid": "..."} objects (or a bare string).</summary>
    private static string ReadText(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var n)) return "";
        if (n.ValueKind == JsonValueKind.String) return n.GetString() ?? "";
        if (n.ValueKind == JsonValueKind.Object)
            return ReadString(n, "#text") is { Length: > 0 } text ? text : ReadString(n, "name");
        return "";
    }

    /// <summary>Totals listens per exact artist/title/album (case-insensitive).</summary>
    private sealed class Counter
    {
        private readonly Dictionary<(string, string, string), int> _counts = new(new KeyComparer());

        public bool Add(string artist, string title, string album)
        {
            artist = artist.Trim();
            title = title.Trim();
            if (artist.Length == 0 || title.Length == 0) return false;
            var key = (artist, title, album.Trim());
            _counts[key] = _counts.TryGetValue(key, out var c) ? c + 1 : 1;
            return true;
        }

        public List<SourcePlayCount> ToList()
            => _counts.Select(kv => new SourcePlayCount(kv.Key.Item1, kv.Key.Item2,
                kv.Key.Item3.Length == 0 ? null : kv.Key.Item3, kv.Value)).ToList();

        private sealed class KeyComparer : IEqualityComparer<(string, string, string)>
        {
            public bool Equals((string, string, string) x, (string, string, string) y)
                => string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Item3, y.Item3, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string, string, string) k)
                => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item3));
        }
    }
}
