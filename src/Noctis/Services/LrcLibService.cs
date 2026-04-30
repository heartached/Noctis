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

    public async Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds)
    {
        var cacheKey = $"get:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (TryGetCached(cacheKey, out LrcLibResult? cached))
            return cached;

        try
        {
            var url = $"{BaseUrl}/get?artist_name={Uri.EscapeDataString(artist)}" +
                      $"&track_name={Uri.EscapeDataString(trackName)}" +
                      $"&duration={Math.Round(durationSeconds)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Noctis/1.0");

            using var response = await _http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Store(cacheKey, (LrcLibResult?)null);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<LrcLibResult>(json);

            Store(cacheKey, result);
            return result;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName)
    {
        var cacheKey = $"search:{artist}|{trackName}";
        if (TryGetCached(cacheKey, out List<LrcLibResult>? cached) && cached != null)
            return cached;

        try
        {
            var url = $"{BaseUrl}/search?artist_name={Uri.EscapeDataString(artist)}" +
                      $"&track_name={Uri.EscapeDataString(trackName)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Noctis/1.0");

            using var response = await _http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var empty = new List<LrcLibResult>();
                Store(cacheKey, empty);
                return empty;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var results = JsonSerializer.Deserialize<List<LrcLibResult>>(json) ?? new List<LrcLibResult>();

            Store(cacheKey, results);
            return results;
        }
        catch (HttpRequestException)
        {
            return new List<LrcLibResult>();
        }
    }

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
