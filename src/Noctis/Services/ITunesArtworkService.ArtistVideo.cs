using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>What an artist video lookup came to.</summary>
public enum ArtistVideoOutcome
{
    /// <summary>The artist was matched and their page has a wide motion video.</summary>
    Found,
    /// <summary>No artist of exactly that name (or more than one): never guessed.</summary>
    NoArtist,
    /// <summary>The artist was matched; Apple has no motion video for them.</summary>
    NoVideo,
    /// <summary>Apple did not answer (offline, an error): not a miss worth remembering.</summary>
    Failed,
}

/// <summary>One artist video lookup: the Apple artist and, when found, the stream to save.</summary>
public sealed record ArtistVideoLookup(
    ArtistVideoOutcome Outcome,
    long ArtistId = 0,
    ITunesArtworkService.AnimatedArtworkVariant? Variant = null,
    string? PageUrl = null);

/// <summary>
/// The motion video an Apple Music artist page carries (Apple Music's moving artist header).
/// The page's inline JSON has <c>"videoArtwork":{"dictionary":{"motionArtistSquare1x1":…}}</c>
/// and <c>"wideVideoArtwork":{"dictionary":{"motionArtistFullscreen16x9":…,
/// "motionArtistWide16x9":…}}</c>, each an HLS master in <c>video</c>, or
/// <c>"wideVideoArtwork":null</c> for an artist without one (recorded 11-26: Chase Atlantic
/// has one, Taylor Swift has null). Only the page's first <c>wideVideoArtwork</c> block is
/// read: it is the page header's, and anything later belongs to other shelves.
/// </summary>
internal sealed record ArtistMotionPage(string? WideVideoUrl, string? WidePosterUrl, bool IsArtistPage)
{
    private static readonly Regex WideBlockRegex = new(
        "\"wideVideoArtwork\"\\s*:\\s*(?<v>null|\\{)", RegexOptions.Compiled);

    internal bool HasVideo => WideVideoUrl != null;

    /// <param name="artistId">The artist the page was asked for: a page whose own intent names
    /// another artist (a redirect somewhere unexpected) is not read at all.</param>
    internal static ArtistMotionPage Parse(string html, long artistId)
    {
        var isArtist = artistId > 0 && html.Contains($"\"storeAdamID\":\"{artistId}\"", StringComparison.Ordinal);
        if (!isArtist)
            return new ArtistMotionPage(null, null, false);

        var block = WideBlockRegex.Match(html);
        if (!block.Success || block.Groups["v"].Value == "null")
            return new ArtistMotionPage(null, null, true);

        var start = block.Groups["v"].Index;
        string? wide = null, widePoster = null, full = null, fullPoster = null;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(html.Substring(start, Math.Min(html.Length - start, 64 * 1024)));
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("dictionary", out var dict) && dict.ValueKind == JsonValueKind.Object)
            {
                (wide, widePoster) = Read(dict, "motionArtistWide16x9");
                (full, fullPoster) = Read(dict, "motionArtistFullscreen16x9");
            }
        }
        catch (Exception)
        {
            return new ArtistMotionPage(null, null, true);
        }

        return wide != null
            ? new ArtistMotionPage(wide, widePoster, true)
            : new ArtistMotionPage(full, fullPoster, true);
    }

    private static (string? Video, string? Poster) Read(JsonElement dict, string key)
    {
        if (!dict.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object)
            return (null, null);
        var video = o.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var poster = o.TryGetProperty("previewFrame", out var f) && f.ValueKind == JsonValueKind.Object &&
                     f.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(video) || !ITunesArtworkService.IsAppleMediaHost(video))
            return (null, null);
        return (video, string.IsNullOrWhiteSpace(poster) ? null : poster);
    }
}

public sealed partial class ITunesArtworkService
{
    /// <summary>One row of a /search?entity=musicArtist answer.</summary>
    internal sealed record ArtistHit(string Name, long Id, string? LinkUrl);

    /// <summary>The largest frame an artist video is saved at: 1080p, never 4K. The hero
    /// decodes at <c>VideoBackdrop.MaxLongSide</c> (960 px) anyway.</summary>
    internal const int ArtistVideoMaxWidth = 1920;
    internal const int ArtistVideoMaxHeight = 1080;

    /// <summary>The hero's decode width (<c>VideoBackdrop.MaxLongSide</c>): the smallest frame
    /// at least this wide is saved, since every pixel above it is decoded and thrown away.</summary>
    internal const int ArtistVideoWantedWidth = Noctis.Controls.VideoBackdrop.MaxLongSide;

    private static readonly Regex ArtistSlugRegex = new(
        @"/artist/(?<slug>[^/?#]+)/(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Settings › Artwork › Artist videos: finds the artist's Apple Music motion video. The
    /// artist is the store's artist of EXACTLY this name (case and accents aside): none, or two
    /// different artists of that name, is <see cref="ArtistVideoOutcome.NoArtist"/>, never a
    /// guess, so a stranger's video can never land on the page. <paramref name="knownArtistId"/>
    /// skips the search (an id resolved before). The artist page is read in the album loops'
    /// storefront order (home and US, then GB and DE); the wide 16:9 loop is preferred, the
    /// fullscreen one taken when it is missing, and the variant is picked by
    /// <see cref="PickArtistVideoVariant"/>. Every request waits its turn at
    /// <paramref name="gate"/> when given. Cancellation propagates; nothing else throws.
    /// </summary>
    public async Task<ArtistVideoLookup> FindArtistVideoAsync(
        string artistName, long knownArtistId = 0, PoliteGate? gate = null, CancellationToken ct = default,
        IReadOnlyList<string>? libraryAlbums = null)
    {
        var name = (artistName ?? string.Empty).Trim();
        if (name.Length == 0 && knownArtistId <= 0)
            return new ArtistVideoLookup(ArtistVideoOutcome.NoArtist);

        try
        {
            var artistId = knownArtistId;
            string? slug = null;
            if (artistId <= 0)
            {
                var answered = false;
                foreach (var sf in AnimatedSearchStorefronts(otherStorefronts: false))
                {
                    if (gate != null) await gate.WaitTurnAsync(ct).ConfigureAwait(false);
                    var hits = await SearchArtistHitsAsync(name, sf, ct).ConfigureAwait(false);
                    if (hits == null) continue;
                    answered = true;
                    var match = MatchArtistExactly(name, hits, out var ambiguous);
                    if (ambiguous)
                    {
                        // Namesakes ("Drake" is several store artists): the one whose releases
                        // are the albums in the library, when exactly one stands out.
                        match = await PickNamesakeByAlbumsAsync(name, hits, libraryAlbums, sf, gate, ct).ConfigureAwait(false);
                        if (match == null)
                        {
                            DebugLog.Write(LogSource, $"artist video: more than one artist named \"{name}\" in {sf}; not guessing");
                            return new ArtistVideoLookup(ArtistVideoOutcome.NoArtist);
                        }
                    }
                    if (match == null) continue;
                    artistId = match.Id;
                    slug = SlugOf(match.LinkUrl);
                    break;
                }
                if (artistId <= 0)
                {
                    if (!answered) return new ArtistVideoLookup(ArtistVideoOutcome.Failed);
                    DebugLog.Write(LogSource, $"artist video: no artist named exactly \"{name}\"");
                    return new ArtistVideoLookup(ArtistVideoOutcome.NoArtist);
                }
            }

            var pageAnswered = false;
            foreach (var sf in AnimatedSearchStorefronts(false).Concat(AnimatedSearchStorefronts(true)))
            {
                var pageUrl = ArtistPageUrl(sf, slug, artistId);
                if (gate != null) await gate.WaitTurnAsync(ct).ConfigureAwait(false);
                var html = await GetPageAsync(pageUrl, ct).ConfigureAwait(false);
                if (html == null) continue;
                // Answered: a page that reads as someone else's (or moved its markup) is a miss
                // to remember, not an outage to retry on every visit.
                pageAnswered = true;
                var page = ArtistMotionPage.Parse(html, artistId);
                if (!page.HasVideo) continue;

                if (gate != null) await gate.WaitTurnAsync(ct).ConfigureAwait(false);
                var variants = await ParseHlsMasterAllVariantsAsync(page.WideVideoUrl!, ct).ConfigureAwait(false);
                var pick = PickArtistVideoVariant(variants);
                if (pick == null)
                {
                    DebugLog.Write(LogSource, $"artist video {artistId} ({sf}): {variants.Count} variant(s), none usable");
                    continue;
                }
                DebugLog.Write(LogSource, $"artist video {artistId} ({sf}): {pick.Width}x{pick.Height} {GetCodecFamily(pick.Codec)} of {variants.Count} variant(s)");
                return new ArtistVideoLookup(ArtistVideoOutcome.Found, artistId, pick, pageUrl);
            }

            if (!pageAnswered) return new ArtistVideoLookup(ArtistVideoOutcome.Failed, artistId);
            DebugLog.Write(LogSource, $"artist video {artistId}: Apple has none");
            return new ArtistVideoLookup(ArtistVideoOutcome.NoVideo, artistId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.Write(LogSource, $"artist video lookup for \"{name}\" failed: {ex.Message}");
            return new ArtistVideoLookup(ArtistVideoOutcome.Failed);
        }
    }

    /// <summary>The artist page in a storefront; Apple redirects a missing or stale slug.</summary>
    internal static string ArtistPageUrl(string storefront, string? slug, long artistId)
        => $"{AppleMusicHost}/{Storefront(storefront)}/artist/{(string.IsNullOrWhiteSpace(slug) ? "artist" : slug)}/{artistId}";

    internal static string? SlugOf(string? linkUrl)
    {
        if (string.IsNullOrWhiteSpace(linkUrl)) return null;
        var m = ArtistSlugRegex.Match(linkUrl);
        return m.Success ? m.Groups["slug"].Value : null;
    }

    /// <summary>
    /// The one store artist whose name is exactly <paramref name="name"/> (case, accents and
    /// punctuation aside: <see cref="AnimatedArtworkMatcher.Normalize"/>). Two different
    /// artists of that name set <paramref name="ambiguous"/> and return null.
    /// </summary>
    internal static ArtistHit? MatchArtistExactly(string name, IReadOnlyList<ArtistHit> hits, out bool ambiguous)
    {
        ambiguous = false;
        var wanted = AnimatedArtworkMatcher.Normalize(name);
        if (wanted.Length == 0) return null;
        var exact = hits
            .Where(h => h.Id > 0 && AnimatedArtworkMatcher.Normalize(h.Name) == wanted)
            .GroupBy(h => h.Id)
            .Select(g => g.First())
            .ToList();
        if (exact.Count > 1)
        {
            ambiguous = true;
            return null;
        }
        return exact.FirstOrDefault();
    }

    /// <summary>How many same-name store artists have their releases read to tell them apart.</summary>
    internal const int MaxNamesakes = 5;

    /// <summary>The store artists named exactly <paramref name="name"/>, one row per id.</summary>
    internal static IReadOnlyList<ArtistHit> ExactNameHits(string name, IReadOnlyList<ArtistHit> hits)
    {
        var wanted = AnimatedArtworkMatcher.Normalize(name);
        return hits
            .Where(h => h.Id > 0 && AnimatedArtworkMatcher.Normalize(h.Name) == wanted)
            .GroupBy(h => h.Id)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// How many of the library's album titles are among a store artist's release titles. A
    /// release counts when the titles are equal (<see cref="AnimatedArtworkMatcher.Normalize"/>)
    /// or one is the other plus an edition ("Take Care" / "Take Care (Deluxe)").
    /// </summary>
    internal static int CountAlbumsInCommon(IReadOnlyList<string> libraryAlbums, IEnumerable<string> storeReleases)
    {
        var store = storeReleases.Select(AnimatedArtworkMatcher.Normalize).Where(t => t.Length > 0).Distinct().ToList();
        var count = 0;
        foreach (var title in libraryAlbums.Select(AnimatedArtworkMatcher.Normalize).Where(t => t.Length > 0).Distinct())
        {
            if (store.Any(r => r == title
                               || (title.Length >= 4 && r.StartsWith(title + " ", StringComparison.Ordinal))
                               || (r.Length >= 4 && title.StartsWith(r + " ", StringComparison.Ordinal))))
                count++;
        }
        return count;
    }

    /// <summary>The namesake with the most library albums among its releases: at least one, and
    /// more than every other namesake (a tie is no answer). Null otherwise.</summary>
    internal static ArtistHit? PickNamesake(IReadOnlyList<(ArtistHit Hit, int InCommon)> scored)
    {
        var ranked = scored.OrderByDescending(s => s.InCommon).ToList();
        if (ranked.Count == 0 || ranked[0].InCommon == 0) return null;
        if (ranked.Count > 1 && ranked[1].InCommon == ranked[0].InCommon) return null;
        return ranked[0].Hit;
    }

    /// <summary>
    /// Several store artists share the name (owner 11-27: "Drake" and "Juice WRLD" never got a
    /// video, the lookup would not guess between namesakes): each one's releases are read and
    /// the artist whose releases are the library's albums is taken (<see cref="PickNamesake"/>).
    /// Without library albums, or when no namesake stands out, null: still never a guess.
    /// </summary>
    private async Task<ArtistHit?> PickNamesakeByAlbumsAsync(
        string name, IReadOnlyList<ArtistHit> hits, IReadOnlyList<string>? libraryAlbums,
        string storefront, PoliteGate? gate, CancellationToken ct)
    {
        if (libraryAlbums is not { Count: > 0 }) return null;
        var scored = new List<(ArtistHit Hit, int InCommon)>();
        foreach (var hit in ExactNameHits(name, hits).Take(MaxNamesakes))
        {
            if (gate != null) await gate.WaitTurnAsync(ct).ConfigureAwait(false);
            var releases = await LookupArtistReleasesAsync(hit.Id, storefront, ct).ConfigureAwait(false);
            scored.Add((hit, CountAlbumsInCommon(libraryAlbums, releases.Select(r => r.CollectionName))));
        }
        var pick = PickNamesake(scored);
        DebugLog.Write(LogSource, $"artist video: {scored.Count} artists named \"{name}\" in {storefront}; library albums in common: " +
            string.Join(", ", scored.Select(x => $"{x.Hit.Id}={x.InCommon}")) + (pick == null ? "; none stands out" : $"; taking {pick.Id}"));
        return pick;
    }

    /// <summary>The artist rows of a /search?entity=musicArtist answer, with their page links.</summary>
    internal static IReadOnlyList<ArtistHit> ParseArtistHits(string json)
    {
        var list = new List<ArtistHit>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("wrapperType", out var w) && w.GetString() == "artist" &&
                item.TryGetProperty("artistId", out var idNode) && idNode.TryGetInt64(out var id) &&
                item.TryGetProperty("artistName", out var n) && n.GetString() is { Length: > 0 } artist)
            {
                var link = item.TryGetProperty("artistLinkUrl", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                list.Add(new ArtistHit(artist, id, link));
            }
        }
        return list;
    }

    /// <summary>The artist search in one storefront; null when Apple did not answer.</summary>
    private async Task<IReadOnlyList<ArtistHit>?> SearchArtistHitsAsync(string term, string country, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(ArtistSearchUrl(term, country), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                DebugLog.Write(LogSource, $"artist video: artist search in {country} returned HTTP {(int)resp.StatusCode}");
                return null;
            }
            var json = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
            return ParseArtistHits(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugLog.Write(LogSource, $"artist video: artist search in {country} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>An Apple Music page as the browser gets it; null on any failure.</summary>
    private async Task<string?> GetPageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(AppleMusicHtmlUserAgent);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                DebugLog.Write(LogSource, $"artist page {url} returned HTTP {(int)resp.StatusCode}");
                return null;
            }
            // A redirect must stay on Apple Music.
            if (resp.RequestMessage?.RequestUri is { } final &&
                !final.Host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase))
                return null;
            return await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugLog.Write(LogSource, $"artist page {url} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The variant an artist video is saved at: SDR only, never above 1920x1080; H.264 when
    /// there is one (H.265 only when the master has no H.264 at all); the smallest frame at
    /// least <see cref="ArtistVideoWantedWidth"/> wide (else the largest there is); and of that
    /// frame the lightest stream: the hero shows it darkened and decoded at 960 px, so a bigger
    /// frame or a heavier encode only costs CPU and cache space. Owner 11-26 "slow and choppy":
    /// LibVLC software decode of a 24 fps H.264 clip into the 960x540 buffer measured ~30% of a
    /// core at 1920x1080 against ~8-11% at 960x540 (ArtistVideoPerfProbeTests).
    /// </summary>
    internal static AnimatedArtworkVariant? PickArtistVideoVariant(IReadOnlyList<AnimatedArtworkVariant> variants)
    {
        var pool = variants
            .Where(v => v.IsHls && v.Width > 0 && v.Height > 0
                        && v.Width <= ArtistVideoMaxWidth && v.Height <= ArtistVideoMaxHeight
                        && (v.VideoRange.Length == 0 || v.VideoRange.Equals("SDR", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var h264 = pool.Where(v => GetCodecFamily(v.Codec) == "h.264").ToList();
        if (h264.Count > 0) pool = h264;
        var wideEnough = pool.Where(v => v.Width >= ArtistVideoWantedWidth).ToList();
        if (wideEnough.Count > 0)
            return wideEnough
                .OrderBy(v => (long)v.Width * v.Height)
                .ThenBy(v => v.Bandwidth)
                .FirstOrDefault();
        return pool
            .OrderByDescending(v => (long)v.Width * v.Height)
            .ThenBy(v => v.Bandwidth)
            .FirstOrDefault();
    }
}
