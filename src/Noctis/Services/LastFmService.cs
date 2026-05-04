using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

public class LastFmService : ILastFmService
{
    // Last.fm API credentials — register at https://www.last.fm/api/account/create
    private const string ApiKey = "7b625c5a18197cf284aabf8b66505156";
    private const string ApiSecret = "ebf114b9e7d31e24c493bc55dac2184e";
    private const string ApiBase = "https://ws.audioscrobbler.com/2.0/";
    private const string AuthBase = "https://www.last.fm/api/auth/";

    private readonly HttpClient _http;
    private string? _sessionKey;
    private string? _token;
    private readonly string _albumDescriptionCachePath;
    private readonly SemaphoreSlim _albumDescriptionLock = new(1, 1);
    private readonly Dictionary<string, AlbumDescriptionCacheEntry> _albumDescriptionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _albumDescriptionCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private bool _albumDescriptionCacheLoaded;

    private static readonly TimeSpan AlbumDescriptionCooldown = TimeSpan.FromSeconds(20);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex HtmlLineBreakRegex = new("<\\s*(br|/p|/div|/li|/h[1-6])\\s*/?>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MultiWhitespaceRegex = new("\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LastFmReadMoreRegex = new("Read\\s+more\\s+on\\s+Last\\.fm\\s*\\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LastFmLicenseRegex = new("User-?contributed\\s+text\\s+is\\s+available\\s+under\\s+the\\s+Creative\\s+Commons\\s+By-?SA\\s+License;?\\s*additional\\s+terms\\s+may\\s+apply\\s*\\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlankLineRegex = new("\\n{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);


    public bool IsAuthenticated => !string.IsNullOrEmpty(_sessionKey);
    public string? Username { get; private set; }

    public LastFmService(HttpClient http)
    {
        _http = http;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _albumDescriptionCachePath = Path.Combine(appData, "Noctis", "cache", "lastfm_album_descriptions.json");
    }

    public void Configure(string? sessionKey)
    {
        _sessionKey = sessionKey;

        // If we have a session key, validate it by getting user info
        if (!string.IsNullOrEmpty(sessionKey))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var name = await GetAuthenticatedUsernameAsync();
                    Username = name;
                }
                catch
                {
                    // Session key may be expired
                    _sessionKey = null;
                    Username = null;
                }
            });
        }
    }

    public async Task<string> GetAuthUrlAsync()
    {
        // Step 1: get a request token
        _token = null;
        try
        {
            _token = await GetTokenAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Failed to get token: {ex.Message}");
            return "";
        }

        if (string.IsNullOrEmpty(_token)) return "";

        return $"{AuthBase}?api_key={ApiKey}&token={_token}";
    }

    public async Task<bool> CompleteAuthAsync()
    {
        if (string.IsNullOrEmpty(_token)) return false;

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                { "method", "auth.getSession" },
                { "api_key", ApiKey },
                { "token", _token }
            };

            var sig = GenerateSignature(parameters);
            parameters["api_sig"] = sig;
            parameters["format"] = "json";

            var url = BuildUrl(parameters);
            var response = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("session", out var session))
            {
                _sessionKey = session.GetProperty("key").GetString();
                Username = session.GetProperty("name").GetString();
                _token = null;
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Auth failed: {ex.Message}");
        }

        return false;
    }

    public string? GetSessionKey() => _sessionKey;

    public void Logout()
    {
        _sessionKey = null;
        Username = null;
        _token = null;
    }

    public async Task ScrobbleAsync(Track track, DateTime startedAt)
    {
        if (!IsAuthenticated || string.IsNullOrEmpty(track.Artist) || string.IsNullOrEmpty(track.Title))
            return;

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                { "method", "track.scrobble" },
                { "api_key", ApiKey },
                { "sk", _sessionKey! },
                { "artist", track.Artist },
                { "track", track.Title },
                { "timestamp", new DateTimeOffset(startedAt).ToUnixTimeSeconds().ToString() }
            };

            if (!string.IsNullOrWhiteSpace(track.Album))
                parameters["album"] = track.Album;

            if (track.Duration.TotalSeconds > 0)
                parameters["duration"] = ((int)track.Duration.TotalSeconds).ToString();

            var sig = GenerateSignature(parameters);
            parameters["api_sig"] = sig;
            parameters["format"] = "json";

            var content = new FormUrlEncodedContent(parameters);
            await _http.PostAsync(ApiBase, content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Scrobble failed: {ex.Message}");
        }
    }

    public async Task UpdateNowPlayingAsync(Track track)
    {
        if (!IsAuthenticated || string.IsNullOrEmpty(track.Artist) || string.IsNullOrEmpty(track.Title))
            return;

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                { "method", "track.updateNowPlaying" },
                { "api_key", ApiKey },
                { "sk", _sessionKey! },
                { "artist", track.Artist },
                { "track", track.Title }
            };

            if (!string.IsNullOrWhiteSpace(track.Album))
                parameters["album"] = track.Album;

            if (track.Duration.TotalSeconds > 0)
                parameters["duration"] = ((int)track.Duration.TotalSeconds).ToString();

            var sig = GenerateSignature(parameters);
            parameters["api_sig"] = sig;
            parameters["format"] = "json";

            var content = new FormUrlEncodedContent(parameters);
            await _http.PostAsync(ApiBase, content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] NowPlaying update failed: {ex.Message}");
        }
    }

    public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
    {
        return GetAlbumDescriptionInternalAsync(artistName, albumName, preferFullText: false, ct);
    }

    public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
    {
        return GetAlbumDescriptionInternalAsync(artistName, albumName, preferFullText: true, ct);
    }

    public async Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumName))
            return;

        var cacheKey = BuildAlbumDescriptionCacheKey(artistName, albumName);
        await EnsureAlbumDescriptionCacheLoadedAsync(ct);

        await _albumDescriptionLock.WaitAsync(ct);
        try
        {
            if (!_albumDescriptionCache.TryGetValue(cacheKey, out var entry))
            {
                entry = new AlbumDescriptionCacheEntry();
                _albumDescriptionCache[cacheKey] = entry;
            }

            entry.UserOverride = description == null
                ? null
                : CleanAlbumContent(description) ?? string.Empty;
            entry.UpdatedUtc = DateTime.UtcNow;
            await SaveAlbumDescriptionCacheUnsafeAsync(ct);
        }
        finally
        {
            _albumDescriptionLock.Release();
        }
    }

    public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
    {
        return SetAlbumDescriptionOverrideAsync(artistName, albumName, null, ct);
    }

    private async Task<string?> GetAlbumDescriptionInternalAsync(string artistName, string albumName, bool preferFullText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumName))
            return null;

        var cacheKey = BuildAlbumDescriptionCacheKey(artistName, albumName);
        await EnsureAlbumDescriptionCacheLoadedAsync(ct);

        await _albumDescriptionLock.WaitAsync(ct);
        try
        {
            if (_albumDescriptionCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.UserOverride != null)
                    return cached.UserOverride;

                // Re-clean cached text in case earlier versions stored trailing "Read more..." fragments.
                cached.Summary = CleanAlbumSummary(cached.Summary) ?? string.Empty;
                cached.FullContent = CleanAlbumContent(cached.FullContent) ?? string.Empty;

                var cachedValue = SelectDescription(cached, preferFullText);
                var shouldUpgradeToFull = preferFullText &&
                                          string.IsNullOrWhiteSpace(cached.FullContent) &&
                                          !string.IsNullOrWhiteSpace(cached.Summary);

                if (!shouldUpgradeToFull)
                    return cachedValue;
            }

            var now = DateTime.UtcNow;
            if (_albumDescriptionCooldownUntil.TryGetValue(cacheKey, out var cooldownUntil) && now < cooldownUntil)
                return null;

            _albumDescriptionCooldownUntil[cacheKey] = now.Add(AlbumDescriptionCooldown);
        }
        finally
        {
            _albumDescriptionLock.Release();
        }

        var fetched = await FetchAlbumDescriptionFromApiAsync(artistName, albumName, ct);

        await _albumDescriptionLock.WaitAsync(ct);
        try
        {
            var userOverride = _albumDescriptionCache.TryGetValue(cacheKey, out var existingEntry)
                ? existingEntry.UserOverride
                : null;

            _albumDescriptionCache[cacheKey] = new AlbumDescriptionCacheEntry
            {
                Summary = fetched?.Summary ?? string.Empty,
                FullContent = fetched?.FullContent ?? string.Empty,
                UserOverride = userOverride,
                UpdatedUtc = DateTime.UtcNow
            };
            await SaveAlbumDescriptionCacheUnsafeAsync(ct);
            if (userOverride != null)
                return userOverride;

            return fetched == null
                ? null
                : (preferFullText ? fetched.FullContent : fetched.Summary);
        }
        finally
        {
            _albumDescriptionLock.Release();
        }
    }

    private async Task EnsureAlbumDescriptionCacheLoadedAsync(CancellationToken ct)
    {
        if (_albumDescriptionCacheLoaded)
            return;

        await _albumDescriptionLock.WaitAsync(ct);
        try
        {
            if (_albumDescriptionCacheLoaded)
                return;

            if (!File.Exists(_albumDescriptionCachePath))
            {
                _albumDescriptionCacheLoaded = true;
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_albumDescriptionCachePath, ct);
                var entries = JsonSerializer.Deserialize<Dictionary<string, AlbumDescriptionCacheEntry>>(json);
                if (entries != null)
                {
                    foreach (var kvp in entries)
                    {
                        if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                            _albumDescriptionCache[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LastFm] Failed to read album description cache: {ex.Message}");
            }

            _albumDescriptionCacheLoaded = true;
        }
        finally
        {
            _albumDescriptionLock.Release();
        }
    }

    private async Task SaveAlbumDescriptionCacheUnsafeAsync(CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(_albumDescriptionCachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_albumDescriptionCache);
            await File.WriteAllTextAsync(_albumDescriptionCachePath, json, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Failed to write album description cache: {ex.Message}");
        }
    }

    private async Task<AlbumDescriptionPayload?> FetchAlbumDescriptionFromApiAsync(string artistName, string albumName, CancellationToken ct)
    {
        // Try the exact name, then progressively stripped variants
        // (Deluxe / Video Deluxe / Anniversary Edition / etc.) so release-variant
        // albums inherit the base release's description instead of being blank.
        foreach (var candidate in BuildAlbumNameCandidates(albumName))
        {
            var payload = await FetchAlbumDescriptionForExactNameAsync(artistName, candidate, ct);
            if (payload != null) return payload;
            if (ct.IsCancellationRequested) return null;
        }
        return null;
    }

    private async Task<AlbumDescriptionPayload?> FetchAlbumDescriptionForExactNameAsync(string artistName, string albumName, CancellationToken ct)
    {
        try
        {
            var url = $"{ApiBase}?method=album.getinfo&api_key={ApiKey}&artist={Uri.EscapeDataString(artistName)}&album={Uri.EscapeDataString(albumName)}&format=json";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(payload);

            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            if (!doc.RootElement.TryGetProperty("album", out var album))
                return null;

            if (!album.TryGetProperty("wiki", out var wiki))
                return null;

            var summary = wiki.TryGetProperty("summary", out var summaryNode)
                ? CleanAlbumSummary(summaryNode.GetString())
                : null;
            var fullContent = wiki.TryGetProperty("content", out var contentNode)
                ? CleanAlbumContent(contentNode.GetString())
                : null;

            // Keep graceful fallback if only one field is populated.
            if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(fullContent))
                return null;

            summary ??= fullContent ?? string.Empty;
            fullContent ??= summary;

            return new AlbumDescriptionPayload
            {
                Summary = summary,
                FullContent = fullContent
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Album description fetch failed for '{artistName} - {albumName}': {ex.Message}");
            return null;
        }
    }

    private static string BuildAlbumDescriptionCacheKey(string artistName, string albumName)
    {
        return $"{artistName.Trim().ToLowerInvariant()}::{albumName.Trim().ToLowerInvariant()}";
    }

    // Last.fm only catalogs base releases; "(Deluxe)" / "[Video Deluxe]" / etc. variants
    // miss otherwise. Generate progressively stripped candidates so a deluxe edition
    // can inherit its base release's description.
    private static IEnumerable<string> BuildAlbumNameCandidates(string albumName)
    {
        if (string.IsNullOrWhiteSpace(albumName)) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = albumName.Trim();

        if (seen.Add(current)) yield return current;

        for (int i = 0; i < 4; i++)
        {
            var stripped = StripTrailingAlbumVariantSuffix(current);
            if (string.IsNullOrWhiteSpace(stripped) || stripped.Equals(current, StringComparison.OrdinalIgnoreCase))
                yield break;
            current = stripped;
            if (seen.Add(current)) yield return current;
        }
    }

    private static string StripTrailingAlbumVariantSuffix(string name)
    {
        var trimmed = name.TrimEnd();

        // Trailing parenthesized or bracketed group: "Album (Deluxe)" / "Album [Video Deluxe]"
        if (trimmed.Length > 0 && (trimmed[^1] == ')' || trimmed[^1] == ']'))
        {
            char open = trimmed[^1] == ')' ? '(' : '[';
            int depth = 0;
            for (int i = trimmed.Length - 1; i >= 0; i--)
            {
                if (trimmed[i] == trimmed[^1]) depth++;
                else if (trimmed[i] == open)
                {
                    depth--;
                    if (depth == 0) return trimmed[..i].TrimEnd();
                }
            }
        }

        // Dash-suffixed edition tag: "Album - Deluxe Edition"
        int dash = trimmed.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && IsKnownEditionTag(trimmed[(dash + 3)..]))
            return trimmed[..dash].TrimEnd();

        return trimmed;
    }

    private static bool IsKnownEditionTag(string tail)
    {
        var t = tail.Trim();
        return t.Equals("Deluxe", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Deluxe Edition", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Video Deluxe", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Anniversary Edition", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Special Edition", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Expanded Edition", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Limited Edition", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Bonus Track Version", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Remastered", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Extended", StringComparison.OrdinalIgnoreCase);
    }

    private static string? CleanAlbumSummary(string? rawSummary)
    {
        return CleanAlbumText(rawSummary, preserveParagraphs: false);
    }

    private static string? CleanAlbumContent(string? rawContent)
    {
        return CleanAlbumText(rawContent, preserveParagraphs: true);
    }

    private static string? CleanAlbumText(string? rawText, bool preserveParagraphs)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        var decoded = WebUtility.HtmlDecode(rawText);
        decoded = LastFmReadMoreRegex.Replace(decoded, string.Empty);
        decoded = LastFmLicenseRegex.Replace(decoded, string.Empty);
        string cleaned;

        if (!preserveParagraphs)
        {
            var withoutTags = HtmlTagRegex.Replace(decoded, " ");
            cleaned = MultiWhitespaceRegex.Replace(withoutTags, " ").Trim();
        }
        else
        {
            var lineBreakNormalized = decoded.Replace("\r\n", "\n").Replace('\r', '\n');
            lineBreakNormalized = HtmlLineBreakRegex.Replace(lineBreakNormalized, "\n");
            lineBreakNormalized = HtmlTagRegex.Replace(lineBreakNormalized, string.Empty);

            var inputLines = lineBreakNormalized.Split('\n');
            var outputLines = new List<string>(inputLines.Length);
            foreach (var line in inputLines)
            {
                var normalized = MultiWhitespaceRegex.Replace(line, " ").Trim();
                if (normalized.Length == 0)
                {
                    if (outputLines.Count > 0 && outputLines[^1].Length > 0)
                        outputLines.Add(string.Empty);
                    continue;
                }

                outputLines.Add(normalized);
            }

            while (outputLines.Count > 0 && outputLines[^1].Length == 0)
                outputLines.RemoveAt(outputLines.Count - 1);

            cleaned = string.Join("\n", outputLines);
            cleaned = BlankLineRegex.Replace(cleaned, "\n\n");
        }

        cleaned = LastFmReadMoreRegex.Replace(cleaned, string.Empty);
        cleaned = LastFmLicenseRegex.Replace(cleaned, string.Empty);

        if (!preserveParagraphs)
        {
            cleaned = MultiWhitespaceRegex.Replace(cleaned, " ").Trim();
        }
        else
        {
            var lines = cleaned
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(l => l.TrimEnd())
                .ToList();

            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);

            cleaned = string.Join("\n", lines);
            cleaned = BlankLineRegex.Replace(cleaned, "\n\n");
            cleaned = cleaned.Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? SelectDescription(AlbumDescriptionCacheEntry entry, bool preferFullText)
    {
        var first = preferFullText ? entry.FullContent : entry.Summary;
        var second = preferFullText ? entry.Summary : entry.FullContent;

        if (!string.IsNullOrWhiteSpace(first))
            return first;
        if (!string.IsNullOrWhiteSpace(second))
            return second;
        return null;
    }

    private async Task<string?> GetTokenAsync()
    {
        var parameters = new SortedDictionary<string, string>
        {
            { "method", "auth.getToken" },
            { "api_key", ApiKey }
        };

        var sig = GenerateSignature(parameters);
        parameters["api_sig"] = sig;
        parameters["format"] = "json";

        var url = BuildUrl(parameters);
        var response = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(response);

        if (doc.RootElement.TryGetProperty("token", out var token))
            return token.GetString();

        return null;
    }

    private async Task<string?> GetAuthenticatedUsernameAsync()
    {
        var parameters = new SortedDictionary<string, string>
        {
            { "method", "user.getInfo" },
            { "api_key", ApiKey },
            { "sk", _sessionKey! }
        };

        var sig = GenerateSignature(parameters);
        parameters["api_sig"] = sig;
        parameters["format"] = "json";

        var url = BuildUrl(parameters);
        var response = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(response);

        if (doc.RootElement.TryGetProperty("user", out var user) &&
            user.TryGetProperty("name", out var name))
            return name.GetString();

        return null;
    }

    private static string GenerateSignature(SortedDictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        foreach (var kvp in parameters)
            sb.Append(kvp.Key).Append(kvp.Value);
        sb.Append(ApiSecret);

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = MD5.HashData(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static string BuildUrl(SortedDictionary<string, string> parameters)
    {
        var sb = new StringBuilder(ApiBase).Append('?');
        foreach (var kvp in parameters)
            sb.Append(Uri.EscapeDataString(kvp.Key)).Append('=').Append(Uri.EscapeDataString(kvp.Value)).Append('&');
        return sb.ToString().TrimEnd('&');
    }

    private sealed class AlbumDescriptionCacheEntry
    {
        public string Summary { get; set; } = string.Empty;
        public string FullContent { get; set; } = string.Empty;
        public string? UserOverride { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class AlbumDescriptionPayload
    {
        public string Summary { get; set; } = string.Empty;
        public string FullContent { get; set; } = string.Empty;
    }
}
