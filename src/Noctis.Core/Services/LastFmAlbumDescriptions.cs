using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>Last.fm's public API identity, shared by the desktop's LastFmService (sign-in,
/// scrobbling) and <see cref="LastFmAlbumDescriptions"/>, which both apps use.</summary>
internal static class LastFmApi
{
    internal const string ApiKey = "7b625c5a18197cf284aabf8b66505156";
    internal const string ApiBase = "https://ws.audioscrobbler.com/2.0/";
}

/// <summary>
/// Album descriptions from Last.fm's album.getinfo: the wiki summary and full text, cleaned of
/// HTML and Last.fm's "Read more" link and licence line, cached in a JSON file keyed
/// "artist::album" (trimmed, lower-case) beside the user's own override. Moved out of the
/// desktop's LastFmService (which keeps its public API and forwards here) so the phone looks
/// descriptions up the same way and reads the same cache format.
/// </summary>
public sealed class LastFmAlbumDescriptions
{
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly bool _rememberFailedLookups;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex HtmlLineBreakRegex = new("<\\s*(br|/p|/div|/li|/h[1-6])\\s*/?>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MultiWhitespaceRegex = new("\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LastFmReadMoreRegex = new("Read\\s+more\\s+on\\s+Last\\.fm\\s*\\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LastFmLicenseRegex = new("User-?contributed\\s+text\\s+is\\s+available\\s+under\\s+the\\s+Creative\\s+Commons\\s+By-?SA\\s+License;?\\s*additional\\s+terms\\s+may\\s+apply\\s*\\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlankLineRegex = new("\\n{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // "…Apple Music. ." — Last.fm puts the sentence period OUTSIDE the "Read more" anchor, so
    // once the anchor text is removed a lone period trails the real one. Collapse it.
    private static readonly Regex OrphanPeriodRegex = new("(?<=[.!?…])\\s+\\.(?=\\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <param name="http">Client for the album.getinfo GETs; the caller owns its timeout policy.</param>
    /// <param name="cachePath">The JSON cache file (the desktop's is cache/lastfm_album_descriptions.json under the data root).</param>
    /// <param name="rememberFailedLookups">
    /// Whether a lookup that never got an answer (no network, a timeout, a server error) is
    /// cached as "no description" like one Last.fm answered without a wiki. The desktop always
    /// has (true); the phone, often offline, passes false so it asks again on a later visit.
    /// </param>
    public LastFmAlbumDescriptions(HttpClient http, string cachePath, bool rememberFailedLookups = true)
    {
        _http = http;
        _cachePath = cachePath;
        _rememberFailedLookups = rememberFailedLookups;
    }

    /// <summary>
    /// The album's description: the user's override when set, else the cached Last.fm text,
    /// else a fresh lookup (at most one per album every 20 s). <paramref name="preferFullText"/>
    /// picks the full wiki text over the summary, falling back to the other one. Null when
    /// there is none. Cancelling throws <see cref="OperationCanceledException"/> and caches nothing.
    /// </summary>
    public async Task<string?> GetAsync(string artistName, string albumName, bool preferFullText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumName))
            return null;

        var cacheKey = BuildCacheKey(artistName, albumName);
        await EnsureCacheLoadedAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
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
            if (_cooldownUntil.TryGetValue(cacheKey, out var cooldownUntil) && now < cooldownUntil)
                return null;

            _cooldownUntil[cacheKey] = now.Add(Cooldown);
        }
        finally
        {
            _lock.Release();
        }

        var (fetched, answered) = await FetchFromApiAsync(artistName, albumName, ct);

        await _lock.WaitAsync(ct);
        try
        {
            var userOverride = _cache.TryGetValue(cacheKey, out var existingEntry)
                ? existingEntry.UserOverride
                : null;

            if (!answered && !_rememberFailedLookups)
                return userOverride;

            _cache[cacheKey] = new CacheEntry
            {
                Summary = fetched?.Summary ?? string.Empty,
                FullContent = fetched?.FullContent ?? string.Empty,
                UserOverride = userOverride,
                UpdatedUtc = DateTime.UtcNow
            };
            await SaveCacheUnsafeAsync(ct);
            if (userOverride != null)
                return userOverride;

            return fetched == null
                ? null
                : (preferFullText ? fetched.FullContent : fetched.Summary);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Stores the user's own description for the album (cleaned like Last.fm's full
    /// text); null removes it, so the Last.fm text shows again.</summary>
    public async Task SetOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumName))
            return;

        var cacheKey = BuildCacheKey(artistName, albumName);
        await EnsureCacheLoadedAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(cacheKey, out var entry))
            {
                entry = new CacheEntry();
                _cache[cacheKey] = entry;
            }

            entry.UserOverride = description == null
                ? null
                : CleanAlbumContent(description) ?? string.Empty;
            entry.UpdatedUtc = DateTime.UtcNow;
            await SaveCacheUnsafeAsync(ct);
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

            if (!File.Exists(_cachePath))
            {
                _cacheLoaded = true;
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_cachePath, ct);
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                if (entries != null)
                {
                    foreach (var kvp in entries)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;
                        // Entries written by builds that stranded ". ." are repaired in place.
                        kvp.Value.Summary = ScrubOrphanPeriods(kvp.Value.Summary);
                        kvp.Value.FullContent = ScrubOrphanPeriods(kvp.Value.FullContent);
                        _cache[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LastFm] Failed to read album description cache: {ex.Message}");
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
            Debug.WriteLine($"[LastFm] Failed to write album description cache: {ex.Message}");
        }
    }

    /// <summary>The first name candidate Last.fm has a wiki for; <c>Answered</c> is false when a
    /// candidate went unanswered (see the constructor's rememberFailedLookups).</summary>
    private async Task<(Payload? Payload, bool Answered)> FetchFromApiAsync(string artistName, string albumName, CancellationToken ct)
    {
        // Try the exact name, then progressively stripped variants
        // (Deluxe / Video Deluxe / Anniversary Edition / etc.) so release-variant
        // albums inherit the base release's description instead of being blank.
        var answered = true;
        foreach (var candidate in BuildAlbumNameCandidates(albumName))
        {
            var (payload, candidateAnswered) = await FetchForExactNameAsync(artistName, candidate, ct);
            if (payload != null) return (payload, true);
            answered &= candidateAnswered;
            if (ct.IsCancellationRequested) return (null, false);
        }
        return (null, answered);
    }

    private async Task<(Payload? Payload, bool Answered)> FetchForExactNameAsync(string artistName, string albumName, CancellationToken ct)
    {
        try
        {
            var url = $"{LastFmApi.ApiBase}?method=album.getinfo&api_key={LastFmApi.ApiKey}&artist={Uri.EscapeDataString(artistName)}&album={Uri.EscapeDataString(albumName)}&format=json";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                // A server error or rate limit is no answer; any other refusal is one.
                return (null, (int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests);

            var payload = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
            using var doc = JsonDocument.Parse(payload);

            if (doc.RootElement.TryGetProperty("error", out _))
                return (null, true);

            if (!doc.RootElement.TryGetProperty("album", out var album))
                return (null, true);

            if (!album.TryGetProperty("wiki", out var wiki))
                return (null, true);

            var summary = wiki.TryGetProperty("summary", out var summaryNode)
                ? CleanAlbumSummary(summaryNode.GetString())
                : null;
            var fullContent = wiki.TryGetProperty("content", out var contentNode)
                ? CleanAlbumContent(contentNode.GetString())
                : null;

            // Keep graceful fallback if only one field is populated.
            if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(fullContent))
                return (null, true);

            summary ??= fullContent ?? string.Empty;
            fullContent ??= summary;

            return (new Payload
            {
                Summary = summary,
                FullContent = fullContent
            }, true);
        }
        catch (OperationCanceledException)
        {
            // The caller's cancellation and HttpClient's own timeout alike: nothing is cached.
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Album description fetch failed for '{artistName} - {albumName}': {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>The cache key: "artist::album", trimmed and lower-cased.</summary>
    internal static string BuildCacheKey(string artistName, string albumName)
    {
        return $"{artistName.Trim().ToLowerInvariant()}::{albumName.Trim().ToLowerInvariant()}";
    }

    // Last.fm only catalogs base releases; "(Deluxe)" / "[Video Deluxe]" / etc. variants
    // miss otherwise. Generate progressively stripped candidates so a deluxe edition
    // can inherit its base release's description.
    internal static IEnumerable<string> BuildAlbumNameCandidates(string albumName)
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

    /// <summary>The wiki summary as one paragraph, without tags or Last.fm's footer.</summary>
    public static string? CleanAlbumSummary(string? rawSummary)
    {
        return CleanAlbumText(rawSummary, preserveParagraphs: false);
    }

    /// <summary>The full wiki text with its paragraphs kept, without tags or Last.fm's footer.</summary>
    public static string? CleanAlbumContent(string? rawContent)
    {
        return CleanAlbumText(rawContent, preserveParagraphs: true);
    }

    /// <summary>Repairs text cleaned by an older build (the orphan ". ." was cached on disk).</summary>
    public static string? ScrubOrphanPeriods(string? text)
        => string.IsNullOrEmpty(text) ? text : OrphanPeriodRegex.Replace(text, string.Empty);

    private static string? CleanAlbumText(string? rawText, bool preserveParagraphs)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        // Tags come off first: the "Read more" phrase sits inside an <a>, and stripping the
        // phrase while the anchor is still there strands the sentence period after it.
        var decoded = WebUtility.HtmlDecode(rawText);
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
        cleaned = OrphanPeriodRegex.Replace(cleaned, string.Empty);

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

    private static string? SelectDescription(CacheEntry entry, bool preferFullText)
    {
        var first = preferFullText ? entry.FullContent : entry.Summary;
        var second = preferFullText ? entry.Summary : entry.FullContent;

        if (!string.IsNullOrWhiteSpace(first))
            return first;
        if (!string.IsNullOrWhiteSpace(second))
            return second;
        return null;
    }

    private sealed class CacheEntry
    {
        public string Summary { get; set; } = string.Empty;
        public string FullContent { get; set; } = string.Empty;
        public string? UserOverride { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class Payload
    {
        public string Summary { get; set; } = string.Empty;
        public string FullContent { get; set; } = string.Empty;
    }
}
