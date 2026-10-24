using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;
using Noctis.Services.Lyrics;

namespace Noctis.Services;

/// <summary>
/// Fetches lyrics from lrc.red (Discord request, nutf!xx). Its API page says "The lrc.red API is
/// free for the public to use" (checked 2026-10-17); no key, no account. Two requests: the track
/// lookup <c>/api/v1?track=&amp;artist=&amp;duration=</c> (the BiniLyrics API, which
/// lyrics-api.binimum.org now redirects to) lists recordings with their ISRC and timing level
/// ("word", "line", "none"), then <c>/s/{isrc}.ttml</c> is the lyrics. The TTML is converted by
/// <see cref="OnlineTtml"/>, so duet voices and background vocals survive.
/// Returns results as <see cref="LrcLibResult"/> for compatibility with existing lyrics infrastructure.
/// <para>
/// Songs it has only line-synced or plain can be word-synced by lrc.red's in-house alignment
/// model (<see cref="SyncWithModelAsync"/>, the site's "Sync with the model" button): a POST to
/// <c>/s/{isrc}/sync</c> answering the song.json shape — its <c>source</c> is the new TTML
/// (checked 2026-10-19). The page asks to be credited and says the models are not free to run,
/// so that call is made only on a user's press, once per press.
/// </para>
/// </summary>
public partial class LrcRedLyricsService : ILrcRedLyricsService
{
    private const string LookupUrl = "https://lrc.red/api/v1";
    private const string FileUrl = "https://lrc.red/s/";
    // LRCLIB's: names the app to a free community service. lrc.red's Cloudflare refuses some
    // stock client agents (Python-urllib answered 403 on 2026-10-17).
    private const string UserAgent = "Noctis (https://github.com/heartached/Noctis)";
    private readonly HttpClient _http;
    private readonly HttpClient _modelHttp;

    // Same bounded memo as KugouLyricsService: found lyrics are written to a sidecar, so this
    // only saves repeat lookups within a short window. Cleared when full.
    internal const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();
    private readonly ConcurrentDictionary<string, LrcRedMatch?> _matches = new();

    // Lyrics the model word-synced this session, by ISRC. lrc.red's lookup still said "line" for a
    // song right after its sync (2026-10-19), so a later search of it is answered from here
    // instead of the line-synced file — and a second press costs no second model run.
    private readonly ConcurrentDictionary<string, LrcLibResult> _modelSynced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a model sync may take before it counts as failed. lrc.red says "a few
    /// seconds"; the shared client's 15 s cap would cut a slow one short, so the sync has its own.</summary>
    internal TimeSpan ModelSyncTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Pause before the one retry of a lookup lrc.red answered "busy"; tests shorten it.</summary>
    internal TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    // Lookups by URL, shared by everyone asking the same query while it runs and, once answered,
    // remembered like the other memos. lrc.red's nginx answers an uncached /api/v1 query right
    // behind another with an HTML 503 (curl 10-19: 200 in 1.38 s, then 503 in 0.16 s), so the
    // lyrics page, Search Lyrics and the Studio asking about one song at once must cost one
    // request. A caller that stops waiting (the page's grace, a closed dialog) leaves the request
    // to finish, bounded by the client's timeout: its answer serves the next ask.
    private readonly Dictionary<string, SharedLookup> _lookups = new();

    private sealed class SharedLookup
    {
        /// <summary>Cancelled when every caller has stopped waiting for a busy lookup: its retry is
        /// dropped. (A first request on the wire is let finish.)</summary>
        public readonly CancellationTokenSource Abandoned = new();
        public Task<string> Body = Task.FromResult(string.Empty);
        public int Waiters;
        /// <summary>lrc.red said busy once; the retry is pending.</summary>
        public bool Retrying;
    }

    public LrcRedLyricsService(HttpClient httpClient) : this(httpClient, null)
    {
    }

    /// <param name="httpClient">Lookups and lyrics files.</param>
    /// <param name="modelHttpClient">The model sync, timed by <see cref="ModelSyncTimeout"/> rather
    /// than the client's own timeout (<see cref="CreateModelHttpClient"/>); null = <paramref name="httpClient"/>.</param>
    public LrcRedLyricsService(HttpClient httpClient, HttpClient? modelHttpClient)
    {
        _http = httpClient;
        _modelHttp = modelHttpClient ?? httpClient;
    }

    /// <summary>A client with no timeout of its own, for <see cref="SyncWithModelAsync"/>.</summary>
    public static HttpClient CreateModelHttpClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string CacheKey(string artist, string trackName, double durationSeconds) =>
        $"lrcred:{artist.Length}:{artist}|{trackName}|{Math.Round(durationSeconds)}";

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(artist, trackName, durationSeconds);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var song = await LookupAsync(cacheKey, artist, trackName, durationSeconds, ct);
            if (song == null)
            {
                Remember(_cache, cacheKey, null);
                return null;
            }
            if (_modelSynced.TryGetValue(song.Isrc, out var synced))
                return synced;

            // Missing file: a definitive miss, like an empty lookup.
            var ttml = await GetStringAsync(FileUrl + song.Isrc + ".ttml", ct, notFoundIsNull: true);
            LrcLibResult? result = null;
            if (ttml != null)
            {
                var lyrics = OnlineTtml.Convert(ttml)
                    ?? throw new InvalidDataException("lrc.red sent TTML that does not parse.");
                result = new LrcLibResult
                {
                    TrackName = song.Title,
                    ArtistName = song.Artist,
                    AlbumName = song.Album,
                    Duration = song.DurationSeconds,
                    SyncedLyrics = lyrics.Synced,
                    PlainLyrics = lyrics.Plain,
                    Isrc = song.Isrc,
                };
            }
            Remember(_cache, cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            // Network failure, non-success status, timeout, malformed JSON / TTML.
            // Uncached so a later attempt can succeed.
            throw new LyricsProviderException("lrc.red", ex);
        }
    }

    public async Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        try
        {
            return await LookupAsync(CacheKey(artist, trackName, durationSeconds), artist, trackName, durationSeconds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            throw new LyricsProviderException("lrc.red", ex);
        }
    }

    private static bool IsProviderFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or InvalidOperationException;

    /// <summary>The lookup's pick for the track, remembered under the search's key.</summary>
    private async Task<LrcRedMatch?> LookupAsync(string key, string artist, string trackName, double durationSeconds, CancellationToken ct)
    {
        if (_matches.TryGetValue(key, out var known))
            return known;
        var lookup = await SharedLookupAsync(LookupQuery(artist, trackName, durationSeconds), ct);
        var song = PickSong(lookup, artist, trackName, durationSeconds);
        Remember(_matches, key, song);
        return song;
    }

    /// <summary>The lookup's answer for <paramref name="url"/>: the one already asked for (running
    /// or answered), or a new request. <paramref name="ct"/> only stops this caller waiting.</summary>
    private async Task<string> SharedLookupAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SharedLookup shared;
        lock (_lookups)
        {
            if (!_lookups.TryGetValue(url, out shared!) || shared.Abandoned.IsCancellationRequested)
            {
                if (_lookups.Count >= MaxCacheEntries) _lookups.Clear();
                var started = shared = new SharedLookup();
                // Off the lock: the request's synchronous start runs no caller code under it.
                started.Body = Task.Run(() => FetchLookupAsync(url, started), CancellationToken.None);
                _lookups[url] = started;
                // A failed or abandoned lookup is forgotten, so a later ask tries again.
                _ = started.Body.ContinueWith(t =>
                {
                    _ = t.Exception;
                    lock (_lookups)
                    {
                        if (_lookups.TryGetValue(url, out var current) && ReferenceEquals(current, started))
                            _lookups.Remove(url);
                    }
                }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }
            shared.Waiters++;
        }

        try
        {
            return await shared.Body.WaitAsync(ct);
        }
        finally
        {
            lock (_lookups)
            {
                if (--shared.Waiters == 0 && shared.Retrying && !shared.Body.IsCompleted)
                    shared.Abandoned.Cancel();
            }
        }
    }

    private static bool IsBusy(HttpStatusCode status) =>
        status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// GET the lookup, once more after <see cref="BusyRetryDelay"/> when lrc.red answers "busy"
    /// (503 from its nginx, or 429): the burst that tripped it has passed by then. Still busy, the
    /// status surfaces (<see cref="LyricsProviderException.IsBusy"/>). The model sync never comes
    /// through here: it is one request per press.
    /// </summary>
    private async Task<string> FetchLookupAsync(string url, SharedLookup shared)
    {
        var abandoned = shared.Abandoned.Token;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", UserAgent);
            using var response = await _http.SendAsync(request, attempt == 0 ? CancellationToken.None : abandoned);
            if (attempt == 0 && IsBusy(response.StatusCode))
            {
                lock (_lookups)
                {
                    shared.Retrying = true;
                    if (shared.Waiters == 0) shared.Abandoned.Cancel(); // no retry for nobody
                }
                DebugLogger.Info(DebugLogger.Category.Lyrics, "LrcRed.Busy", $"{(int)response.StatusCode}, retrying once");
                await Task.Delay(BusyRetryDelay, abandoned);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: CancellationToken.None);
        }
    }

    public async Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default)
    {
        // The URL is built from it: only an ISRC goes in.
        if (string.IsNullOrEmpty(isrc) || !IsrcPattern().IsMatch(isrc))
            throw new ArgumentException("Not an ISRC.", nameof(isrc));
        // Synced already this session (the page, then Search Lyrics): no second model run.
        if (_modelSynced.TryGetValue(isrc, out var known))
            return known;

        try
        {
            return await RunModelSyncAsync(isrc, ct);
        }
        catch (LrcRedSyncException ex)
        {
            // The session log ("Copy Logs"): DebugLogger's Lyrics lines never reach it, so a
            // report of the page's "lrc.red: …" error carried no trace of the request.
            DebugLog.Write("Lyrics", $"{ex.Message} | isrc={isrc}{(ex.InnerException is { } inner ? $" | {inner.GetType().Name}: {inner.Message}" : "")}");
            throw;
        }
    }

    private async Task<LrcLibResult> RunModelSyncAsync(string isrc, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ModelSyncTimeout);
        string body;
        try
        {
            // No body, as the site sends it; one request, never retried.
            using var request = new HttpRequestMessage(HttpMethod.Post, FileUrl + isrc + "/sync");
            request.Headers.Add("User-Agent", UserAgent);
            using var response = await _modelHttp.SendAsync(request, timeout.Token);
            body = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new LrcRedSyncException(
                    response.StatusCode == HttpStatusCode.TooManyRequests ? LrcRedSyncFailure.RateLimited : LrcRedSyncFailure.Refused,
                    ErrorText(body), (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Our ModelSyncTimeout, or the client's own timeout.
            throw new LrcRedSyncException(LrcRedSyncFailure.Timeout, inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LrcRedSyncException(LrcRedSyncFailure.Network, inner: ex);
        }

        var result = ParseSong(body, isrc) ?? throw new LrcRedSyncException(LrcRedSyncFailure.BadResponse);
        if (LyricsSearchSelector.FormatRank(result) < LyricsSearchSelector.WordSyncedRank)
            throw new LrcRedSyncException(LrcRedSyncFailure.NotWordSynced);

        if (_modelSynced.Count >= MaxCacheEntries) _modelSynced.Clear();
        _modelSynced[isrc] = result;
        // Searches already answered for this recording now answer with the word-synced lyrics.
        foreach (var entry in _cache)
        {
            if (string.Equals(entry.Value?.Isrc, isrc, StringComparison.OrdinalIgnoreCase))
                _cache[entry.Key] = result;
        }
        return result;
    }

    /// <summary>
    /// A song.json-shaped answer (the sync's) as lyrics: its <c>source</c> TTML through
    /// <see cref="OnlineTtml"/>, so duets, background vocals, translations and romaji survive;
    /// failing that, its <c>lyrics.lines</c>. Null when neither gives lyrics.
    /// </summary>
    internal static LrcLibResult? ParseSong(string json, string isrc)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var hasLines = root.TryGetProperty("lyrics", out var lyricsElement) && lyricsElement.ValueKind == JsonValueKind.Object;
            var lyrics = OnlineTtml.Convert(Str(root, "source"))
                         ?? (hasLines ? LinesToLyrics(lyricsElement) : null);
            if (lyrics is not { } found) return null;
            var duration = Num(root, "duration");
            if (duration <= 0 && hasLines) duration = Num(lyricsElement, "duration");
            return new LrcLibResult
            {
                TrackName = Str(root, "title"),
                ArtistName = Str(root, "artist"),
                AlbumName = Str(root, "album"),
                Duration = duration,
                SyncedLyrics = found.Synced,
                PlainLyrics = found.Plain,
                Isrc = isrc,
            };
        }
    }

    /// <summary>
    /// song.json's <c>lyrics.lines</c> (each line's text, begin/end in seconds, and its words as
    /// arrays of syllables) as (ELRC, plain). A word keeps the space its line text has after it.
    /// </summary>
    private static (string? Synced, string Plain)? LinesToLyrics(JsonElement lyrics)
    {
        if (!lyrics.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return null;
        var parsed = new List<LyricLine>();
        foreach (var line in lines.EnumerateArray())
        {
            if (parsed.Count >= EnhancedLrcParser.MaxLyricLines) break;
            var text = Str(line, "text")?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var timed = !(line.TryGetProperty("timed", out var t) && t.ValueKind == JsonValueKind.False)
                        && line.TryGetProperty("begin", out var b) && b.ValueKind == JsonValueKind.Number;
            var row = new LyricLine { Text = text, Timestamp = timed ? Seconds(Num(line, "begin")) : null };
            var words = new List<WordTiming>();
            var cursor = 0;
            if (timed && line.TryGetProperty("words", out var ws) && ws.ValueKind == JsonValueKind.Array)
            {
                foreach (var word in ws.EnumerateArray())
                {
                    var syllables = word.ValueKind == JsonValueKind.Array ? word.EnumerateArray().ToList() : new List<JsonElement> { word };
                    var wordText = string.Concat(syllables.Select(s => Str(s, "text") ?? string.Empty)).Trim();
                    if (wordText.Length == 0 || syllables.Count == 0) continue;
                    var at = text.IndexOf(wordText, cursor, StringComparison.Ordinal);
                    if (at >= 0) cursor = at + wordText.Length;
                    var spaced = at >= 0 && cursor < text.Length && char.IsWhiteSpace(text[cursor]);
                    words.Add(new WordTiming
                    {
                        Text = spaced ? wordText + " " : wordText,
                        Start = Seconds(Num(syllables[0], "begin")),
                        End = Seconds(Num(syllables[^1], "end")),
                    });
                }
            }
            if (words.Count is > 0 and <= EnhancedLrcParser.MaxWordsPerLine)
            {
                row.EndTimestamp = words[^1].End;
                row.Words = words;
            }
            parsed.Add(row);
        }
        if (parsed.Count == 0) return null;
        var plain = string.Join('\n', parsed.Select(l => l.Text));
        return (parsed.Any(l => l.Timestamp.HasValue) ? TtmlParser.ToLrc(parsed) : null, plain);

        static TimeSpan Seconds(double s) => TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, s) * 1000));
    }

    /// <summary>The "error" text of a refusal ({"error":"No lyrics with that ISRC."}) as one short line; null without one.</summary>
    internal static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = Str(doc.RootElement, "error");
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= 200 ? text : text[..200].TrimEnd() + "…";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The lookup URL: by track and artist (and duration when known), or a title search
    /// when the artist is the library's placeholder — the lookup needs both names.</summary>
    internal static string LookupQuery(string artist, string trackName, double durationSeconds)
    {
        if (LyricsSearchSelector.IsUnknownArtist(artist))
            return $"{LookupUrl}?q={Uri.EscapeDataString(trackName.Trim())}";
        var url = $"{LookupUrl}?track={Uri.EscapeDataString(trackName.Trim())}&artist={Uri.EscapeDataString(artist.Trim())}";
        if (durationSeconds > 0)
            url += "&duration=" + Math.Round(durationSeconds).ToString(CultureInfo.InvariantCulture);
        return url;
    }

    private static void Remember<T>(ConcurrentDictionary<string, T> cache, string key, T value)
    {
        if (cache.Count >= MaxCacheEntries)
            cache.Clear();
        cache[key] = value;
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken ct, bool notFoundIsNull = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, ct);
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    /// <summary>
    /// The lookup result that is the local track, or null. Every result must match by title,
    /// artist and duration (±5 s); among those an exact title beats a "(Live)" / "(Acoustic)"
    /// cut that merely contains it, then the richer timing, then the closer duration, then the
    /// lookup's own order.
    /// </summary>
    internal static LrcRedMatch? PickSong(string json, string artist, string trackName, double durationSeconds)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        LrcRedMatch? best = null;
        (int Exact, int Timing, double Delta) bestKey = default;
        foreach (var r in results.EnumerateArray())
        {
            var isrc = Str(r, "isrc") ?? Str(r, "id");
            if (isrc == null || !IsrcPattern().IsMatch(isrc)) continue;
            var candidate = new LrcLibResult
            {
                TrackName = Str(r, "track_name"),
                ArtistName = Str(r, "artist_name"),
                Duration = Num(r, "duration"),
            };
            if (!LyricsSearchSelector.MatchesTrack(candidate, artist, trackName, durationSeconds)) continue;

            var timing = Str(r, "timing_type") is "word" or "line" ? Str(r, "timing_type")! : "none";
            var key = (
                Exact: string.Equals(candidate.TrackName!.Trim(), trackName.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                Timing: timing switch { "word" => 2, "line" => 1, _ => 0 },
                Delta: durationSeconds > 0 && candidate.Duration > 0 ? Math.Abs(candidate.Duration - durationSeconds) : 0);
            if (best != null && !IsBetter(key, bestKey)) continue;
            best = new LrcRedMatch(isrc, candidate.TrackName!, candidate.ArtistName!, Str(r, "album_name"), candidate.Duration, timing);
            bestKey = key;
        }
        return best;
    }

    private static bool IsBetter((int Exact, int Timing, double Delta) a, (int Exact, int Timing, double Delta) b) =>
        a.Exact != b.Exact ? a.Exact > b.Exact
        : a.Timing != b.Timing ? a.Timing > b.Timing
        : a.Delta < b.Delta;

    // An ISRC is 12 letters and digits; the file URL is built from it, never taken from the response.
    [GeneratedRegex("^[A-Za-z0-9]{12}$")]
    private static partial Regex IsrcPattern();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
}
