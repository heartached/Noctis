using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctis.Services;

// GitHub #222: the Statistics page with "Last.fm" as its source. Read-only public GETs, like the
// #177 taste reads (account NAME + the app's API key, never the session key): user.getInfo,
// user.getTopArtists / getTopAlbums / getTopTracks for the chart period, and user.getRecentTracks
// with limit=1 for how many scrobbles fall in a window (its "@attr" total). Nothing is written
// to Last.fm and nothing here touches local play counts.

/// <summary>What one refresh read for one account and one chart period; what the cache holds.</summary>
public sealed class LastFmStatsSnapshot
{
    public string User { get; set; } = "";
    /// <summary>The Last.fm chart period (<see cref="LastFmPeriod"/>).</summary>
    public string Period { get; set; } = LastFmPeriod.Overall;
    public DateTimeOffset FetchedUtc { get; set; }

    /// <summary>Scrobbles in the period: the window's recent-tracks total, or the account's
    /// lifetime play count for <see cref="LastFmPeriod.Overall"/>. Null when Last.fm didn't say.</summary>
    public long? Scrobbles { get; set; }
    /// <summary>Scrobbles in the window of the same length just before (null for overall).</summary>
    public long? PreviousScrobbles { get; set; }
    /// <summary>Distinct artists / songs in the period's charts (the charts' totals).</summary>
    public long? ArtistCount { get; set; }
    public long? TrackCount { get; set; }
    /// <summary>When the account started scrobbling (user.getInfo "registered").</summary>
    public DateTimeOffset? RegisteredUtc { get; set; }

    public List<LastFmTasteArtist> TopArtists { get; set; } = new();
    public List<LastFmTasteAlbum> TopAlbums { get; set; } = new();
    public List<LastFmTasteTrack> TopTracks { get; set; } = new();
}

/// <summary>The cache file: one account, one snapshot per chart period.</summary>
public sealed class LastFmStatsCache
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string User { get; set; } = "";
    public Dictionary<string, LastFmStatsSnapshot> Periods { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The Statistics reads, through <see cref="LastFmTasteClient"/>'s polite GET (one request
/// at a time with a pause, transient failures retried with back-off, URLs never logged).</summary>
public static class LastFmStatsClient
{
    /// <summary>Rows per list, the same as the page's own (ListeningReportBuilder.TopArtistCount / TopListCount).</summary>
    public const int TopArtistsLimit = 6;
    public const int TopListLimit = 8;

    /// <summary>The window user.getRecentTracks counts for a chart period; null for overall.
    /// "1month" is counted as 30 days, the page's own "30 days".</summary>
    public static TimeSpan? WindowOf(string period) => period switch
    {
        LastFmPeriod.SevenDays => TimeSpan.FromDays(7),
        LastFmPeriod.OneMonth => TimeSpan.FromDays(30),
        LastFmPeriod.ThreeMonths => TimeSpan.FromDays(90),
        LastFmPeriod.SixMonths => TimeSpan.FromDays(180),
        LastFmPeriod.TwelveMonths => TimeSpan.FromDays(365),
        _ => null,
    };

    /// <summary>
    /// Reads the account's figures for <paramref name="period"/>: 4 requests for overall, 6 for a
    /// windowed period. The charts must succeed; the scrobble totals are best effort (null on a miss).
    /// </summary>
    public static async Task<LastFmStatsSnapshot> FetchAsync(LastFmTasteClient client, string user, string period,
        DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new LastFmTasteException(LastFmTasteError.UserNotFound);
        if (!LastFmPeriod.IsValid(period)) period = LastFmPeriod.Overall;
        var u = Uri.EscapeDataString(user.Trim());
        var s = new LastFmStatsSnapshot { User = user.Trim(), Period = period, FetchedUtc = now };

        using (var doc = await client.GetAsync($"method=user.getinfo&user={u}", ct))
        {
            if (doc.RootElement.TryGetProperty("user", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                if (period == LastFmPeriod.Overall) s.Scrobbles = LastFmTasteClient.ReadLong(info, "playcount");
                if (info.TryGetProperty("registered", out var reg))
                {
                    var unix = reg.ValueKind == JsonValueKind.Object
                        ? LastFmTasteClient.ReadLong(reg, "unixtime") ?? LastFmTasteClient.ReadLong(reg, "#text")
                        : LastFmTasteClient.ReadLong(info, "registered");
                    if (unix is > 0) s.RegisteredUtc = DateTimeOffset.FromUnixTimeSeconds(unix.Value);
                }
            }
        }

        using (var doc = await client.GetAsync($"method=user.gettopartists&user={u}&period={period}&limit={TopArtistsLimit}", ct))
        {
            s.TopArtists = LastFmTasteClient.ParseArtists(doc.RootElement);
            s.ArtistCount = LastFmTasteClient.ReadTotal(doc.RootElement, "topartists");
        }
        using (var doc = await client.GetAsync($"method=user.gettopalbums&user={u}&period={period}&limit={TopListLimit}", ct))
            s.TopAlbums = LastFmTasteClient.ParseAlbums(doc.RootElement);
        using (var doc = await client.GetAsync($"method=user.gettoptracks&user={u}&period={period}&limit={TopListLimit}", ct))
        {
            s.TopTracks = LastFmTasteClient.ParseTracks(doc.RootElement, "toptracks");
            s.TrackCount = LastFmTasteClient.ReadTotal(doc.RootElement, "toptracks");
        }

        if (WindowOf(period) is { } window)
        {
            var to = now.ToUnixTimeSeconds();
            var from = (now - window).ToUnixTimeSeconds();
            var before = (now - window - window).ToUnixTimeSeconds();
            s.Scrobbles = await CountAsync(client, u, from, to, ct);
            s.PreviousScrobbles = await CountAsync(client, u, before, from, ct);
        }
        return s;
    }

    private static async Task<long?> CountAsync(LastFmTasteClient client, string u, long from, long to, CancellationToken ct)
    {
        using var doc = await client.GetAsync($"method=user.getrecenttracks&user={u}&from={from}&to={to}&limit=1", ct);
        return LastFmTasteClient.ReadTotal(doc.RootElement, "recenttracks");
    }
}

/// <summary>What <see cref="LastFmStatsService.GetAsync"/> found: the snapshot to show (fresh, cached,
/// or the old one after a failed refresh; null when there is none) and the refresh error, if any.</summary>
public sealed record LastFmStatsResult(LastFmStatsSnapshot? Snapshot, LastFmTasteError? Error, bool FromCache);

/// <summary>
/// The Statistics snapshots for one account, one per chart period, cached in memory and in a JSON
/// file (cache/lastfm_stats.json). A snapshot younger than the TTL (1 h) is served without a
/// request; an older one is still shown while a refresh runs. A failed refresh is not retried for
/// <see cref="FailureCooldown"/> unless forced (Try Again), so an offline visit costs nothing.
/// One fetch at a time; cancelling (page left, period or source changed) stops between requests.
/// </summary>
public sealed class LastFmStatsService
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private readonly string _cachePath;
    private readonly Func<string, string, DateTimeOffset, CancellationToken, Task<LastFmStatsSnapshot>> _fetch;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private LastFmStatsCache _cache = new();
    private bool _loaded;
    private (string User, string Period, DateTimeOffset At, LastFmTasteError Error)? _lastFailure;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public LastFmStatsService(HttpClient http, string cachePath, Func<DateTimeOffset>? now = null, TimeSpan? ttl = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : this(cachePath, (user, period, at, ct) => LastFmStatsClient.FetchAsync(new LastFmTasteClient(http, delay), user, period, at, ct), now, ttl)
    {
    }

    /// <summary>For tests: any fetch function in place of the HTTP client.</summary>
    internal LastFmStatsService(string cachePath,
        Func<string, string, DateTimeOffset, CancellationToken, Task<LastFmStatsSnapshot>> fetch,
        Func<DateTimeOffset>? now = null, TimeSpan? ttl = null)
    {
        _cachePath = cachePath;
        _fetch = fetch;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>True while <paramref name="snapshot"/> needs no refresh.</summary>
    public bool IsFresh(LastFmStatsSnapshot snapshot)
    {
        var now = _now();
        return snapshot.FetchedUtc <= now && now - snapshot.FetchedUtc < _ttl;
    }

    /// <summary>The snapshot already held for the account and period (any age), without a request.</summary>
    public async Task<LastFmStatsSnapshot?> PeekAsync(string user, string period, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return Find(user, period);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The account's snapshot for the period: the cached one while fresh, else a new read.
    /// Never throws for network or Last.fm trouble (see <see cref="LastFmStatsResult.Error"/>);
    /// cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<LastFmStatsResult> GetAsync(string user, string period, bool forceRefresh = false, CancellationToken ct = default)
    {
        user = (user ?? "").Trim();
        if (user.Length == 0) return new LastFmStatsResult(null, null, false);
        if (!LastFmPeriod.IsValid(period)) period = LastFmPeriod.Overall;

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var now = _now();
            var mine = Find(user, period);
            if (!forceRefresh)
            {
                if (mine != null && IsFresh(mine))
                    return new LastFmStatsResult(mine, null, true);
                if (_lastFailure is { } f && string.Equals(f.User, user, StringComparison.OrdinalIgnoreCase)
                    && f.Period == period && now - f.At < FailureCooldown)
                    return new LastFmStatsResult(mine, f.Error, true);
            }

            try
            {
                var fresh = await _fetch(user, period, now, ct);
                if (!string.Equals(_cache.User, user, StringComparison.OrdinalIgnoreCase))
                    _cache = new LastFmStatsCache { User = user }; // another account: its figures go
                _cache.Periods[period] = fresh;
                _lastFailure = null;
                await SaveAsync(ct);
                return new LastFmStatsResult(fresh, null, false);
            }
            catch (LastFmTasteException ex)
            {
                _lastFailure = (user, period, now, ex.Kind);
                DebugLog.Write("LastFmStats", $"refresh failed: {ex.Kind}");
                return new LastFmStatsResult(mine, ex.Kind, true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets every snapshot and deletes the cache file (sign-out, Reset Everything).</summary>
    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _cache = new LastFmStatsCache();
            _lastFailure = null;
            _loaded = true;
            try { File.Delete(_cachePath); }
            catch (Exception ex) { Debug.WriteLine($"[LastFmStats] Failed to delete cache: {ex.Message}"); }
        }
        finally
        {
            _lock.Release();
        }
    }

    private LastFmStatsSnapshot? Find(string? user, string period)
        => string.Equals(_cache.User, (user ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
           && _cache.Periods.TryGetValue(period, out var s) ? s : null;

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_cachePath)) return;
            await using var stream = File.OpenRead(_cachePath);
            var c = await JsonSerializer.DeserializeAsync<LastFmStatsCache>(stream, JsonOptions, ct);
            if (c != null && c.Version == LastFmStatsCache.CurrentVersion && !string.IsNullOrWhiteSpace(c.User) && c.Periods != null)
                _cache = new LastFmStatsCache { User = c.User, Periods = new(c.Periods, StringComparer.Ordinal) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _loaded = false;
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFmStats] Failed to read cache: {ex.Message}");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            // Written beside and swapped in, so a crash mid-write never leaves half a file.
            var tmp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_cache, JsonOptions), ct);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFmStats] Failed to write cache: {ex.Message}");
        }
    }
}
