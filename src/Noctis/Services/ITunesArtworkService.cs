using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Looks up album artwork and (when available) animated cover videos through
/// Apple's public iTunes Search API + the public Apple Music web page. No
/// developer token is required; everything used here is part of the same
/// surface area as bendodson.com/projects/itunes-artwork-finder.
/// </summary>
public sealed class ITunesArtworkService
{
    private const string SearchUrl = "https://itunes.apple.com/search";
    private const string LookupUrl = "https://itunes.apple.com/lookup";
    private const string AppleMusicSearchUrl = "https://music.apple.com/us/search";
    private const string AppleMusicHtmlUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    // Apple Music embeds the animated cover in the album page's inline JSON, under
    // videoArtwork / tallVideoArtwork:
    //   "videoArtwork":{"dictionary":{"motionDetailSquare":{…,"video":"https://mvod….m3u8"}}}
    // The key was "videoUrl" when this was written and is "video" now, so the old pattern
    // matched nothing at all and every result was coming from the host-only sweep below.
    // Accept both spellings, and .m3u8 or .mp4, to survive the next rename.
    private static readonly Regex AnimatedUrlRegex = new(
        "\"video(?:Url)?\"\\s*:\\s*\"(?<u>https?:[^\"]+?\\.(?:m3u8|mp4)[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The rendered element that carries the same stream. Apple has already renamed the JSON
    // key above once, and when it did, everything fell through to the host-only sweep — which
    // cannot tell a cover loop from a music-video preview. This is precise, so it sits between
    // the two. Route confirmed by Ben Dodson, who maintains the Apple Music Artwork Finder.
    private static readonly Regex AmbientVideoRegex = new(
        "<amp-ambient-video\\b[^>]*\\bsrc=\"(?<u>[^\"]+?\\.m3u8[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnimatedUrlFallbackRegex = new(
        "(https?:[^\"\\\\\\s]+?\\.(?:m3u8|mp4))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;

    public ITunesArtworkService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>One iTunes search hit, with both a thumbnail and a hi-res URL.</summary>
    public sealed record ArtworkCandidate(
        long CollectionId,
        string CollectionName,
        string ArtistName,
        string ThumbUrl,
        string StandardUrl,
        string HiResUrl,
        string ViewUrl);

    public sealed record AnimatedArtworkVariant(
        string Label,
        string Url,
        int Width,
        int Height,
        string Codec,
        long Bandwidth,
        bool IsHls);

    /// <summary>
    /// Searches Apple's catalogue for album artwork, using the same iTunes Search API
    /// surface as the iTunes Artwork Finder. When the tag names an artist, the query
    /// says so — a title-only "7" surfaces every album in the store called "7" — and
    /// the old title-only search stays on as the fallback when the enriched term finds
    /// nothing, so a mistagged artist never costs results the title alone would find.
    /// </summary>
    public async Task<IReadOnlyList<ArtworkCandidate>> SearchAlbumsAsync(
        string artist, string album, int limit = 8, CancellationToken ct = default)
    {
        var albumTerm = (album ?? string.Empty).Trim();
        var artistTerm = (artist ?? string.Empty).Trim();
        if (albumTerm.Length == 0 && artistTerm.Length == 0)
            return Array.Empty<ArtworkCandidate>();

        try
        {
            var candidates = new Dictionary<long, ArtworkCandidate>();

            var enrichedTerm = BuildAlbumSearchTerm(artistTerm, albumTerm);
            if (!string.Equals(enrichedTerm, albumTerm, StringComparison.OrdinalIgnoreCase))
                await AddSearchResultsAsync(candidates, enrichedTerm, limit * 3, albumAttributeOnly: false, ct);

            // Title-only search: the whole query when no artist is known, and the
            // fallback when the enriched term came back empty. Gated on zero results,
            // so the ordinary artist-tagged case stays a single request.
            if (candidates.Count == 0 && albumTerm.Length > 0)
                await AddSearchResultsAsync(candidates, albumTerm, limit * 3, albumAttributeOnly: true, ct);

            return candidates.Values
                .OrderBy(c => RankAlbumCandidate(c, albumTerm, artistTerm))
                .ThenBy(c => c.CollectionName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[iTunes] search failed: {ex.Message}");
            return Array.Empty<ArtworkCandidate>();
        }
    }

    /// <summary>
    /// Free-text term for the artist-enriched album searches. Only the primary credited
    /// artist goes in: /search AND-matches every word of the term, and a multi-artist tag
    /// string ("Lil Nas X feat. Billy Ray Cyrus") carries words the store's album entry
    /// may not, which pushes recall to zero — the reported symptom was a grid of unrelated
    /// albums that merely shared the title "7". The album text is the user's tag and is
    /// passed through untouched. Internal for tests (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal static string BuildAlbumSearchTerm(string? artist, string? album)
        => $"{Track.GetPrimaryArtist(artist)} {(album ?? string.Empty).Trim()}".Trim();

    // Animated covers are short video loops; generous cap so a hostile response
    // still can't fill memory, while 1080p variants (~60 MB) pass untouched.
    public const long MaxAnimatedCoverBytes = 256L * 1024 * 1024;

    /// <summary>Downloads the bytes at <paramref name="url"/>; returns null on failure or oversize.</summary>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default,
        long maxBytes = HttpSafety.MaxImageBytes)
    {
        try
        {
            // Precautionary, not a reproduced fix: Apple's CDN serves these byte-identically
            // under the default agent today (checked on every hop). Ben Dodson reports having
            // seen the videos fail outside Safari, so the browser agent the page fetch already
            // uses is carried through to the playlists and the media itself.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(AppleMusicHtmlUserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await HttpSafety.ReadBytesBoundedAsync(resp.Content, maxBytes, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[iTunes] download failed: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<AnimatedArtworkVariant>> SearchAnimatedArtworkVariantsAsync(
        string albumViewUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumViewUrl))
            return Array.Empty<AnimatedArtworkVariant>();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, albumViewUrl);
            req.Headers.UserAgent.ParseAdd(AppleMusicHtmlUserAgent);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<AnimatedArtworkVariant>();

            var html = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            var mediaUrls = ExtractAnimatedMediaUrls(html, albumViewUrl);
            var variants = new List<AnimatedArtworkVariant>();

            foreach (var mediaUrl in mediaUrls)
            {
                if (mediaUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    variants.Add(new AnimatedArtworkVariant(
                        "Animated Artwork (MP4)",
                        mediaUrl,
                        0,
                        0,
                        "mp4",
                        0,
                        false));
                    continue;
                }

                if (!mediaUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    continue;

                variants.AddRange(await ParseHlsMasterVariantsAsync(mediaUrl, ct));
            }

            var deduped = variants
                .GroupBy(v => $"{v.Width}x{v.Height}:{GetCodecFamily(v.Codec)}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(v => v.Bandwidth).First())
                .ToList();

            return PickTierVariants(deduped);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[iTunes] animated lookup failed: {ex.Message}");
            return Array.Empty<AnimatedArtworkVariant>();
        }
    }

    public async Task<string?> FindAnimatedCoverUrlAsync(string albumViewUrl, CancellationToken ct = default)
        => (await SearchAnimatedArtworkVariantsAsync(albumViewUrl, ct)).FirstOrDefault()?.Url;

    public async Task<bool> DownloadHlsVariantAsMp4Async(
        AnimatedArtworkVariant variant,
        string destinationPath,
        CancellationToken ct = default)
    {
        if (!variant.IsHls)
        {
            var data = await DownloadAsync(variant.Url, ct, MaxAnimatedCoverBytes);
            if (data is null or { Length: 0 })
                return false;

            await File.WriteAllBytesAsync(destinationPath, data, ct);
            return true;
        }

        try
        {
            var playlist = await GetTextAsync(variant.Url, ct);
            if (string.IsNullOrWhiteSpace(playlist))
                return false;

            var baseUri = new Uri(variant.Url);
            var parts = new List<Uri>();

            foreach (var rawLine in playlist.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
                {
                    var mapUri = ParseAttribute(line, "URI");
                    if (!string.IsNullOrWhiteSpace(mapUri))
                        parts.Add(new Uri(baseUri, mapUri));
                    continue;
                }

                if (!line.StartsWith("#", StringComparison.Ordinal))
                    parts.Add(new Uri(baseUri, line));
            }

            if (parts.Count == 0)
                return false;

            await using var output = File.Create(destinationPath);
            foreach (var part in parts.DistinctBy(p => p.ToString()))
            {
                // A playlist line may carry an absolute URI to any host; a segment
                // off the Apple allowlist means a hostile or broken playlist, and
                // concatenating the remaining parts would be garbage anyway.
                if (!IsAppleMediaHost(part.ToString()))
                    return false;

                // Animated-cover cap, not the 24MB image cap: Apple often serves the
                // whole loop as ONE fMP4 part (~31MB at 1080p, ~90MB at 2160p), and
                // the image cap silently failed every download over 24MB.
                var data = await DownloadAsync(part.ToString(), ct, MaxAnimatedCoverBytes);
                if (data is null or { Length: 0 })
                    return false;

                await output.WriteAsync(data, ct);
            }

            return new FileInfo(destinationPath).Length > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[iTunes] HLS download failed: {ex.Message}");
            return false;
        }
    }

    private static string CleanUrl(string u) => u.Replace("\\u002F", "/").Replace("\\/", "/");

    /// <summary>
    /// Resolves a single album by Apple/iTunes collection ID via the public /lookup
    /// endpoint. Used as a manual fallback when /search fails to surface an album
    /// (some catalog entries are reachable by ID but not by free-text search).
    /// </summary>
    public async Task<ArtworkCandidate?> LookupAlbumByIdAsync(long collectionId, CancellationToken ct = default)
    {
        if (collectionId <= 0) return null;

        try
        {
            var url = $"{LookupUrl}?id={collectionId}&country=us";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in results.EnumerateArray())
            {
                if (TryReadCandidate(item, out var candidate))
                    return candidate;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[iTunes] lookup failed: {ex.Message}");
        }

        return null;
    }

    // Album links on the Apple Music web pages: /<storefront>/album/<slug>/<id>. Track links
    // on a search page carry their album's id too, so the same id repeats and order matters —
    // the page lists them by relevance.
    private static readonly Regex AppleMusicAlbumLinkRegex = new(
        @"/[a-z]{2}/album/[^""'/\s]+/(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Searches the Apple Music catalogue through the public web search page, then resolves
    /// the albums it links to by ID.
    ///
    /// This exists because the iTunes Search API's index is not the Apple Music catalogue and
    /// has holes in it: "YHLQMDLG" (Bad Bunny) returns exactly one hit there, a cover act's
    /// record, so an album Apple serves an animated cover for looked like a miss. The web
    /// search page is server-rendered and ranks the real album first. Both surfaces used here
    /// (the page, and /lookup) are ones this class already talks to.
    /// </summary>
    public async Task<IReadOnlyList<ArtworkCandidate>> SearchAppleMusicAlbumsAsync(
        string artist, string album, int limit = 6, CancellationToken ct = default)
    {
        // Same primary-artist policy as SearchAlbumsAsync: the featured-artist tail of a
        // multi-artist tag only muddies the page's ranking of the album we want.
        var term = BuildAlbumSearchTerm(artist, album);
        if (term.Length == 0)
            return Array.Empty<ArtworkCandidate>();

        try
        {
            var url = $"{AppleMusicSearchUrl}?term={Uri.EscapeDataString(term)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Apple serves the crawlable, server-rendered markup to a browser UA.
            req.Headers.UserAgent.ParseAdd(AppleMusicHtmlUserAgent);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<ArtworkCandidate>();

            var html = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            var ids = ExtractAppleMusicAlbumIds(html).Take(Math.Max(1, limit)).ToList();
            if (ids.Count == 0)
                return Array.Empty<ArtworkCandidate>();

            // The page gives IDs but no dependable title/artist text, and the caller has to
            // check both — so let /lookup name them authoritatively, in one request.
            return await LookupAlbumsByIdAsync(ids, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[iTunes] Apple Music search failed: {ex.Message}");
            return Array.Empty<ArtworkCandidate>();
        }
    }

    internal static IReadOnlyList<long> ExtractAppleMusicAlbumIds(string html)
    {
        var ids = new List<long>();
        var seen = new HashSet<long>();

        foreach (Match m in AppleMusicAlbumLinkRegex.Matches(html))
        {
            if (long.TryParse(m.Groups["id"].Value, out var id) && seen.Add(id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// Resolves several albums in one /lookup call, preserving the order the IDs came in —
    /// that order is the search ranking, and the endpoint does not honour it.
    /// </summary>
    public async Task<IReadOnlyList<ArtworkCandidate>> LookupAlbumsByIdAsync(
        IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var wanted = ids.Where(i => i > 0).Distinct().ToList();
        if (wanted.Count == 0)
            return Array.Empty<ArtworkCandidate>();

        try
        {
            var url = $"{LookupUrl}?id={string.Join(",", wanted)}&country=us&entity=album";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<ArtworkCandidate>();

            var json = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return Array.Empty<ArtworkCandidate>();

            var byId = new Dictionary<long, ArtworkCandidate>();
            foreach (var item in results.EnumerateArray())
            {
                if (TryReadCandidate(item, out var candidate))
                    byId[candidate.CollectionId] = candidate;
            }

            return wanted
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[iTunes] batch lookup failed: {ex.Message}");
            return Array.Empty<ArtworkCandidate>();
        }
    }

    /// <summary>Reads one /search or /lookup result row into a candidate.</summary>
    private static bool TryReadCandidate(JsonElement item, out ArtworkCandidate candidate)
    {
        candidate = null!;
        if (!item.TryGetProperty("collectionId", out var idNode) ||
            !idNode.TryGetInt64(out var id))
            return false;

        var name = item.TryGetProperty("collectionName", out var n) ? n.GetString() ?? "" : "";
        var artistName = item.TryGetProperty("artistName", out var a) ? a.GetString() ?? "" : "";
        var artworkUrl = item.TryGetProperty("artworkUrl100", out var t) ? t.GetString() ?? "" : "";
        var viewUrl = item.TryGetProperty("collectionViewUrl", out var v) ? v.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(artworkUrl))
            return false;

        var thumb = RewriteArtworkUrl(artworkUrl, "300x300bb");
        var standard = RewriteArtworkUrl(artworkUrl, "1000x1000bb");
        var hiRes = BuildUncompressedArtworkUrl(artworkUrl) ?? RewriteArtworkUrl(artworkUrl, "100000x100000-999");
        candidate = new ArtworkCandidate(id, name, artistName, thumb, standard, hiRes, viewUrl);
        return true;
    }

    private async Task AddSearchResultsAsync(
        Dictionary<long, ArtworkCandidate> candidates,
        string term,
        int limit,
        bool albumAttributeOnly,
        CancellationToken ct)
    {
        var url = $"{SearchUrl}?term={Uri.EscapeDataString(term)}&media=music&entity=album&country=us&limit={Math.Max(1, limit)}";
        if (albumAttributeOnly)
            url += "&attribute=albumTerm";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return;
        var json = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in results.EnumerateArray())
        {
            if (TryReadCandidate(item, out var candidate) &&
                !candidates.ContainsKey(candidate.CollectionId))
                candidates[candidate.CollectionId] = candidate;
        }
    }

    internal static IReadOnlyList<string> ExtractAnimatedMediaUrls(string html, string? pageUrl = null)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri);

        void Add(string url)
        {
            // The element's src is markup, so it is HTML-encoded, and it is allowed to be
            // relative. Apple serves an absolute one today, but a CDN moving to a
            // protocol-relative "//host/…" is ordinary and would otherwise go silently dark.
            var decoded = WebUtility.HtmlDecode(CleanUrl(url));

            string? resolved = null;
            if (IsAbsoluteWebUrl(decoded))
                resolved = decoded; // verbatim: no Uri round-trip to re-escape it
            else if (pageUri != null &&
                     Uri.TryCreate(pageUri, decoded, out var combined) &&
                     IsAbsoluteWebUrl(combined.ToString()))
                resolved = combined.ToString();

            if (resolved != null && IsAppleMediaHost(resolved) && seen.Add(resolved))
                urls.Add(resolved);
        }

        // Both structured passes are precise, so they are merged rather than ranked: the live
        // page carries the JSON and the element, and dropping either loses a crop.
        foreach (Match m in AnimatedUrlRegex.Matches(html))
            Add(m.Groups["u"].Value);

        foreach (Match m in AmbientVideoRegex.Matches(html))
            Add(m.Groups["u"].Value);

        // Only when the structured entries are missing. This net matches any Apple-hosted
        // stream on the page and cannot tell a cover loop from a music-video preview, so it
        // must never add to a result the structured pass already produced — that is how a
        // trailer would end up offered as an album's animated cover.
        if (urls.Count > 0)
            return urls;

        foreach (Match m in AnimatedUrlFallbackRegex.Matches(html))
            Add(m.Value);

        return urls;
    }

    /// <summary>
    /// True only for absolute http(s). On Windows a protocol-relative "//host/path" parses as
    /// an absolute UNC path, i.e. file://host/path — which would sail through the Apple-host
    /// check below while pointing at something that is not a web URL at all.
    /// </summary>
    private static bool IsAbsoluteWebUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal static bool IsAppleMediaHost(string url)
    {
        // A real host comparison, not a substring of the whole URL: otherwise
        // "https://evil.example/?x=mzstatic.com" or "mzstatic.com.evil.example"
        // would pass the allowlist.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return IsHostOrSubdomain(uri.Host, "mvod.itunes.apple.com") ||
               IsHostOrSubdomain(uri.Host, "mzstatic.com");
    }

    private static bool IsHostOrSubdomain(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<AnimatedArtworkVariant>> ParseHlsMasterVariantsAsync(
        string masterUrl,
        CancellationToken ct)
    {
        var text = await GetTextAsync(masterUrl, ct);
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<AnimatedArtworkVariant>();

        var list = new List<AnimatedArtworkVariant>();
        string? pendingInfo = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingInfo = line;
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (pendingInfo == null)
                continue;

            var url = new Uri(new Uri(masterUrl), line).ToString();
            // An absolute URI in the playlist can point anywhere; keep the
            // variants pinned to the same allowlist as the page extraction.
            if (!IsAppleMediaHost(url))
            {
                pendingInfo = null;
                continue;
            }
            var (width, height) = ParseResolution(pendingInfo);
            var codec = ParseAttribute(pendingInfo, "CODECS");
            var bandwidth = ParseLongAttribute(pendingInfo, "BANDWIDTH");
            var codecFamily = GetCodecFamily(codec);
            var p = Math.Max(width, height);
            var label = p > 0
                ? $"Animated Artwork ({p}p {codecFamily})"
                : $"Animated Artwork ({codecFamily})";

            list.Add(new AnimatedArtworkVariant(label, url, width, height, codec, bandwidth, true));
            pendingInfo = null;
        }

        return PickTierVariants(list);
    }

    // From every parsed HLS variant, keep at most two clean choices the UI can
    // surface: a 1080p tier (max dimension in [900, 1600]) and a 2160p tier
    // (max dimension >= 1900). For each tier, prefer h.265 then highest bandwidth.
    // Labels are normalized so the UI shows "1080p" / "2160p" instead of the raw
    // resolutions Apple ships (1438p, 2216p, 2732p, …).
    private static IReadOnlyList<AnimatedArtworkVariant> PickTierVariants(
        IReadOnlyList<AnimatedArtworkVariant> variants)
    {
        if (variants.Count == 0)
            return Array.Empty<AnimatedArtworkVariant>();

        // Prefer max-dim closest to the tier target (so the "1080p tier" actually
        // gives us ~1080p, not a 1438p portrait crop), then prefer square aspect
        // (the AnimatedCoverImage renders into a square buffer — non-square sources
        // get stretched and look chunky), then h.265, then highest bandwidth.
        AnimatedArtworkVariant? Pick(IEnumerable<AnimatedArtworkVariant> pool, int targetMaxDim)
            => pool
                .OrderBy(v => Math.Abs(Math.Max(v.Width, v.Height) - targetMaxDim))
                .ThenBy(v => v.Width == v.Height ? 0 : 1)
                .ThenBy(v => GetCodecFamily(v.Codec) == "h.265" ? 0 : 1)
                .ThenByDescending(v => v.Bandwidth)
                .FirstOrDefault();

        var picked = new List<AnimatedArtworkVariant>(2);

        var tier1080 = variants.Where(v =>
        {
            var p = Math.Max(v.Width, v.Height);
            return p >= 900 && p < 1900;
        });
        var v1080 = Pick(tier1080, 1080);
        if (v1080 != null)
            picked.Add(v1080 with { Label = "1080p" });

        var tier2160 = variants.Where(v => Math.Max(v.Width, v.Height) >= 1900);
        var v2160 = Pick(tier2160, 2160);
        if (v2160 != null)
            picked.Add(v2160 with { Label = "2160p" });

        if (picked.Count > 0)
            return picked;

        // Nothing reached 1080p — fall back to the single best lower-res variant
        // so the user still sees *something* rather than an empty list.
        var fallback = Pick(variants, 1080);
        return fallback == null
            ? Array.Empty<AnimatedArtworkVariant>()
            : new[] { fallback with { Label = $"{Math.Max(fallback.Width, fallback.Height)}p" } };
    }

    private async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(AppleMusicHtmlUserAgent);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        return await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
    }

    private static (int Width, int Height) ParseResolution(string line)
    {
        var match = Regex.Match(line, @"RESOLUTION=(?<w>\d+)x(?<h>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return (0, 0);

        return (int.Parse(match.Groups["w"].Value), int.Parse(match.Groups["h"].Value));
    }

    private static string ParseAttribute(string line, string name)
    {
        var quoted = Regex.Match(line, $@"{Regex.Escape(name)}=""(?<v>[^""]+)""", RegexOptions.IgnoreCase);
        if (quoted.Success)
            return quoted.Groups["v"].Value;

        var bare = Regex.Match(line, $@"{Regex.Escape(name)}=(?<v>[^,]+)", RegexOptions.IgnoreCase);
        return bare.Success ? bare.Groups["v"].Value : string.Empty;
    }

    private static long ParseLongAttribute(string line, string name)
    {
        var value = ParseAttribute(line, name);
        return long.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static string GetCodecFamily(string codec)
    {
        if (codec.Contains("hvc1", StringComparison.OrdinalIgnoreCase) ||
            codec.Contains("hev1", StringComparison.OrdinalIgnoreCase))
            return "h.265";

        if (codec.Contains("avc1", StringComparison.OrdinalIgnoreCase))
            return "h.264";

        return "video";
    }

    /// <summary>
    /// Whether an iTunes candidate really is the album that was searched for. Ranking alone
    /// only orders candidates — it never rejects one, so a lookup that wants "the animated
    /// cover for THIS album" would otherwise happily accept the best of a bad list.
    /// </summary>
    internal static bool IsLikelySameAlbum(
        string? candidateAlbum, string? candidateArtist, string? album, string? artist)
    {
        var wantedAlbum = NormalizeAlbumForMatch(album);
        var gotAlbum = NormalizeAlbumForMatch(candidateAlbum);
        if (wantedAlbum.Length == 0 || gotAlbum.Length == 0)
            return false;

        // Deliberately exact. A prefix rule reads "1989 (Taylor's Version)" as "1989", and
        // handing someone a re-recording's cover for their original is worse than offering
        // nothing — the manual "paste the Apple Music link" path covers the odd edition.
        if (!string.Equals(gotAlbum, wantedAlbum, StringComparison.Ordinal))
            return false;

        // Same title, different act: karaoke, lullaby and piano-cover records all collide
        // with the real album here, so the artist has to corroborate when we know it.
        var wantedArtist = NormalizeSearchText(artist);
        var gotArtist = NormalizeSearchText(candidateArtist);
        if (wantedArtist.Length == 0 || gotArtist.Length == 0)
            return true;

        return IsLikelySameArtist(candidateArtist, artist);
    }

    /// <summary>
    /// Loose artist comparison — the same rule <see cref="IsLikelySameAlbum"/> corroborates
    /// with, exposed so a title-only match can still *prefer* the artist we know about.
    /// </summary>
    internal static bool IsLikelySameArtist(string? candidateArtist, string? artist)
    {
        var wanted = NormalizeSearchText(artist);
        var got = NormalizeSearchText(candidateArtist);
        if (wanted.Length == 0 || got.Length == 0)
            return false;

        return got.Contains(wanted, StringComparison.Ordinal) ||
               wanted.Contains(got, StringComparison.Ordinal);
    }

    // Edition wrappers a local tag and the store routinely spell differently. Only these are
    // erased: anything else in brackets ("(Taylor's Version)", "(The Til Dawn Edition)") names
    // a distinct release with its own artwork and has to keep the albums apart.
    private static readonly Regex AlbumEditionSuffixRegex = new(
        @"[\(\[]\s*(?:(?:\d{4}\s+)?remaster(?:ed)?|deluxe(?:\s+(?:edition|version))?|" +
        @"expanded(?:\s+edition)?|special\s+edition|extended(?:\s+version)?|" +
        @"bonus\s+track\s+version|video\s+version|explicit(?:\s+version)?|" +
        @"(?:\d+(?:st|nd|rd|th)?\s+)?anniversary(?:\s+edition)?)\s*[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string NormalizeAlbumForMatch(string? value)
        => NormalizeSearchText(AlbumEditionSuffixRegex.Replace(value ?? string.Empty, " "));

    private static int RankAlbumCandidate(ArtworkCandidate candidate, string album, string artist)
    {
        var score = 0;
        var candidateAlbum = NormalizeSearchText(candidate.CollectionName);
        var wantedAlbum = NormalizeSearchText(album);
        var candidateArtist = NormalizeSearchText(candidate.ArtistName);
        var wantedArtist = NormalizeSearchText(artist);

        if (wantedAlbum.Length > 0)
        {
            if (candidateAlbum == wantedAlbum)
                score -= 200;
            else if (candidateAlbum.StartsWith(wantedAlbum, StringComparison.Ordinal))
                score -= 120;
            else if (candidateAlbum.Contains(wantedAlbum, StringComparison.Ordinal))
                score -= 60;
            else
                score += 150;
        }

        if (wantedArtist.Length > 0)
        {
            if (candidateArtist == wantedArtist)
                score -= 80;
            // Symmetric containment: the store's credit ("Lil Nas X") must corroborate
            // the longer multi-artist tag it came from ("Lil Nas X feat. Billy Ray
            // Cyrus"). A one-directional Contains could never rank the real album above
            // the same-titled strangers a title-only search drags in.
            else if (IsLikelySameArtist(candidate.ArtistName, artist))
                score -= 35;
            else
                score += 35;
        }

        return score;
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s*-\s*(single|ep)$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string RewriteArtworkUrl(string url, string sizeSuffix)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var rewritten = Regex.Replace(url, @"\d+x\d+bb\.(jpg|png|jpeg)$", $"{sizeSuffix}.$1",
            RegexOptions.IgnoreCase);
        return rewritten;
    }

    private static string? BuildUncompressedArtworkUrl(string url)
    {
        var hiRes = RewriteArtworkUrl(url, "100000x100000-999");
        if (!Uri.TryCreate(hiRes, UriKind.Absolute, out var uri))
            return null;

        var marker = "/image/thumb/";
        var path = uri.AbsolutePath;
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var assetPath = path[(markerIndex + marker.Length)..];
        var slash = assetPath.LastIndexOf('/');
        if (slash <= 0)
            return null;

        return "https://a5.mzstatic.com/us/r1000/0/" + assetPath[..slash];
    }
}
