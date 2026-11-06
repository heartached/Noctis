using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctis.Services;

// GitHub #177: the connected Last.fm account's taste read back into Noctis. Read-only public
// GETs (user.getTopTracks / getLovedTracks / getTopArtists / getTopAlbums, artist.getSimilar):
// they need the account NAME and the app's API key, never the session key, so nothing here
// can write to the account. The result is cached on disk (cache/lastfm_taste.json) with a TTL;
// the mixes are built from it against the local library (LastFmTasteMixes), and Discover lists
// artists/albums the library lacks, linking only to their Last.fm pages.

/// <summary>Last.fm's chart periods (the API's own "period" values).</summary>
public static class LastFmPeriod
{
    public const string Overall = "overall";
    public const string SevenDays = "7day";
    public const string OneMonth = "1month";
    public const string ThreeMonths = "3month";
    public const string SixMonths = "6month";
    public const string TwelveMonths = "12month";

    /// <summary>The default: long enough to be the user's taste, recent enough to follow it.</summary>
    public const string Default = SixMonths;

    public static bool IsValid(string? period) => period is Overall or SevenDays or OneMonth or ThreeMonths or SixMonths or TwelveMonths;
}

/// <summary>A song from a Last.fm chart or the loved list.</summary>
public sealed record LastFmTasteTrack(string Artist, string Title, int PlayCount, string? Url);

/// <summary>An artist from the user's chart.</summary>
public sealed record LastFmTasteArtist(string Name, int PlayCount, string? Url);

/// <summary>An album from the user's chart.</summary>
public sealed record LastFmTasteAlbum(string Artist, string Name, int PlayCount, string? Url);

/// <summary>An artist Last.fm calls similar to <see cref="SimilarTo"/> (match 0..1).</summary>
public sealed record LastFmSimilarArtist(string Name, double Match, string? Url, string SimilarTo);

/// <summary>Everything one refresh read for one account; what the cache file holds.</summary>
public sealed class LastFmTasteSnapshot
{
    /// <summary>Cache format; a file of another version is ignored (fetched again).</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string User { get; set; } = "";
    public string Period { get; set; } = LastFmPeriod.Default;
    public DateTimeOffset FetchedUtc { get; set; }
    public List<LastFmTasteTrack> TopTracks { get; set; } = new();
    public List<LastFmTasteTrack> LovedTracks { get; set; } = new();
    public List<LastFmTasteArtist> TopArtists { get; set; } = new();
    public List<LastFmTasteAlbum> TopAlbums { get; set; } = new();
    public List<LastFmSimilarArtist> SimilarArtists { get; set; } = new();
}

/// <summary>Why a refresh failed.</summary>
public enum LastFmTasteError
{
    /// <summary>Last.fm doesn't know the account name (error 6).</summary>
    UserNotFound,
    /// <summary>The account hides its listening data (error 17).</summary>
    Private,
    /// <summary>Offline, timed out, or Last.fm down / rate limiting after the retries.</summary>
    Unreachable,
    /// <summary>An answer that wasn't the expected JSON.</summary>
    BadReply,
}

public sealed class LastFmTasteException(LastFmTasteError kind, string? detail = null, Exception? inner = null)
    : Exception(detail ?? kind.ToString(), inner)
{
    public LastFmTasteError Kind { get; } = kind;
}

/// <summary>
/// The Last.fm reads. Polite like the play count import: one request at a time with a pause
/// between them, transient failures (network, 429, 5xx, Last.fm codes 8/11/16/29) retried with
/// back-off. Request URLs carry the API key, so they are never logged or put in exceptions.
/// </summary>
public sealed class LastFmTasteClient
{
    internal const int TopTracksLimit = 100;
    internal const int LovedLimit = 100;
    internal const int TopArtistsLimit = 30;
    internal const int TopAlbumsLimit = 30;
    /// <summary>How many of the top artists seed artist.getSimilar.</summary>
    internal const int SimilarSeeds = 5;
    internal const int SimilarLimit = 20;
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan RequestPause = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _apiBase;
    private bool _first = true;

    public LastFmTasteClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null, string? apiBase = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
        _apiBase = apiBase ?? LastFmApi.ApiBase;
    }

    /// <summary>
    /// Reads the account's charts, loved songs and similar artists. The four account reads must
    /// succeed (their failure is the answer: unknown user, private, offline); a similar-artist
    /// read that fails only leaves that seed out.
    /// </summary>
    public async Task<LastFmTasteSnapshot> FetchSnapshotAsync(string user, string period, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new LastFmTasteException(LastFmTasteError.UserNotFound);
        if (!LastFmPeriod.IsValid(period)) period = LastFmPeriod.Default;
        var u = Uri.EscapeDataString(user.Trim());

        var snapshot = new LastFmTasteSnapshot { User = user.Trim(), Period = period, FetchedUtc = now };

        using (var doc = await GetAsync($"method=user.gettoptracks&user={u}&period={period}&limit={TopTracksLimit}", ct))
            snapshot.TopTracks = ParseTracks(doc.RootElement, "toptracks");
        using (var doc = await GetAsync($"method=user.getlovedtracks&user={u}&limit={LovedLimit}", ct))
            snapshot.LovedTracks = ParseTracks(doc.RootElement, "lovedtracks");
        using (var doc = await GetAsync($"method=user.gettopartists&user={u}&period={period}&limit={TopArtistsLimit}", ct))
            snapshot.TopArtists = ParseArtists(doc.RootElement);
        using (var doc = await GetAsync($"method=user.gettopalbums&user={u}&period={period}&limit={TopAlbumsLimit}", ct))
            snapshot.TopAlbums = ParseAlbums(doc.RootElement);

        foreach (var seed in snapshot.TopArtists.Take(SimilarSeeds))
        {
            try
            {
                using var doc = await GetAsync(
                    $"method=artist.getsimilar&artist={Uri.EscapeDataString(seed.Name)}&autocorrect=1&limit={SimilarLimit}", ct);
                snapshot.SimilarArtists.AddRange(ParseSimilar(doc.RootElement, seed.Name));
            }
            catch (LastFmTasteException ex)
            {
                Debug.WriteLine($"[LastFmTaste] similar artists for one seed skipped: {ex.Kind}");
            }
        }
        return snapshot;
    }

    // ── HTTP ──

    private async Task<JsonDocument> GetAsync(string query, CancellationToken ct)
    {
        if (!_first) await _delay(RequestPause, ct);
        _first = false;

        var url = $"{_apiBase}?{query}&api_key={LastFmApi.ApiKey}&format=json";
        string? lastFailure = null;
        TimeSpan? wait = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) await _delay(wait ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            wait = null;
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                var body = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(body); } catch (JsonException) { }

                // Last.fm reports its own errors as {"error": n, "message": ...}, with HTTP 200
                // or a 4xx; the code decides, not the status.
                if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var err))
                {
                    var code = err.ValueKind == JsonValueKind.Number && err.TryGetInt32(out var c) ? c : 0;
                    doc.Dispose();
                    switch (code)
                    {
                        case 6: throw new LastFmTasteException(LastFmTasteError.UserNotFound);
                        case 17: throw new LastFmTasteException(LastFmTasteError.Private);
                        case 8 or 11 or 16 or 29:
                            lastFailure = $"Last.fm error {code}";
                            continue;
                        default: throw new LastFmTasteException(LastFmTasteError.BadReply, $"Last.fm error {code}");
                    }
                }

                if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
                {
                    doc?.Dispose();
                    lastFailure = $"HTTP {(int)resp.StatusCode}";
                    if (resp.Headers.RetryAfter?.Delta is { } d) wait = d < TimeSpan.FromSeconds(30) ? d : TimeSpan.FromSeconds(30);
                    continue;
                }
                if (!resp.IsSuccessStatusCode || doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    doc?.Dispose();
                    throw new LastFmTasteException(LastFmTasteError.BadReply, $"HTTP {(int)resp.StatusCode}");
                }
                return doc;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (LastFmTasteException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // Only the type: an HttpRequestException's message can quote the request URL.
                lastFailure = ex.GetType().Name;
            }
        }
        throw new LastFmTasteException(LastFmTasteError.Unreachable, lastFailure);
    }

    // ── Parsing (internal for tests) ──

    /// <summary>toptracks.track[] / lovedtracks.track[]: name, playcount, artist {name}, url.</summary>
    internal static List<LastFmTasteTrack> ParseTracks(JsonElement root, string container)
    {
        var list = new List<LastFmTasteTrack>();
        if (!root.TryGetProperty(container, out var box)) throw new LastFmTasteException(LastFmTasteError.BadReply, "no " + container);
        foreach (var t in Items(box, "track"))
        {
            var title = ReadString(t, "name").Trim();
            var artist = ReadNamed(t, "artist").Trim();
            if (title.Length == 0 || artist.Length == 0) continue;
            list.Add(new LastFmTasteTrack(artist, title, ReadInt(t, "playcount"), NullIfEmpty(ReadString(t, "url"))));
        }
        return list;
    }

    internal static List<LastFmTasteArtist> ParseArtists(JsonElement root)
    {
        var list = new List<LastFmTasteArtist>();
        if (!root.TryGetProperty("topartists", out var box)) throw new LastFmTasteException(LastFmTasteError.BadReply, "no topartists");
        foreach (var a in Items(box, "artist"))
        {
            var name = ReadString(a, "name").Trim();
            if (name.Length == 0) continue;
            list.Add(new LastFmTasteArtist(name, ReadInt(a, "playcount"), NullIfEmpty(ReadString(a, "url"))));
        }
        return list;
    }

    internal static List<LastFmTasteAlbum> ParseAlbums(JsonElement root)
    {
        var list = new List<LastFmTasteAlbum>();
        if (!root.TryGetProperty("topalbums", out var box)) throw new LastFmTasteException(LastFmTasteError.BadReply, "no topalbums");
        foreach (var a in Items(box, "album"))
        {
            var name = ReadString(a, "name").Trim();
            var artist = ReadNamed(a, "artist").Trim();
            if (name.Length == 0 || artist.Length == 0) continue;
            list.Add(new LastFmTasteAlbum(artist, name, ReadInt(a, "playcount"), NullIfEmpty(ReadString(a, "url"))));
        }
        return list;
    }

    internal static List<LastFmSimilarArtist> ParseSimilar(JsonElement root, string seed)
    {
        var list = new List<LastFmSimilarArtist>();
        if (!root.TryGetProperty("similarartists", out var box)) throw new LastFmTasteException(LastFmTasteError.BadReply, "no similarartists");
        foreach (var a in Items(box, "artist"))
        {
            var name = ReadString(a, "name").Trim();
            if (name.Length == 0) continue;
            list.Add(new LastFmSimilarArtist(name, ReadDouble(a, "match"), NullIfEmpty(ReadString(a, "url")), seed));
        }
        return list;
    }

    /// <summary>Last.fm's JSON turns a one-item list into a bare object, and an empty one into "".</summary>
    private static IEnumerable<JsonElement> Items(JsonElement box, string name)
    {
        if (box.ValueKind != JsonValueKind.Object || !box.TryGetProperty(name, out var items)) return Array.Empty<JsonElement>();
        return items.ValueKind switch
        {
            JsonValueKind.Array => items.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList(),
            JsonValueKind.Object => new[] { items },
            _ => Array.Empty<JsonElement>(),
        };
    }

    private static string ReadString(JsonElement e, string name)
        => e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";

    /// <summary>{"name": ...} / {"#text": ...} objects, or a bare string.</summary>
    private static string ReadNamed(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var n)) return "";
        if (n.ValueKind == JsonValueKind.String) return n.GetString() ?? "";
        if (n.ValueKind != JsonValueKind.Object) return "";
        return ReadString(n, "name") is { Length: > 0 } s ? s : ReadString(n, "#text");
    }

    private static int ReadInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var n)) return 0;
        if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var i)) return i;
        return n.ValueKind == JsonValueKind.String &&
               int.TryParse(n.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    private static double ReadDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var n)) return 0;
        if (n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d)) return d;
        return n.ValueKind == JsonValueKind.String &&
               double.TryParse(n.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>What <see cref="LastFmTasteService.GetAsync"/> found: the snapshot to show (fresh,
/// cached, or stale after a failed refresh; null when there is none for this account) and the
/// refresh error, if the last attempt failed.</summary>
public sealed record LastFmTasteResult(LastFmTasteSnapshot? Snapshot, LastFmTasteError? Error, bool FromCache);

/// <summary>
/// The snapshot for one account, cached in memory and in a JSON file. A cached snapshot younger
/// than the TTL (12 h by default) is served without a request; a refresh that fails keeps the
/// old one on screen and is not retried for <see cref="FailureCooldown"/> unless forced (the
/// Refresh button), so an offline Home visit costs nothing. Thread-safe; one fetch at a time.
/// </summary>
public sealed class LastFmTasteService
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(12);
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private readonly string _cachePath;
    private readonly Func<string, string, DateTimeOffset, CancellationToken, Task<LastFmTasteSnapshot>> _fetch;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private LastFmTasteSnapshot? _snapshot;
    private bool _loaded;
    private (string User, DateTimeOffset At, LastFmTasteError Error)? _lastFailure;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <param name="http">Client for the GETs; the caller owns its timeout.</param>
    /// <param name="cachePath">The JSON cache (the desktop's is cache/lastfm_taste.json under the data root).</param>
    public LastFmTasteService(HttpClient http, string cachePath, Func<DateTimeOffset>? now = null, TimeSpan? ttl = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : this(cachePath, (user, period, at, ct) => new LastFmTasteClient(http, delay).FetchSnapshotAsync(user, period, at, ct), now, ttl)
    {
    }

    /// <summary>For tests: any fetch function in place of the HTTP client.</summary>
    internal LastFmTasteService(string cachePath,
        Func<string, string, DateTimeOffset, CancellationToken, Task<LastFmTasteSnapshot>> fetch,
        Func<DateTimeOffset>? now = null, TimeSpan? ttl = null)
    {
        _cachePath = cachePath;
        _fetch = fetch;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// The account's snapshot: the cached one while fresh, else a new read. <paramref name="forceRefresh"/>
    /// (the Refresh button) reads even when the cache is fresh or a failure is cooling down.
    /// Never throws for network or Last.fm trouble (see <see cref="LastFmTasteResult.Error"/>);
    /// cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<LastFmTasteResult> GetAsync(string user, bool forceRefresh = false,
        string period = LastFmPeriod.Default, CancellationToken ct = default)
    {
        user = (user ?? "").Trim();
        if (user.Length == 0) return new LastFmTasteResult(null, null, false);
        if (!LastFmPeriod.IsValid(period)) period = LastFmPeriod.Default;

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var now = _now();
            var mine = _snapshot != null && string.Equals(_snapshot.User, user, StringComparison.OrdinalIgnoreCase)
                       && _snapshot.Period == period ? _snapshot : null;
            if (!forceRefresh)
            {
                if (mine != null && now - mine.FetchedUtc < _ttl && mine.FetchedUtc <= now)
                    return new LastFmTasteResult(mine, null, true);
                if (_lastFailure is { } f && string.Equals(f.User, user, StringComparison.OrdinalIgnoreCase)
                    && now - f.At < FailureCooldown)
                    return new LastFmTasteResult(mine, f.Error, true);
            }

            try
            {
                var fresh = await _fetch(user, period, now, ct);
                _snapshot = fresh;
                _lastFailure = null;
                await SaveAsync(fresh, ct);
                return new LastFmTasteResult(fresh, null, false);
            }
            catch (LastFmTasteException ex)
            {
                _lastFailure = (user, now, ex.Kind);
                DebugLog.Write("LastFmTaste", $"refresh failed: {ex.Kind}");
                return new LastFmTasteResult(mine, ex.Kind, true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The snapshot already held for the account (memory or disk), without a request.</summary>
    public async Task<LastFmTasteSnapshot?> PeekAsync(string user, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return _snapshot != null && string.Equals(_snapshot.User, (user ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                ? _snapshot : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets the snapshot and deletes the cache file (sign-out, Reset Everything).</summary>
    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _snapshot = null;
            _lastFailure = null;
            _loaded = true;
            try { File.Delete(_cachePath); }
            catch (Exception ex) { Debug.WriteLine($"[LastFmTaste] Failed to delete cache: {ex.Message}"); }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_cachePath)) return;
            await using var stream = File.OpenRead(_cachePath);
            var s = await JsonSerializer.DeserializeAsync<LastFmTasteSnapshot>(stream, JsonOptions, ct);
            if (s != null && s.Version == LastFmTasteSnapshot.CurrentVersion && !string.IsNullOrWhiteSpace(s.User))
                _snapshot = s;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _loaded = false;
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFmTaste] Failed to read cache: {ex.Message}");
        }
    }

    private async Task SaveAsync(LastFmTasteSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            // Written beside and swapped in, so a crash mid-write never leaves half a file.
            var tmp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot, JsonOptions), ct);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFmTaste] Failed to write cache: {ex.Message}");
        }
    }
}
