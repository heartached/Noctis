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

    // Session-log tag for every stage of the animated-artwork lookup. These failures used to
    // go to Debug.WriteLine, which a release build drops, so a miss left nothing to go on.
    internal const string LogSource = "AnimatedArt";

    private readonly HttpClient _http;

    public ITunesArtworkService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// The largest box Apple's artwork CDN accepts: it serves the master (3000 px, or 4000 for
    /// OK Computer) for any box up to 10000, and answers 400 "Invalid request parameter" above
    /// it. Verified 2026-10-17: 10000x10000-999 → 200, 10001x10001-999 and the old
    /// 100000x100000-999 → 400, and the uncompressed original on a5.mzstatic.com/us/r1000/0/
    /// → 403 for every album tried — so Max fell through to the 300 px thumbnail.
    /// </summary>
    internal const int MaxArtworkBox = 10000;

    /// <summary>One iTunes search hit, with both a thumbnail and a hi-res URL.</summary>
    public sealed record ArtworkCandidate(
        long CollectionId,
        string CollectionName,
        string ArtistName,
        string ThumbUrl,
        string StandardUrl,
        string HiResUrl,
        string ViewUrl)
    {
        /// <summary>
        /// URLs to try for a pick, best first. Max goes to the master at maximum quality, then
        /// the master at the CDN's normal quality (a large master at -999 can pass the 24 MB
        /// image cap), then the standard 1000 px; the thumbnail is the last resort for both,
        /// so a failed Max never lands below Standard.
        /// </summary>
        public IReadOnlyList<string> DownloadUrls(bool highestResolution)
        {
            var urls = highestResolution
                ? new[] { HiResUrl, RewriteArtworkUrl(StandardUrl, $"{MaxArtworkBox}x{MaxArtworkBox}bb"), StandardUrl, ThumbUrl }
                : new[] { StandardUrl, ThumbUrl };
            return urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>The store's explicitness: "explicit", "cleaned" or "notExplicit" (may be empty).</summary>
        public string Explicitness { get; init; } = string.Empty;

        /// <summary>Release year from the store, 0 when it gave none.</summary>
        public int ReleaseYear { get; init; }

        /// <summary>Songs on the store album, 0 when it gave none.</summary>
        public int TrackCount { get; init; }
    }

    public sealed record AnimatedArtworkVariant(
        string Label,
        string Url,
        int Width,
        int Height,
        string Codec,
        long Bandwidth,
        bool IsHls);

    /// <summary>
    /// One album (or playlist) that has motion artwork, as the chooser shows it: what it is, a
    /// still of the loop, and the variants worth offering.
    /// </summary>
    public sealed record AnimatedArtworkAlbum(
        long CollectionId,
        string Title,
        string Artist,
        string PageUrl,
        string? PosterUrl,
        string? CoverThumbUrl,
        IReadOnlyList<AnimatedArtworkVariant> Variants,
        AnimatedArtworkVariant? PreviewVariant,
        bool HasSquare,
        bool HasTall)
    {
        /// <summary>Only the 3:4 portrait loop exists; the offered variants are that crop.</summary>
        public bool IsTallOnly => HasTall && !HasSquare;

        public string Explicitness { get; init; } = string.Empty;
        public int ReleaseYear { get; init; }
    }

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
            DebugLog.Write(LogSource, $"iTunes album search for \"{albumTerm}\" failed: {ex.Message}");
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
    /// <param name="onProgress">Bytes read so far and the length the server declared (0 when it sent none),
    /// from the reading thread (GitHub #185: the chooser pop-ups' download bar).</param>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default,
        long maxBytes = HttpSafety.MaxImageBytes, Action<long, long>? onProgress = null)
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
            if (onProgress is null)
                return await HttpSafety.ReadBytesBoundedAsync(resp.Content, maxBytes, ct);
            using var buffer = new MemoryStream();
            await StreamBodyAsync(resp.Content, buffer, maxBytes, onProgress, ct);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            DebugLog.Write(LogSource, $"download of {url} failed: {ex.Message}");
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
            if (!resp.IsSuccessStatusCode)
            {
                DebugLog.Write(LogSource, $"album page {albumViewUrl} returned HTTP {(int)resp.StatusCode}");
                return Array.Empty<AnimatedArtworkVariant>();
            }

            var html = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            var mediaUrls = ExtractAnimatedMediaUrls(html, albumViewUrl);
            if (mediaUrls.Count == 0)
            {
                DebugLog.Write(LogSource, $"album page {albumViewUrl}: {DescribeMissingAnimatedArtwork(html)}");
                return Array.Empty<AnimatedArtworkVariant>();
            }

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

                var parsed = await ParseHlsMasterVariantsAsync(mediaUrl, ct);
                if (parsed.Count == 0)
                    DebugLog.Write(LogSource, $"HLS master {mediaUrl} gave no usable variants");
                variants.AddRange(parsed);
            }

            var deduped = variants
                .GroupBy(v => $"{v.Width}x{v.Height}:{GetCodecFamily(v.Codec)}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(v => v.Bandwidth).First())
                .ToList();

            return PickTierVariants(deduped);
        }
        catch (Exception ex)
        {
            DebugLog.Write(LogSource, $"animated lookup of {albumViewUrl} failed: {ex.Message}");
            return Array.Empty<AnimatedArtworkVariant>();
        }
    }

    public async Task<string?> FindAnimatedCoverUrlAsync(string albumViewUrl, CancellationToken ct = default)
        => (await SearchAnimatedArtworkVariantsAsync(albumViewUrl, ct)).FirstOrDefault()?.Url;

    /// <summary>What the animated-artwork chooser searches for.</summary>
    /// <param name="Album">The album title, or the whole typed query when <paramref name="Typed"/>.</param>
    /// <param name="Artist">The artist to search and rank with (empty for a typed query).</param>
    /// <param name="PreferArtist">The edited track's artist: only a tiebreak for typed queries.</param>
    public sealed record AnimatedArtworkQuery(string Album, string Artist, string PreferArtist, bool Typed)
    {
        /// <summary>The local album's year, 0 when unknown: only breaks ties between editions.</summary>
        public int Year { get; init; }

        /// <summary>Songs in the local album, 0 when unknown: only breaks ties between editions.</summary>
        public int TrackCount { get; init; }
    }

    public sealed record RankedAlbumCandidate(ArtworkCandidate Candidate, double Score);

    /// <summary>
    /// Albums worth opening for an animated-artwork search, best first. Both catalogues are
    /// asked at once — the Apple Music search page (ranks well, finds what the iTunes index
    /// misses, e.g. Bad Bunny's "YHLQMDLG") and the iTunes Search API (a plain JSON API that
    /// keeps answering if the page's markup shifts) — merged, then scored by
    /// <see cref="RankAnimatedCandidates"/>. Whether each one has a loop is the caller's next
    /// step (<see cref="GetAnimatedArtworkAlbumAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<RankedAlbumCandidate>> FindAnimatedArtworkCandidatesAsync(
        AnimatedArtworkQuery query, int max = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Album))
            return Array.Empty<RankedAlbumCandidate>();

        var apple = SearchAppleMusicAlbumsAsync(query.Artist, query.Album, limit: 14, ct);
        var itunes = SearchAlbumsAsync(query.Artist, query.Album, limit: 10, ct);
        await Task.WhenAll(apple, itunes);

        var merged = new List<ArtworkCandidate>();
        var seen = new HashSet<long>();
        foreach (var c in apple.Result.Concat(itunes.Result))
        {
            if (seen.Add(c.CollectionId))
                merged.Add(c);
        }

        var ranked = RankAnimatedCandidates(merged, query, max);
        DebugLog.Write(LogSource,
            $"search \"{query.Album}\"{(query.Artist.Length > 0 ? $" by \"{query.Artist}\"" : string.Empty)}: " +
            $"{apple.Result.Count} Apple Music + {itunes.Result.Count} iTunes candidate(s), {ranked.Count} relevant" +
            (ranked.Count > 0 ? $"; top \"{ranked[0].Candidate.CollectionName}\" by {ranked[0].Candidate.ArtistName}" : string.Empty));
        return ranked;
    }

    /// <summary>
    /// Scores and orders candidates (in the order the sources returned them), dropping the
    /// irrelevant ones: a title that does not resemble the query at all. When no title is
    /// close, the wanted artist's albums are kept instead, so a mistagged album still turns
    /// up something to choose from.
    /// </summary>
    internal static IReadOnlyList<RankedAlbumCandidate> RankAnimatedCandidates(
        IReadOnlyList<ArtworkCandidate> candidates, AnimatedArtworkQuery query, int max)
    {
        var scored = candidates
            .Select((c, i) => (c, i,
                relevance: AnimatedArtworkMatcher.TitleRelevance(c.CollectionName, c.ArtistName, query.Album, query.Typed),
                score: AnimatedArtworkMatcher.Score(c.CollectionName, c.ArtistName, query.Album, query.Artist,
                    query.PreferArtist, query.Typed, i)
                    + AnimatedArtworkMatcher.EditionTieBreak(c.ReleaseYear, query.Year, c.TrackCount, query.TrackCount)))
            .ToList();

        // A search from the tags knows the artist: a candidate whose title is not the album's
        // AND whose credit is someone else is a different record, never an option (GitHub #167:
        // "Wings of Love" by Erasure offered "Tears of Love" by Tears for Fears). A same-titled
        // album under another credit stays, for a mistagged artist to be picked by hand.
        bool OtherRecord((ArtworkCandidate c, int i, double relevance, double score) s)
            => !query.Typed && query.Artist.Length > 0
               && AnimatedArtworkMatcher.ArtistSimilarity(s.c.ArtistName, query.Artist) < AnimatedArtworkMatcher.SameArtist
               && AnimatedArtworkMatcher.TitleSimilarity(s.c.CollectionName, query.Album) < AnimatedArtworkMatcher.SameTitle;

        var relevant = scored.Where(s => s.relevance >= 0.3 && !OtherRecord(s)).ToList();
        if (relevant.Count == 0)
        {
            // A typed query may be just an artist ("selena gomez"): no title resembles it, so
            // that artist's albums are the answer; the edited track's artist comes next.
            var artist = query.Typed ? query.PreferArtist : query.Artist;
            relevant = scored.Where(s =>
                    (query.Typed && AnimatedArtworkMatcher.ArtistSimilarity(s.c.ArtistName, query.Album) >= 0.85) ||
                    AnimatedArtworkMatcher.ArtistSimilarity(s.c.ArtistName, artist) >= 0.85)
                .ToList();
        }

        return relevant
            .OrderByDescending(s => s.score)
            .ThenBy(s => s.i)
            .Take(Math.Max(1, max))
            .Select(s => new RankedAlbumCandidate(s.c, s.score))
            .ToList();
    }

    private static readonly Regex StorefrontCodeRegex = new("^[a-z]{2}$", RegexOptions.Compiled);

    /// <summary>A two-letter storefront for the store endpoints; anything else is the US store.</summary>
    internal static string Storefront(string? country)
    {
        var c = (country ?? string.Empty).Trim().ToLowerInvariant();
        return StorefrontCodeRegex.IsMatch(c) ? c : "us";
    }

    /// <summary>
    /// Opens one album (or playlist) page and reads its motion artwork for the chooser: the
    /// square loop's variants (the tall 3:4 loop only when there is no square one — the player
    /// draws covers into a square, so a portrait loop would be squashed), the smallest variant
    /// for a cheap live preview, and a still of the loop. Null when the page has no loop.
    /// <paramref name="known"/> names the album when /lookup already did; otherwise the page's
    /// own title and credit are used (pasted playlist links, storefront-only albums).
    /// </summary>
    public async Task<AnimatedArtworkAlbum?> GetAnimatedArtworkAlbumAsync(
        string pageUrl, ArtworkCandidate? known = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) ||
            !Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) ||
            !pageUri.Host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            req.Headers.UserAgent.ParseAdd(AppleMusicHtmlUserAgent);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                DebugLog.Write(LogSource, $"album page {pageUrl} returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            var html = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            var page = MotionArtworkPage.Parse(html);

            var square = new List<AnimatedArtworkVariant>();
            var tall = new List<AnimatedArtworkVariant>();
            if (page.SquareVideoUrl != null)
                square.AddRange(await ParseHlsMasterAllVariantsAsync(page.SquareVideoUrl, ct));
            if (square.Count == 0 && page.TallVideoUrl != null)
                tall.AddRange(await ParseHlsMasterAllVariantsAsync(page.TallVideoUrl, ct));

            // A page whose JSON moved: fall back to the older extraction, which reads the
            // rendered element and a host-only sweep. Its streams cannot be told apart by crop.
            if (square.Count == 0 && tall.Count == 0 && !page.HasMotion)
            {
                foreach (var media in ExtractAnimatedMediaUrls(html, pageUrl))
                {
                    if (media.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                        square.AddRange(await ParseHlsMasterAllVariantsAsync(media, ct));
                }
            }

            var pool = square.Count > 0 ? square : tall;
            if (pool.Count == 0)
            {
                DebugLog.Write(LogSource, $"album page {pageUrl}: {DescribeMissingAnimatedArtwork(html)}");
                return null;
            }

            var deduped = pool
                .GroupBy(v => $"{v.Width}x{v.Height}:{GetCodecFamily(v.Codec)}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(v => v.Bandwidth).First())
                .ToList();
            var tiers = PickTierVariants(deduped);
            var preview = deduped
                .Where(v => v.Width > 0)
                .OrderBy(v => Math.Max(v.Width, v.Height))
                .ThenBy(v => v.Bandwidth)
                .FirstOrDefault() ?? tiers.FirstOrDefault();

            var title = known?.CollectionName ?? page.Title ?? string.Empty;
            var artist = known?.ArtistName ?? page.Artist ?? string.Empty;
            var poster = MotionArtworkPage.SizeImageTemplate(
                square.Count > 0 ? page.SquarePosterUrl : page.TallPosterUrl, 300);
            var cover = known?.ThumbUrl ?? page.ImageUrl;

            DebugLog.Write(LogSource, $"\"{title}\" ({pageUrl}): {tiers.Count} tier(s) from {deduped.Count} variant(s)" +
                (square.Count == 0 ? " (tall only)" : page.TallVideoUrl != null ? " (tall too)" : string.Empty));

            return new AnimatedArtworkAlbum(
                known?.CollectionId ?? 0, title, artist, pageUrl,
                poster, cover, tiers, preview,
                HasSquare: square.Count > 0,
                HasTall: page.TallVideoUrl != null)
            {
                Explicitness = known?.Explicitness ?? string.Empty,
                ReleaseYear = known?.ReleaseYear ?? 0,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.Write(LogSource, $"animated lookup of {pageUrl} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Saves a loop to <paramref name="destinationPath"/>: a plain file as is, an HLS variant as
    /// its parts joined. Streamed to disk part by part (a 2160p loop is ~90 MB: buffering it
    /// whole put that, twice over, on the large-object heap). False on any failure or on
    /// cancellation, and then nothing is left at <paramref name="destinationPath"/>.
    /// </summary>
    /// <param name="onProgress">Bytes saved so far and the expected total (0 while it is not
    /// known: an HLS loop's total is known once its last part's length is).</param>
    public async Task<bool> DownloadHlsVariantAsMp4Async(
        AnimatedArtworkVariant variant,
        string destinationPath,
        CancellationToken ct = default,
        Action<long, long>? onProgress = null)
    {
        var ok = false;
        try
        {
            ok = await SaveVariantAsync(variant, destinationPath, onProgress, ct);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                DebugLog.Write(LogSource, $"download of {variant.Url} failed: {ex.Message}");
        }
        if (!ok)
        {
            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
        return ok;
    }

    private async Task<bool> SaveVariantAsync(
        AnimatedArtworkVariant variant, string destinationPath, Action<long, long>? onProgress, CancellationToken ct)
    {
        var parts = new List<Uri>();
        if (!variant.IsHls)
        {
            parts.Add(new Uri(variant.Url));
        }
        else
        {
            var playlist = await GetTextAsync(variant.Url, ct);
            if (string.IsNullOrWhiteSpace(playlist))
                return false;

            var baseUri = new Uri(variant.Url);
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
            parts = parts.DistinctBy(p => p.ToString()).ToList();

            // A playlist line may carry an absolute URI to any host; a segment off the Apple
            // allowlist means a hostile or broken playlist, and joining the rest would be garbage.
            if (parts.Count == 0 || parts.Any(p => !IsAppleMediaHost(p.ToString())))
                return false;
        }

        await using var output = File.Create(destinationPath);
        long before = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var last = i == parts.Count - 1;
            var partBase = before;
            using var req = new HttpRequestMessage(HttpMethod.Get, parts[i]);
            req.Headers.UserAgent.ParseAdd(AppleMusicHtmlUserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                DebugLog.Write(LogSource, $"download of {parts[i]} returned HTTP {(int)resp.StatusCode}");
                return false;
            }

            // Animated-cover cap, not the 24MB image cap: Apple often serves the whole loop
            // as ONE fMP4 part (~31MB at 1080p, ~90MB at 2160p). The cap is per loop, not per part.
            var written = await StreamBodyAsync(resp.Content, output, MaxAnimatedCoverBytes - before,
                onProgress is null ? null : (done, total) =>
                    onProgress(partBase + done, last && total > 0 ? partBase + total : 0),
                ct);
            if (written == 0)
                return false;
            before += written;
        }

        // The stream's own length: the file on disk lags behind its write buffer until the
        // stream is flushed, so a short loop (one part under the 4 kB buffer) read as empty.
        await output.FlushAsync(ct);
        onProgress?.Invoke(before, before);
        return output.Length > 0;
    }

    /// <summary>
    /// Copies a response body into <paramref name="output"/>, reporting bytes read and the
    /// declared length (0 when none); throws <see cref="HttpRequestException"/> past
    /// <paramref name="maxBytes"/>, as <see cref="HttpSafety.ReadBytesBoundedAsync"/>.
    /// </summary>
    private static async Task<long> StreamBodyAsync(
        HttpContent content, Stream output, long maxBytes, Action<long, long>? onProgress, CancellationToken ct)
    {
        var declared = content.Headers.ContentLength ?? 0;
        if (declared > maxBytes)
            throw new HttpRequestException($"Response body ({declared} bytes) exceeds the {maxBytes}-byte limit.");
        onProgress?.Invoke(0, declared);

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var chunk = new byte[81920];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (done + read > maxBytes)
                throw new HttpRequestException($"Response body exceeds the {maxBytes}-byte limit.");
            await output.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            onProgress?.Invoke(done, declared);
        }
        return done;
    }

    private static string CleanUrl(string u) => u.Replace("\\u002F", "/").Replace("\\/", "/");

    /// <summary>
    /// Resolves a single album by Apple/iTunes collection ID via the public /lookup
    /// endpoint. Used as a manual fallback when /search fails to surface an album
    /// (some catalog entries are reachable by ID but not by free-text search).
    /// </summary>
    public async Task<ArtworkCandidate?> LookupAlbumByIdAsync(long collectionId, CancellationToken ct = default,
        string? country = null)
    {
        if (collectionId <= 0) return null;

        try
        {
            var url = $"{LookupUrl}?id={collectionId}&country={Storefront(country)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                DebugLog.Write(LogSource, $"lookup of {collectionId} returned HTTP {(int)resp.StatusCode}");
                return null;
            }

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

            DebugLog.Write(LogSource, $"lookup of {collectionId} found no album in the {Storefront(country)} store");
        }
        catch (Exception ex)
        {
            DebugLog.Write(LogSource, $"lookup of {collectionId} failed: {ex.Message}");
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
            {
                DebugLog.Write(LogSource, $"Apple Music search for \"{term}\" returned HTTP {(int)resp.StatusCode}");
                return Array.Empty<ArtworkCandidate>();
            }

            var html = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            var ids = ExtractAppleMusicAlbumIds(html).Take(Math.Max(1, limit)).ToList();
            if (ids.Count == 0)
            {
                // A page with no album link at all is likelier a markup change than an empty
                // catalogue, so the size goes in too.
                DebugLog.Write(LogSource, $"Apple Music search for \"{term}\" linked no albums ({html.Length} chars of page)");
                return Array.Empty<ArtworkCandidate>();
            }

            // The page gives IDs but no dependable title/artist text, and the caller has to
            // check both — so let /lookup name them authoritatively, in one request.
            return await LookupAlbumsByIdAsync(ids, ct);
        }
        catch (Exception ex)
        {
            DebugLog.Write(LogSource, $"Apple Music search for \"{term}\" failed: {ex.Message}");
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
        IReadOnlyList<long> ids, CancellationToken ct = default, string? country = null)
    {
        var wanted = ids.Where(i => i > 0).Distinct().ToList();
        if (wanted.Count == 0)
            return Array.Empty<ArtworkCandidate>();

        try
        {
            var url = $"{LookupUrl}?id={string.Join(",", wanted)}&country={Storefront(country)}&entity=album";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                DebugLog.Write(LogSource, $"lookup of {wanted.Count} album(s) returned HTTP {(int)resp.StatusCode}");
                return Array.Empty<ArtworkCandidate>();
            }

            var json = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                DebugLog.Write(LogSource, $"lookup of {wanted.Count} album(s) had no results array");
                return Array.Empty<ArtworkCandidate>();
            }

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
            DebugLog.Write(LogSource, $"lookup of {wanted.Count} album(s) failed: {ex.Message}");
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
        var hiRes = RewriteArtworkUrl(artworkUrl, $"{MaxArtworkBox}x{MaxArtworkBox}-999");
        var explicitness = item.TryGetProperty("collectionExplicitness", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty
            : string.Empty;
        var year = 0;
        if (item.TryGetProperty("releaseDate", out var r) && r.ValueKind == JsonValueKind.String &&
            r.GetString() is { Length: >= 4 } date)
            int.TryParse(date.AsSpan(0, 4), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out year);
        var trackCount = item.TryGetProperty("trackCount", out var tc) && tc.ValueKind == JsonValueKind.Number &&
                         tc.TryGetInt32(out var count) ? count : 0;
        candidate = new ArtworkCandidate(id, name, artistName, thumb, standard, hiRes, viewUrl)
        {
            Explicitness = explicitness,
            ReleaseYear = year,
            TrackCount = trackCount,
        };
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
    /// Why an album page yielded no stream, for the log. Apple's page says it outright:
    /// <c>"videoArtwork":null</c> is an album without a loop (HIT ME HARD AND SOFT — the
    /// catalog API's editorialVideo agrees), while a page with no such key at all means the
    /// markup moved and the scraper needs looking at.
    /// </summary>
    internal static string DescribeMissingAnimatedArtwork(string html)
    {
        if (NullVideoArtworkRegex.IsMatch(html))
            return "videoArtwork is null (Apple has no animated artwork for this album)";
        if (html.Contains("videoArtwork", StringComparison.Ordinal))
            return "videoArtwork present but no stream URL matched (page format may have changed)";
        return $"no videoArtwork field in {html.Length} chars of page (page format may have changed)";
    }

    private static readonly Regex NullVideoArtworkRegex = new(
        "\"videoArtwork\"\\s*:\\s*null", RegexOptions.Compiled);

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
        => PickTierVariants(await ParseHlsMasterAllVariantsAsync(masterUrl, ct));

    /// <summary>Every playable variant in an HLS master, unfiltered.</summary>
    private async Task<IReadOnlyList<AnimatedArtworkVariant>> ParseHlsMasterAllVariantsAsync(
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

        return list;
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

    /// <summary>
    /// Whether a candidate is the album someone typed into the animated-artwork box. People
    /// type "Happier Than Ever Billie Eilish", not just the title, and Apple's search page ranks
    /// those queries perfectly — but read as a title, "album artist" never equals the store's
    /// album name, so every right answer was rejected before its page was opened. Accepted:
    /// the title alone (<see cref="IsLikelySameAlbum"/>'s exact rule), or the title with the
    /// artist before or after it ("by" and dashes allowed). The leftover must be a whole-word
    /// run of the candidate's credit ("Lady Gaga" in "Lady Gaga &amp; Bruno Mars"), never the
    /// other way round, or "1989 Taylor's Version Taylor Swift" would read as the original 1989.
    /// </summary>
    internal static bool MatchesTypedAlbumQuery(string? candidateAlbum, string? candidateArtist, string? typed)
    {
        if (IsLikelySameAlbum(candidateAlbum, candidateArtist, typed, null))
            return true;

        var query = NormalizeSearchText(typed);
        var album = NormalizeAlbumForMatch(candidateAlbum);
        var credit = NormalizeSearchText(candidateArtist);
        if (query.Length == 0 || album.Length == 0 || credit.Length == 0)
            return false;

        string rest;
        if (query.StartsWith(album + " ", StringComparison.Ordinal))
            rest = query[(album.Length + 1)..];
        else if (query.EndsWith(" " + album, StringComparison.Ordinal))
            rest = query[..^(album.Length + 1)];
        else
            return false;

        if (rest.StartsWith("by ", StringComparison.Ordinal))
            rest = rest[3..];
        else if (rest.EndsWith(" by", StringComparison.Ordinal))
            rest = rest[..^3];

        return rest.Length > 0 && $" {credit} ".Contains($" {rest} ", StringComparison.Ordinal);
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
}
