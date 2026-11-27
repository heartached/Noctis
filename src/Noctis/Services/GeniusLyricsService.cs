using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Services;

/// <summary>
/// Plain lyrics from Genius (GitHub #265): the place rarer artists' words are often written down
/// when no synced source has them, ready to time in Lyrics Studio. Keyless, the way other players
/// read it: <c>genius.com/api/search/song?q=</c> (public JSON, checked live 2026-11-25) for the
/// hits, the best one validated against the song like every search hit, then the song's page,
/// whose <c>data-lyrics-container</c> blocks hold the words. Never synced — Genius has no
/// timings. Only genius.com pages are fetched; a hit or redirect pointing elsewhere is a miss.
/// </summary>
public partial class GeniusLyricsService : IGeniusLyricsService
{
    private const string SearchUrl = "https://genius.com/api/search/song";
    private const string Host = "genius.com";
    private const string UserAgent = "Noctis (https://github.com/heartached/Noctis)";
    internal const string ProviderName = "Genius";
    internal const int MaxCacheEntries = 256;

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public GeniusLyricsService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    /// <summary>
    /// The client Genius is asked with: on Windows one going through WinHTTP, because genius.com's
    /// Cloudflare screen answers every request from .NET's Windows TLS stack with a 403 challenge
    /// (live 2026-11-25; WinHTTP, curl, and .NET on Linux got 200). Null elsewhere: use the shared one.
    /// </summary>
    public static HttpClient? CreateHttpClient() => OperatingSystem.IsWindows()
        ? new HttpClient(new WinHttpGetHandler()) { Timeout = TimeSpan.FromSeconds(20) }
        : null;

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        var cacheKey = $"genius:{artist.Length}:{artist}|{trackName}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var title = MatchText.StripFeaturing(trackName);
            if (title.Length == 0) title = trackName.Trim();
            var query = LyricsSearchSelector.IsUnknownArtist(artist)
                ? title
                : $"{LyricsSearchSelector.PrimaryArtist(artist)} {title}";
            var json = await GetAsync($"{SearchUrl}?q={Uri.EscapeDataString(query)}&per_page=10", "application/json", ct);
            var hit = json is null ? null : PickHit(ParseHits(json), artist, trackName);
            LrcLibResult? result = null;
            if (hit is not null && await GetAsync(hit.Url!, "text/html", ct) is { } html
                && ExtractLyrics(html) is { } lyrics
                && !LyricsSearchSelector.IsInstrumentalPlaceholder(lyrics))
            {
                result = new LrcLibResult
                {
                    TrackName = hit.Title,
                    ArtistName = hit.ArtistNames,
                    PlainLyrics = lyrics,
                };
            }
            Remember(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            // Network failure, 429/503 (busy), 5xx, timeout, oversized or malformed answer.
            // Uncached so a later attempt can succeed.
            throw new LyricsProviderException(ProviderName, ex);
        }
    }

    /// <summary>GETs a genius.com URL; null on 404 or when a redirect left genius.com.</summary>
    private async Task<string?> GetAsync(string url, string accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.RequestMessage?.RequestUri is { } final && !IsGeniusUrl(final)) return null;
        HttpSafety.EnsureSuccess(response);
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    /// <summary>An https URL on genius.com itself — the only pages this service fetches.</summary>
    internal static bool IsGeniusUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);

    internal sealed record Hit(string? Title, string? ArtistNames, string? Url, bool Instrumental);

    /// <summary>The song hits of a /api/search/song answer, in Genius' order.</summary>
    internal static IReadOnlyList<Hit> ParseHits(string json)
    {
        var answer = JsonSerializer.Deserialize<SearchAnswer>(json);
        return answer?.Response?.Sections?
                   .Where(s => s.Hits is not null)
                   .SelectMany(s => s.Hits!)
                   .Where(h => h.Type == "song" && h.Result is not null)
                   .Select(h => new Hit(h.Result!.Title, h.Result.ArtistNames ?? h.Result.PrimaryArtist?.Name,
                       h.Result.Url, h.Result.Instrumental))
                   .ToList()
               ?? (IReadOnlyList<Hit>)Array.Empty<Hit>();
    }

    /// <summary>
    /// The hit that is this song: it must validate like every search hit (title, and artist when
    /// known — a "Genius Brasil Traduções" translation page does not), then the closest title and
    /// artist win, and a hit with the same version markers ("Live", "Remix") as the song beats one
    /// without, so "Jóga" is not answered with "Jóga (Live …)". Ties keep Genius' order.
    /// </summary>
    internal static Hit? PickHit(IEnumerable<Hit> hits, string? artist, string? title)
    {
        var markers = MatchText.AnalyzeTitle(title).Markers;
        var knownArtist = !LyricsSearchSelector.IsUnknownArtist(artist);
        Hit? best = null;
        var bestScore = double.MinValue;
        foreach (var hit in hits)
        {
            if (hit.Instrumental) continue;
            if (hit.Url is null || !Uri.TryCreate(hit.Url, UriKind.Absolute, out var uri) || !IsGeniusUrl(uri)) continue;
            var candidate = new LrcLibResult { TrackName = hit.Title, ArtistName = hit.ArtistNames };
            if (!LyricsSearchSelector.MatchesTrack(candidate, artist, title, 0)) continue;

            var score = MatchText.TitleSimilarity(title, hit.Title)
                        + (knownArtist ? MatchText.ArtistSimilarity(artist, hit.ArtistNames) : 0)
                        + (MatchText.AnalyzeTitle(hit.Title).Markers.SetEquals(markers) ? 1 : 0);
            if (score > bestScore)
            {
                best = hit;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// The lyrics of a Genius song page: the text of every <c>data-lyrics-container="true"</c>
    /// block, without what Genius marks as not lyrics (<c>data-exclude-from-selection</c>: the
    /// "Contributors / Translations / Read More" header, ads). Line breaks kept, tags dropped,
    /// entities decoded. Section labels ("[Chorus]", "[Verse 1: …]") are dropped: no other source
    /// sends them, and Lyrics Studio would try to hear them sung. One blank line between stanzas.
    /// Null when the page has no lyrics (an unreleased or instrumental song).
    /// </summary>
    internal static string? ExtractLyrics(string html)
    {
        var parts = new StringBuilder();
        var at = 0;
        while ((at = html.IndexOf("data-lyrics-container=\"true\"", at, StringComparison.Ordinal)) >= 0)
        {
            var open = html.LastIndexOf('<', at);
            if (open < 0) break;
            var (innerStart, innerEnd, end) = Element(html, open);
            if (end < 0) break;
            parts.Append(WithoutExcluded(html[innerStart..innerEnd])).Append("<br/>");
            at = end;
        }
        if (parts.Length == 0) return null;

        var text = LineBreak().Replace(parts.ToString(), "\n");
        text = Tag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace(' ', ' ').Trim();
            if (SectionLabel().IsMatch(line)) line = string.Empty;
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count > 0 ? string.Join('\n', lines) : null;
    }

    /// <summary>The markup with every element Genius marks <c>data-exclude-from-selection="true"</c> cut out.</summary>
    private static string WithoutExcluded(string markup)
    {
        int at;
        while ((at = markup.IndexOf("data-exclude-from-selection=\"true\"", StringComparison.Ordinal)) >= 0)
        {
            var open = markup.LastIndexOf('<', at);
            if (open < 0) return markup;
            var (_, _, end) = Element(markup, open);
            markup = end < 0 ? markup[..open] : markup[..open] + markup[end..];
        }
        return markup;
    }

    /// <summary>
    /// The element whose start tag begins at <paramref name="open"/>: where its content starts and
    /// ends and where the element ends, counting nested elements of the same name. End -1 when it
    /// is never closed.
    /// </summary>
    private static (int InnerStart, int InnerEnd, int End) Element(string html, int open)
    {
        var nameMatch = TagName().Match(html, open);
        var tagEnd = html.IndexOf('>', open);
        if (!nameMatch.Success || nameMatch.Index != open || tagEnd < 0) return (0, 0, -1);
        var name = nameMatch.Groups[1].Value;
        if (html[tagEnd - 1] == '/') return (tagEnd + 1, tagEnd + 1, tagEnd + 1);

        var depth = 1;
        var pattern = new Regex($@"<(/?){Regex.Escape(name)}\b[^>]*>", RegexOptions.IgnoreCase);
        for (var m = pattern.Match(html, tagEnd + 1); m.Success; m = m.NextMatch())
        {
            if (m.Groups[1].Length > 0)
            {
                if (--depth == 0) return (tagEnd + 1, m.Index, m.Index + m.Length);
            }
            else if (!m.Value.EndsWith("/>", StringComparison.Ordinal))
            {
                depth++;
            }
        }
        return (0, 0, -1);
    }

    [GeneratedRegex(@"<([a-zA-Z][a-zA-Z0-9]*)")]
    private static partial Regex TagName();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    /// <summary>A whole line in brackets: "[Chorus]", "[Verse 2: Björk]", "[Instrumental Break]".</summary>
    [GeneratedRegex(@"^\[[^\]\n]*\]$")]
    private static partial Regex SectionLabel();

    private sealed class SearchAnswer
    {
        [JsonPropertyName("response")] public SearchResponse? Response { get; set; }
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("sections")] public List<Section>? Sections { get; set; }
    }

    private sealed class Section
    {
        [JsonPropertyName("hits")] public List<SearchHit>? Hits { get; set; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("result")] public Song? Result { get; set; }
    }

    private sealed class Song
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("artist_names")] public string? ArtistNames { get; set; }
        [JsonPropertyName("primary_artist")] public Artist? PrimaryArtist { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
    }

    private sealed class Artist
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
