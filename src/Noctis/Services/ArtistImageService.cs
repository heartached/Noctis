using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

public class ArtistImageService
{
    private const string DeezerSearchUrl = "https://api.deezer.com/search/artist";
    private const int DeezerSearchLimit = 10;
    private readonly HttpClient _http;
    private readonly string _artistArtworkDir;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly Dictionary<string, DateTime> _failedArtistCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FailedArtistCooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Delay before each artist's network work. Deezer allows 50 requests per 5 seconds
    /// and every artist costs up to two (search + image), so 120ms keeps a full-library
    /// sweep comfortably inside the budget.
    /// </summary>
    private const int RequestPacingMs = 120;

    public ArtistImageService(HttpClient http, IPersistenceService persistence)
    {
        _http = http;
        _artistArtworkDir = Path.Combine(persistence.DataDirectory, "artwork", "artists");
        Directory.CreateDirectory(_artistArtworkDir);

        // Defer the placeholder purge off the DI resolution path so it doesn't
        // enumerate the artwork directory on the UI thread during startup.
        _ = Task.Run(PurgeLastFmPlaceholders);
    }

    /// <summary>
    /// One-time cleanup: remove old Last.fm placeholder images (all ≤5KB, same generic star icon)
    /// so they get re-fetched from Deezer with real artist photos.
    /// </summary>
    private void PurgeLastFmPlaceholders()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_artistArtworkDir, "*.jpg"))
            {
                var info = new FileInfo(file);
                if (info.Length > 0 && info.Length <= 5120)
                    info.Delete();
            }
        }
        catch { }
    }

    public string GetCachedImagePath(Guid artistId)
        => Path.Combine(_artistArtworkDir, $"{artistId}.jpg");

    public bool HasCachedImage(Guid artistId)
        => File.Exists(GetCachedImagePath(artistId));

    /// <summary>Sentinel marking an artist whose image the user explicitly removed,
    /// so the background fetcher leaves it blank instead of re-downloading.</summary>
    private string GetRemovedMarkerPath(Guid artistId)
        => Path.Combine(_artistArtworkDir, $"{artistId}.removed");

    public bool IsImageRemoved(Guid artistId)
        => File.Exists(GetRemovedMarkerPath(artistId));

    /// <summary>
    /// Saves a user-picked image as the artist's portrait, overriding any auto-fetched
    /// art and clearing a prior "removed" marker. Returns the cached path, or null on failure.
    /// </summary>
    public async Task<string?> SetCustomImageAsync(Artist artist, byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0)
            return null;

        var cachedPath = GetCachedImagePath(artist.Id);
        try
        {
            await File.WriteAllBytesAsync(cachedPath, imageData);

            var marker = GetRemovedMarkerPath(artist.Id);
            if (File.Exists(marker))
                File.Delete(marker);

            artist.ImagePath = cachedPath;
            return cachedPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtistImage] Failed to set custom image for '{artist.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Removes the artist's portrait (custom or auto-fetched) and marks it so the
    /// background fetcher won't re-download it. The grid falls back to the placeholder.
    /// </summary>
    public void RemoveImage(Artist artist)
    {
        try
        {
            var cachedPath = GetCachedImagePath(artist.Id);
            if (File.Exists(cachedPath))
                File.Delete(cachedPath);

            File.WriteAllText(GetRemovedMarkerPath(artist.Id), string.Empty);
            artist.ImagePath = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtistImage] Failed to remove image for '{artist.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches and caches artist images in the background.
    /// Calls onImageReady for each artist that gets a new image.
    /// </summary>
    public async Task FetchAndCacheAsync(IReadOnlyList<Artist> artists, Action<Artist, string>? onImageReady = null)
    {
        // Get off the caller's thread before doing anything.
        //
        // The Artists tab calls this fire-and-forget from Refresh(). With an uncontended
        // semaphore the WaitAsync below completes synchronously, and with no
        // ConfigureAwait(false) the continuation stayed on the UI thread — so for every
        // already-cached artist the loop never hit a real await and just ran two blocking
        // File.Exists calls inline. On a 4,000-artist library that was thousands of stats
        // on the UI thread before the tab could paint.
        await Task.Yield();

        await _fetchGate.WaitAsync().ConfigureAwait(false);

        try
        {
            var artistsByName = artists
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var artist in artists)
            {
                var cachedPath = GetCachedImagePath(artist.Id);

                // Honor an explicit user removal: keep the portrait blank instead of
                // re-downloading. Checked before the cache hit so a lingering file
                // (e.g. from a fetch that raced the removal) never resurfaces.
                if (File.Exists(GetRemovedMarkerPath(artist.Id)))
                {
                    if (artist.ImagePath != null)
                        artist.ImagePath = null;
                    continue;
                }

                // Skip if already cached
                if (File.Exists(cachedPath))
                {
                    if (artist.ImagePath != cachedPath)
                    {
                        artist.ImagePath = cachedPath;
                        onImageReady?.Invoke(artist, cachedPath);
                    }
                    continue;
                }

                // Skip unknown artists
                if (string.IsNullOrWhiteSpace(artist.Name) || artist.Name == "Unknown Artist")
                    continue;

                var artistName = artist.Name.Trim();
                if (_failedArtistCooldownUntil.TryGetValue(artistName, out var retryAt) &&
                    DateTime.UtcNow < retryAt)
                {
                    TryUsePrimaryArtistImageFallback(artist, artistsByName, onImageReady);
                    continue;
                }

                // Rate limit: Deezer allows 50 requests per 5 seconds. Paced BEFORE the
                // request, so it applies to every network call. It used to sit at the
                // very bottom of the loop, after the `continue`s for both success and
                // fallback — i.e. only on outright failure. On a first scan each
                // *successful* artist costs at least two requests (search + image) with
                // zero pacing, so a few hundred artists burst far past the limit, Deezer
                // started 429-ing, and that poisoned the whole sweep into the 15-minute
                // cooldown.
                await Task.Delay(RequestPacingMs).ConfigureAwait(false);

                try
                {
                    if (await TryDownloadAndSaveAsync(artistName, cachedPath).ConfigureAwait(false))
                    {
                        artist.ImagePath = cachedPath;
                        onImageReady?.Invoke(artist, cachedPath);
                        _failedArtistCooldownUntil.Remove(artistName);
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Download watchdog fired (or shutdown) — don't burn the cooldown.
                    Debug.WriteLine($"[ArtistImage] Timed out for '{artist.Name}'");
                    continue;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ArtistImage] Failed for '{artist.Name}': {ex.Message}");
                }

                if (TryUsePrimaryArtistImageFallback(artist, artistsByName, onImageReady))
                {
                    _failedArtistCooldownUntil.Remove(artistName);
                    continue;
                }

                _failedArtistCooldownUntil[artistName] = DateTime.UtcNow.Add(FailedArtistCooldown);
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>
    /// Looks the artist up online and re-downloads their photo, clearing any prior
    /// "removed" marker so the restored image isn't suppressed on the next refresh.
    /// Use to bring a portrait back after Remove, or to refresh a custom one.
    /// Returns the cached path, or null if nothing was found.
    /// </summary>
    public async Task<string?> RefetchImageAsync(Artist artist)
    {
        if (artist == null || string.IsNullOrWhiteSpace(artist.Name) || artist.Name == "Unknown Artist")
            return null;

        var artistName = artist.Name.Trim();
        var cachedPath = GetCachedImagePath(artist.Id);

        try
        {
            var marker = GetRemovedMarkerPath(artist.Id);
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch { }

        try
        {
            if (await TryDownloadAndSaveAsync(artistName, cachedPath))
            {
                artist.ImagePath = cachedPath;
                return cachedPath;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtistImage] Refetch failed for '{artist.Name}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Resolves the artist's Deezer photo and writes it to <paramref name="cachedPath"/>.
    /// Returns true if an image was downloaded and saved.
    /// </summary>
    /// <summary>Hard ceiling for a single artist-image download, headers plus body.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    private async Task<bool> TryDownloadAndSaveAsync(string artistName, string cachedPath,
        CancellationToken ct = default)
    {
        // HttpClient.Timeout only covers up to the response headers when
        // ResponseHeadersRead is used, and no token was threaded through the body read —
        // so a stalled/half-open connection to the CDN blocked ReadBytesBoundedAsync with
        // nothing able to cancel it. Because FetchAndCacheAsync holds _fetchGate for the
        // whole sweep, one stalled socket blocked every later artist-image fetch for the
        // rest of the process lifetime, and shutdown couldn't cancel it either.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DownloadTimeout);
        var token = timeoutCts.Token;

        var imageUrl = await GetDeezerArtistImageUrlAsync(artistName, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(imageUrl))
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            return false;

        var imageData = await HttpSafety
            .ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, token).ConfigureAwait(false);
        // Magic-byte check: an error/HTML page must never be cached as artwork.
        if (imageData.Length == 0 || !HttpSafety.LooksLikeImage(imageData))
            return false;

        // The cache subdirectory is created once in the constructor, so a "Clear Artwork
        // Cache" (which deletes the whole artwork tree) left every later write throwing
        // DirectoryNotFoundException into a swallowing catch — artist photos silently
        // stopped caching until the next app restart.
        var dir = Path.GetDirectoryName(cachedPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(cachedPath, imageData, token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Searches Deezer for the artist and returns a 500x500 image URL.
    /// Tries the full artist name first, then the primary artist for collaborations.
    /// </summary>
    private async Task<string?> GetDeezerArtistImageUrlAsync(string artistName, CancellationToken ct = default)
    {
        foreach (var candidate in BuildArtistCandidates(artistName))
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{DeezerSearchUrl}?q={Uri.EscapeDataString(candidate)}&limit={DeezerSearchLimit}";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                continue;

            var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
                continue;

            string? bestImageUrl = null;
            var bestRank = int.MaxValue;

            foreach (var item in data.EnumerateArray())
            {
                var imageUrl = GetBestDeezerImageUrl(item);
                if (string.IsNullOrWhiteSpace(imageUrl))
                    continue;

                var resultName = item.TryGetProperty("name", out var nameNode)
                    ? nameNode.GetString()
                    : null;
                var rank = RankDeezerArtistMatch(resultName, candidate);
                if (rank >= bestRank)
                    continue;

                bestRank = rank;
                bestImageUrl = imageUrl;
                if (bestRank == 0)
                    return bestImageUrl;
            }

            if (!string.IsNullOrWhiteSpace(bestImageUrl))
                return bestImageUrl;
        }

        return null;
    }

    private static string? GetBestDeezerImageUrl(JsonElement artistNode)
    {
        foreach (var propertyName in new[] { "picture_xl", "picture_big", "picture_medium" })
        {
            if (!artistNode.TryGetProperty(propertyName, out var node))
                continue;

            var imageUrl = node.GetString();
            if (!string.IsNullOrWhiteSpace(imageUrl) && !IsDeezerPlaceholderUrl(imageUrl))
                return imageUrl;
        }

        return null;
    }

    private static int RankDeezerArtistMatch(string? resultName, string queryName)
    {
        if (string.IsNullOrWhiteSpace(resultName))
            return 100;

        var result = resultName.Trim();
        var query = queryName.Trim();
        if (result.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;

        var compactResult = RemoveWhitespace(result);
        var compactQuery = RemoveWhitespace(query);
        if (compactResult.Equals(compactQuery, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (result.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (result.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;

        return 10;
    }

    private static string RemoveWhitespace(string value)
        => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    private static bool IsDeezerPlaceholderUrl(string url)
        => url.Contains("/artist//", StringComparison.Ordinal)
           || url.Contains("/images/artist//", StringComparison.Ordinal);

    private bool TryUsePrimaryArtistImageFallback(
        Artist artist,
        IReadOnlyDictionary<string, Artist> artistsByName,
        Action<Artist, string>? onImageReady)
    {
        foreach (var candidate in BuildArtistCandidates(artist.Name).Skip(1))
        {
            string? fallbackPath = null;
            if (artistsByName.TryGetValue(candidate, out var primaryArtist))
            {
                fallbackPath = primaryArtist.ImagePath;
                if (string.IsNullOrWhiteSpace(fallbackPath) || !File.Exists(fallbackPath))
                    fallbackPath = GetCachedImagePath(primaryArtist.Id);
            }

            if (string.IsNullOrWhiteSpace(fallbackPath) || !File.Exists(fallbackPath))
                fallbackPath = GetCachedImagePath(ComputeArtistId(candidate));

            if (!File.Exists(fallbackPath))
                continue;

            if (!string.Equals(artist.ImagePath, fallbackPath, StringComparison.OrdinalIgnoreCase))
            {
                artist.ImagePath = fallbackPath;
                onImageReady?.Invoke(artist, fallbackPath);
            }

            return true;
        }

        return false;
    }

    private static Guid ComputeArtistId(string artistName)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
        return new Guid(hash);
    }

    private static IEnumerable<string> BuildArtistCandidates(string artistName)
    {
        var normalized = artistName.Trim();
        if (normalized.Length == 0)
            yield break;

        yield return normalized;

        // Same separators as the artist index, so the portrait lookup for a grouped
        // name tries exactly the name the grid shows.
        var primary = Track.GetPrimaryArtist(normalized);

        if (!string.IsNullOrWhiteSpace(primary) &&
            !string.Equals(primary, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return primary;
        }
    }
}
