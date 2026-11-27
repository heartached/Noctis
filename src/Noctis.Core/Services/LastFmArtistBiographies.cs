using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Artist biographies from Last.fm's artist.getinfo: the bio's summary and full text, cleaned
/// exactly as <see cref="LastFmAlbumDescriptions"/> cleans an album's wiki (HTML, the "Read more
/// on Last.fm" link and the licence line off), cached in a JSON file keyed by the artist's name
/// (trimmed, lower-case). Same API key, same cooldown, same rule for unanswered lookups as the
/// album descriptions: the phone asks it when an artist page opens.
/// </summary>
public sealed class LastFmArtistBiographies
{
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly bool _rememberFailedLookups;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    /// <summary>Names that are a library's placeholder, not an artist: Last.fm has pages for some
    /// of them ("Various Artists" is a real Last.fm page about compilations), never a biography.</summary>
    private static readonly HashSet<string> PlaceholderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unknown Artist", Track.VariousArtists, "Various", "VA", "V.A.", "[unknown]", "<unknown>",
    };

    /// <param name="http">Client for the artist.getinfo GETs; the caller owns its timeout policy.</param>
    /// <param name="cachePath">The JSON cache file (the phone's is cache/lastfm_artist_bios.json under its data directory).</param>
    /// <param name="rememberFailedLookups">As <see cref="LastFmAlbumDescriptions"/>'s: false (the
    /// phone) asks again on a later visit when a lookup never got an answer.</param>
    public LastFmArtistBiographies(HttpClient http, string cachePath, bool rememberFailedLookups = true)
    {
        _http = http;
        _cachePath = cachePath;
        _rememberFailedLookups = rememberFailedLookups;
    }

    /// <summary>Whether <paramref name="artistName"/> is worth asking about: not blank and not a
    /// placeholder ("Unknown Artist", "Various Artists").</summary>
    public static bool IsLookupName(string? artistName) =>
        !string.IsNullOrWhiteSpace(artistName) && !PlaceholderNames.Contains(artistName.Trim());

    /// <summary>
    /// The artist's biography: the cached Last.fm text, else a fresh lookup (at most one per
    /// artist every 20 s). <paramref name="preferFullText"/> picks the full bio over the summary,
    /// falling back to the other one. Null when there is none or the name is a placeholder.
    /// Cancelling throws <see cref="OperationCanceledException"/> and caches nothing.
    /// </summary>
    public async Task<string?> GetAsync(string artistName, bool preferFullText, CancellationToken ct = default)
    {
        if (!IsLookupName(artistName))
            return null;

        var cacheKey = BuildCacheKey(artistName);
        await EnsureCacheLoadedAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return SelectBiography(cached, preferFullText);

            var now = DateTime.UtcNow;
            if (_cooldownUntil.TryGetValue(cacheKey, out var cooldownUntil) && now < cooldownUntil)
                return null;

            _cooldownUntil[cacheKey] = now.Add(Cooldown);
        }
        finally
        {
            _lock.Release();
        }

        var (fetched, answered) = await FetchFromApiAsync(artistName, ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (!answered && !_rememberFailedLookups)
                return null;

            var entry = new CacheEntry
            {
                Summary = fetched?.Summary ?? string.Empty,
                FullContent = fetched?.FullContent ?? string.Empty,
                UpdatedUtc = DateTime.UtcNow
            };
            _cache[cacheKey] = entry;
            await SaveCacheUnsafeAsync(ct);
            return SelectBiography(entry, preferFullText);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets every biography and deletes the cache file.</summary>
    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _cache.Clear();
            _cooldownUntil.Clear();
            _cacheLoaded = true;
            try { File.Delete(_cachePath); }
            catch (Exception ex) { Debug.WriteLine($"[LastFm] Failed to delete artist bio cache: {ex.Message}"); }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken ct)
    {
        if (_cacheLoaded)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cacheLoaded)
                return;

            if (File.Exists(_cachePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_cachePath, ct);
                    var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                    if (entries != null)
                    {
                        foreach (var kvp in entries)
                        {
                            if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;
                            _cache[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[LastFm] Failed to read artist bio cache: {ex.Message}");
                }
            }

            _cacheLoaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveCacheUnsafeAsync(CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_cache);
            await File.WriteAllTextAsync(_cachePath, json, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Failed to write artist bio cache: {ex.Message}");
        }
    }

    /// <summary>The artist's bio; <c>Answered</c> is false when Last.fm never answered (no
    /// network, a server error, a rate limit), as for the album descriptions.</summary>
    private async Task<(Payload? Payload, bool Answered)> FetchFromApiAsync(string artistName, CancellationToken ct)
    {
        try
        {
            var url = $"{LastFmApi.ApiBase}?method=artist.getinfo&api_key={LastFmApi.ApiKey}&artist={Uri.EscapeDataString(artistName.Trim())}&format=json";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return (null, (int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests);

            var payload = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
            using var doc = JsonDocument.Parse(payload);

            if (doc.RootElement.TryGetProperty("error", out _)
                || !doc.RootElement.TryGetProperty("artist", out var artist)
                || !artist.TryGetProperty("bio", out var bio))
                return (null, true);

            var summary = bio.TryGetProperty("summary", out var summaryNode) && summaryNode.ValueKind == JsonValueKind.String
                ? LastFmAlbumDescriptions.CleanAlbumSummary(summaryNode.GetString())
                : null;
            var fullContent = bio.TryGetProperty("content", out var contentNode) && contentNode.ValueKind == JsonValueKind.String
                ? LastFmAlbumDescriptions.CleanAlbumContent(contentNode.GetString())
                : null;

            // A bio of only Last.fm's link cleans to nothing: no biography.
            if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(fullContent))
                return (null, true);

            summary ??= fullContent ?? string.Empty;
            fullContent ??= summary;
            return (new Payload { Summary = summary, FullContent = fullContent }, true);
        }
        catch (OperationCanceledException)
        {
            // The caller's cancellation and HttpClient's own timeout alike: nothing is cached.
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Artist bio fetch failed for '{artistName}': {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>The cache key: the artist's name, trimmed and lower-cased.</summary>
    internal static string BuildCacheKey(string artistName) => artistName.Trim().ToLowerInvariant();

    private static string? SelectBiography(CacheEntry entry, bool preferFullText)
    {
        var first = preferFullText ? entry.FullContent : entry.Summary;
        var second = preferFullText ? entry.Summary : entry.FullContent;
        if (!string.IsNullOrWhiteSpace(first)) return first;
        if (!string.IsNullOrWhiteSpace(second)) return second;
        return null;
    }

    private sealed class CacheEntry
    {
        public string Summary { get; set; } = string.Empty;
        public string FullContent { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class Payload
    {
        public string Summary { get; set; } = string.Empty;
        public string FullContent { get; set; } = string.Empty;
    }
}
