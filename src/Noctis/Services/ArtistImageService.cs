using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

public class ArtistImageService
{
    private const string DeezerSearchUrl = "https://api.deezer.com/search/artist";
    private readonly HttpClient _http;
    private readonly string _artistArtworkDir;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly Dictionary<string, DateTime> _failedArtistCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FailedArtistCooldown = TimeSpan.FromMinutes(15);
    private static readonly Regex ArtistSplitRegex = new(@"\s*(?:,|;|/|&|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\band\b|\bwith\b|\bx\b)\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    /// <summary>
    /// Fetches and caches artist images in the background.
    /// Calls onImageReady for each artist that gets a new image.
    /// </summary>
    public async Task FetchAndCacheAsync(IReadOnlyList<Artist> artists, Action<Artist, string>? onImageReady = null)
    {
        if (!await _fetchGate.WaitAsync(0))
            return;

        try
        {
            foreach (var artist in artists)
            {
                var cachedPath = GetCachedImagePath(artist.Id);

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
                    continue;
                }

                try
                {
                    var imageUrl = await GetDeezerArtistImageUrlAsync(artistName);
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        if (response.IsSuccessStatusCode &&
                            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            var imageData = await response.Content.ReadAsByteArrayAsync();
                            if (imageData.Length > 0)
                            {
                                await File.WriteAllBytesAsync(cachedPath, imageData);
                                artist.ImagePath = cachedPath;
                                onImageReady?.Invoke(artist, cachedPath);
                                _failedArtistCooldownUntil.Remove(artistName);
                                continue;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ArtistImage] Failed for '{artist.Name}': {ex.Message}");
                }

                _failedArtistCooldownUntil[artistName] = DateTime.UtcNow.Add(FailedArtistCooldown);

                // Rate limit: Deezer allows 50 requests per 5 seconds.
                await Task.Delay(120);
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>
    /// Searches Deezer for the artist and returns a 500x500 image URL.
    /// Tries the full artist name first, then the primary artist for collaborations.
    /// </summary>
    private async Task<string?> GetDeezerArtistImageUrlAsync(string artistName)
    {
        foreach (var candidate in BuildArtistCandidates(artistName))
        {
            var url = $"{DeezerSearchUrl}?q={Uri.EscapeDataString(candidate)}&limit=1";
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                continue;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
                continue;

            var first = data[0];
            // Prefer picture_big (500x500), fall back to picture_medium (250x250)
            if (first.TryGetProperty("picture_big", out var bigNode))
            {
                var imageUrl = bigNode.GetString();
                if (!string.IsNullOrWhiteSpace(imageUrl))
                    return imageUrl;
            }
            if (first.TryGetProperty("picture_medium", out var medNode))
            {
                var imageUrl = medNode.GetString();
                if (!string.IsNullOrWhiteSpace(imageUrl))
                    return imageUrl;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildArtistCandidates(string artistName)
    {
        var normalized = artistName.Trim();
        if (normalized.Length == 0)
            yield break;

        yield return normalized;

        var primary = ArtistSplitRegex
            .Split(normalized)
            .Select(t => t.Trim())
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (!string.IsNullOrWhiteSpace(primary) &&
            !string.Equals(primary, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return primary;
        }
    }
}
