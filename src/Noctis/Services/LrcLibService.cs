using System.Globalization;
using System.Net;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

public class LrcLibService : ILrcLibService
{
    private const string BaseUrl = "https://lrclib.net/api";
    private const int MaxCacheEntries = 256;

    private readonly HttpClient _http;

    // Bounded LRU for /api/get and /api/search responses. Guarded by _cacheLock.
    // A LinkedList provides O(1) recency-touch + eviction; the dictionary is the lookup table.
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cacheIndex = new();
    private readonly LinkedList<CacheEntry> _cacheOrder = new();

    private readonly record struct CacheEntry(string Key, object? Value);

    public LrcLibService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = CacheKey("get", artist, trackName, Math.Round(durationSeconds).ToString(CultureInfo.InvariantCulture));
        if (TryGetCached(cacheKey, out LrcLibResult? cached))
            return cached;

        try
        {
            var url = $"{BaseUrl}/get?artist_name={Uri.EscapeDataString(artist)}" +
                      $"&track_name={Uri.EscapeDataString(trackName)}" +
                      $"&duration={Math.Round(durationSeconds)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Noctis (https://github.com/heartached/Noctis)");

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Store(cacheKey, (LrcLibResult?)null);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
            var result = JsonSerializer.Deserialize<LrcLibResult>(json);

            Store(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancel (track skip) — propagate silently, never
            // report it as a provider error and never poison the cache.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Network failure, 5xx, timeout, or malformed body. A definitive miss
            // is the cached-null 404 path above — this must stay distinguishable,
            // and stays uncached so a later attempt can succeed.
            throw new LyricsProviderException("LRCLIB", ex);
        }
    }

    public async Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
    {
        var cacheKey = CacheKey("search", artist, trackName);
        if (TryGetCached(cacheKey, out List<LrcLibResult>? cached) && cached != null)
            return cached;

        try
        {
            var url = $"{BaseUrl}/search?artist_name={Uri.EscapeDataString(artist)}" +
                      $"&track_name={Uri.EscapeDataString(trackName)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Noctis (https://github.com/heartached/Noctis)");

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var empty = new List<LrcLibResult>();
                Store(cacheKey, empty);
                return empty;
            }

            response.EnsureSuccessStatusCode();
            var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
            var results = JsonSerializer.Deserialize<List<LrcLibResult>>(json) ?? new List<LrcLibResult>();

            Store(cacheKey, results);
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new LyricsProviderException("LRCLIB", ex);
        }
    }

    /// <summary>
    /// Length-prefixed join makes the key injective: artist "A|B" + title "C"
    /// can no longer collide with artist "A" + title "B|C".
    /// </summary>
    private static string CacheKey(string prefix, params string[] parts)
        => prefix + string.Concat(parts.Select(p => $"|{p.Length}:{p}"));

    // ── Bounded LRU ──

    private bool TryGetCached<T>(string key, out T? value)
    {
        lock (_cacheLock)
        {
            if (_cacheIndex.TryGetValue(key, out var node))
            {
                _cacheOrder.Remove(node);
                _cacheOrder.AddFirst(node);
                value = (T?)node.Value.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private void Store<T>(string key, T? value)
    {
        lock (_cacheLock)
        {
            if (_cacheIndex.TryGetValue(key, out var existing))
            {
                _cacheOrder.Remove(existing);
                _cacheIndex.Remove(key);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, value));
            _cacheOrder.AddFirst(node);
            _cacheIndex[key] = node;

            while (_cacheIndex.Count > MaxCacheEntries)
            {
                var oldest = _cacheOrder.Last;
                if (oldest == null) break;
                _cacheOrder.RemoveLast();
                _cacheIndex.Remove(oldest.Value.Key);
            }
        }
    }
}
