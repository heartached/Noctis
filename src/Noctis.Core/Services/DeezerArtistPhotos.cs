using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Artist photos from Deezer's public API (no account, no key): which Deezer artist a name is,
/// and that artist's photo. Moved out of the desktop's ArtistImageService (which keeps its cache,
/// sidecars, refresh sweep and fan counts, and asks here) so the phone matches names the same way:
/// diacritics folded, same-name impostors ranked by fans and verified against the library's own
/// titles, the primary artist tried for a collaboration. Only the artist's name is sent (and,
/// when several Deezer accounts share it, their public ids for the top-track check).
/// </summary>
public sealed class DeezerArtistPhotos
{
    public const string SearchUrl = "https://api.deezer.com/search/artist";
    public const int SearchLimit = 10;
    public const string ArtistUrl = "https://api.deezer.com/artist";

    /// <summary>How many same-name accounts (fan-ordered) get their top tracks checked.</summary>
    public const int VerifyCandidateLimit = 3;

    private readonly HttpClient _http;

    /// <param name="http">Client for the searches and downloads; the caller owns its timeouts
    /// (each call also takes a token).</param>
    public DeezerArtistPhotos(HttpClient http) => _http = http;

    /// <summary>The Deezer account chosen for an artist: its photo URL and fan count.</summary>
    public sealed record Match(string ImageUrl, long Fans);

    /// <summary>
    /// A lookup's outcome. <see cref="Answered"/> is false when a name candidate got no usable
    /// answer (a server error, a rate limit): a null <see cref="Match"/> then means "unknown",
    /// not "Deezer has no such artist", and a caller should not remember it as a miss.
    /// </summary>
    public sealed record Lookup(Match? Match, bool Answered);

    /// <summary>One search hit in the best name tier, in Deezer's own numbers.</summary>
    public sealed record Candidate(long Id, string ImageUrl, long Fans, long Albums);

    /// <summary>
    /// Searches Deezer for the artist and returns the best account's photo URL and fan count.
    /// Tries the full artist name first, then the primary artist for collaborations.
    /// <paramref name="libraryTitleKeys"/> gives the library's titles for the artist as
    /// <see cref="TitleKey"/>s; it is asked only when several accounts share the best name.
    /// Network and parse errors propagate (cancellation included); a refused request skips to
    /// the next candidate.
    /// </summary>
    public async Task<Lookup> FindAsync(string artistName, Func<IReadOnlySet<string>>? libraryTitleKeys = null, CancellationToken ct = default)
    {
        var answered = true;
        foreach (var candidate in BuildArtistCandidates(artistName))
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{SearchUrl}?q={Uri.EscapeDataString(candidate)}&limit={SearchLimit}";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                answered = false;
                continue;
            }

            var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
                continue;

            // Deezer's public search has no "verified" flag and returns impostor entries
            // with the SAME name — for "Bad Bunny" a 6-fan, 1-album account came back
            // FIRST and the real artist (7.99M fans, 88 albums) second. Returning on the
            // first exact match therefore swapped in a fake's photo. Names are compared
            // without diacritics: the real "Arcángel" is spelled "Arcangel" on Deezer, and
            // an accent-exact tier handed the page to a 1,683-fan duplicate (09-17).
            // Within the best tier the accounts are verified against the library's own
            // track titles (Deezer top tracks); fans then album count break what is left.
            var tier = new List<Candidate>();
            var bestRank = int.MaxValue;

            foreach (var item in data.EnumerateArray())
            {
                var imageUrl = GetBestImageUrl(item);
                if (string.IsNullOrWhiteSpace(imageUrl))
                    continue;

                var resultName = item.TryGetProperty("name", out var nameNode)
                    ? nameNode.GetString()
                    : null;
                var rank = RankMatch(resultName, candidate);
                // Deezer's search is fuzzy: an artist it lacks still answers with whoever
                // shares a word ("Low" → "Lower Than Atlantis"), and taking that hit put a
                // stranger's photo on the page (GitHub #141). An unrelated name is no match.
                if (rank >= NoMatchRank || rank > bestRank)
                    continue;
                if (rank < bestRank)
                {
                    bestRank = rank;
                    tier.Clear();
                }
                tier.Add(new Candidate(ReadCount(item, "id"), imageUrl, ReadCount(item, "nb_fan"), ReadCount(item, "nb_album")));
            }

            if (tier.Count == 0)
                continue;

            tier.Sort((a, b) => b.Fans != a.Fans ? b.Fans.CompareTo(a.Fans) : b.Albums.CompareTo(a.Albums));
            var chosen = tier.Count > 1
                ? await VerifyAgainstLibraryAsync(tier, libraryTitleKeys, ct).ConfigureAwait(false)
                : tier[0];
            return new Lookup(new Match(chosen.ImageUrl, chosen.Fans), true);
        }

        return new Lookup(null, answered);
    }

    /// <summary>One row of a manual artist search (GitHub #141): who the account is, a small
    /// photo for the row and the full-size one to save, in Deezer's own numbers.</summary>
    public sealed record SearchHit(long Id, string Name, string? ThumbnailUrl, string? ImageUrl, long Fans, long Albums);

    /// <summary>How many accounts a manual search lists.</summary>
    public const int ManualSearchLimit = 25;

    /// <summary>
    /// Deezer's own answer for <paramref name="query"/>, in its order, for the user to pick from
    /// ("Search Artist Info" on the artist page). Accounts without a photo are listed too: the
    /// right artist may have none. An error status throws <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query))
            return hits;
        using var response = await _http.GetAsync(
            $"{SearchUrl}?q={Uri.EscapeDataString(query.Trim())}&limit={ManualSearchLimit}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return hits;
        foreach (var item in data.EnumerateArray())
        {
            var hit = ReadHit(item);
            if (hit != null)
                hits.Add(hit);
        }
        return hits;
    }

    /// <summary>
    /// The photo URL and fan count of one Deezer account, for an artist the user pinned to it.
    /// A null <see cref="Lookup.Match"/> with <see cref="Lookup.Answered"/> true means the
    /// account has no photo (or is gone); false means Deezer gave no usable answer.
    /// </summary>
    public async Task<Lookup> GetArtistAsync(long deezerId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{ArtistUrl}/{deezerId}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new Lookup(null, false);
        var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        // A removed account answers 200 with { "error": { … } }.
        if (doc.RootElement.TryGetProperty("error", out _))
            return new Lookup(null, true);
        var hit = ReadHit(doc.RootElement);
        return hit?.ImageUrl is { } url
            ? new Lookup(new Match(url, hit.Fans), true)
            : new Lookup(null, true);
    }

    private static SearchHit? ReadHit(JsonElement item)
    {
        var id = ReadCount(item, "id");
        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (id <= 0 || string.IsNullOrWhiteSpace(name))
            return null;
        string? thumb = null;
        foreach (var size in new[] { "picture_medium", "picture_big", "picture_xl" })
        {
            if (item.TryGetProperty(size, out var p) && p.ValueKind == JsonValueKind.String
                && p.GetString() is { Length: > 0 } url && !IsPlaceholderUrl(url))
            {
                thumb = url;
                break;
            }
        }
        return new SearchHit(id, name.Trim(), thumb, GetBestImageUrl(item), ReadCount(item, "nb_fan"), ReadCount(item, "nb_album"));
    }

    /// <summary>
    /// Picks between same-name accounts by how many of the library's own titles for the
    /// artist appear in each account's Deezer top tracks; the most overlaps wins, and with
    /// no library titles or no overlap at all the fan order stands. Costs one request per
    /// checked account, only in the ambiguous case.
    /// </summary>
    private async Task<Candidate> VerifyAgainstLibraryAsync(List<Candidate> tier, Func<IReadOnlySet<string>>? libraryTitleKeys, CancellationToken ct)
    {
        var titles = libraryTitleKeys?.Invoke();
        if (titles == null || titles.Count == 0)
            return tier[0];

        var best = tier[0];
        var bestOverlap = 0;
        foreach (var candidate in tier.Take(VerifyCandidateLimit))
        {
            if (candidate.Id <= 0)
                continue;
            var overlap = await CountTopTrackOverlapAsync(candidate.Id, titles, ct).ConfigureAwait(false);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = candidate;
            }
        }
        return best;
    }

    private async Task<int> CountTopTrackOverlapAsync(long deezerId, IReadOnlySet<string> libraryKeys, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{ArtistUrl}/{deezerId}/top?limit=50", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return 0;
            var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return 0;

            var overlap = 0;
            foreach (var item in data.EnumerateArray())
            {
                var title = item.TryGetProperty("title_short", out var shortNode) ? shortNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(title) && item.TryGetProperty("title", out var titleNode))
                    title = titleNode.GetString();
                var key = TitleKey(title);
                if (key.Length >= 3 && libraryKeys.Contains(key))
                    overlap++;
            }
            return overlap;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtistImage] Top-track check failed for Deezer artist {deezerId}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>A download's outcome: the photo's bytes, or null. <see cref="NoPhoto"/> is true
    /// when Deezer answered with its "no photo" silhouette: the artist has no photo, which is
    /// an answer (unlike a refusal or a failure).</summary>
    public sealed record ImageDownload(byte[]? Bytes, bool NoPhoto);

    /// <summary>
    /// The photo's bytes, or null when the server refuses, answers with something other than an
    /// image, the body fails the magic-byte check, or it is Deezer's silhouette. A 1800 px URL
    /// that is refused is retried at Deezer's 1000 px original. Network errors and cancellation
    /// propagate.
    /// </summary>
    public async Task<ImageDownload> DownloadImageAsync(string imageUrl, CancellationToken ct = default)
    {
        var download = await DownloadImageOnceAsync(imageUrl, ct).ConfigureAwait(false);
        // Should Deezer ever refuse the large size, the 1000px original still exists. The
        // silhouette is not retried: a hash Deezer lacks is redirected at every size (10-12).
        if (download.Bytes == null && !download.NoPhoto && imageUrl.Contains("/1800x1800-", StringComparison.Ordinal))
            download = await DownloadImageOnceAsync(imageUrl.Replace("/1800x1800-", "/1000x1000-", StringComparison.Ordinal), ct).ConfigureAwait(false);
        return download;
    }

    private async Task<ImageDownload> DownloadImageOnceAsync(string imageUrl, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            return new(null, false);
        // A followed redirect leaves the final URL on the request.
        if (IsPlaceholderUrl((response.RequestMessage ?? request).RequestUri?.AbsoluteUri ?? ""))
            return new(null, true);

        var imageData = await HttpSafety
            .ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, token).ConfigureAwait(false);
        // Magic-byte check: an error/HTML page must never be cached as artwork.
        if (imageData.Length == 0 || !HttpSafety.LooksLikeImage(imageData))
            return new(null, false);
        if (IsNoPhotoImage(imageData))
            return new(null, true);
        return new(imageData, false);
    }

    /// <summary>Deezer serves the same photo at any square size up to 1800px; the search
    /// JSON only offers 1000px (<c>picture_xl</c>). The desktop artist page paints the portrait
    /// across the whole window, so ask for 1800 (measured 09-13: 296 KB vs 88 KB, same
    /// hash). Non-Deezer or unfamiliar URLs pass through untouched.</summary>
    public static string UpgradeSize(string imageUrl)
        => imageUrl.Contains("dzcdn.net/images/artist/", StringComparison.OrdinalIgnoreCase)
            ? imageUrl.Replace("/1000x1000-", "/1800x1800-", StringComparison.Ordinal)
            : imageUrl;

    /// <summary>A Deezer photo URL at the 1000px original instead of the 1800px
    /// <see cref="UpgradeSize"/> asks for: the phone never draws a portrait wider than its
    /// screen, and the small file is a third of the data.</summary>
    public static string OriginalSize(string imageUrl)
        => imageUrl.Replace("/1800x1800-", "/1000x1000-", StringComparison.Ordinal);

    /// <summary>Title comparison key: diacritics folded, case and punctuation dropped, any
    /// "(feat. …)" / "[…]" / " - …" suffix removed so a tagged "Me Acostumbré (feat. Bad
    /// Bunny)" meets Deezer's "Me Acostumbré".</summary>
    public static string TitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        var cut = title.IndexOfAny(new[] { '(', '[' });
        if (cut > 0) title = title[..cut];
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) title = title[..dash];
        return string.Concat(FoldDiacritics(title).Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    /// <summary>"Arcángel" → "Arcangel": Deezer spells many Latin artists without accents.</summary>
    public static string FoldDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary><see cref="RankMatch"/>'s answer for a name that is not the asked one.</summary>
    public const int NoMatchRank = 10;

    /// <summary>How well a search hit's name matches the asked one: 0 equal (diacritics folded,
    /// case ignored), 1 equal without spaces, 2 starts with it as whole words ("Ghost B.C." for
    /// "Ghost", not "Ghostface Killah"), 3 contains it as whole words ("The Weeknd" for
    /// "Weeknd", not "Asia" for "Sia"), 10 otherwise, 100 nameless.</summary>
    public static int RankMatch(string? resultName, string queryName)
    {
        if (string.IsNullOrWhiteSpace(resultName))
            return 100;

        var result = FoldDiacritics(resultName.Trim());
        var query = FoldDiacritics(queryName.Trim());
        if (result.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;

        var compactResult = RemoveWhitespace(result);
        var compactQuery = RemoveWhitespace(query);
        if (compactResult.Equals(compactQuery, StringComparison.OrdinalIgnoreCase))
            return 1;

        var at = IndexOfWholeWords(result, query);
        if (at == 0)
            return 2;
        if (at > 0)
            return 3;

        return NoMatchRank;
    }

    /// <summary>Where <paramref name="query"/> first occurs in <paramref name="text"/> with no
    /// letter or digit glued to either end (case ignored), or -1.</summary>
    private static int IndexOfWholeWords(string text, string query)
    {
        if (query.Length == 0)
            return -1;
        for (var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(query, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + query.Length;
            if ((at == 0 || !char.IsLetterOrDigit(text[at - 1]))
                && (end == text.Length || !char.IsLetterOrDigit(text[end])))
                return at;
        }
        return -1;
    }

    /// <summary>The names asked, in order: the full credit, then its primary artist when that
    /// differs (same separators as the artist index, so a grouped name tries exactly the name the
    /// grid shows).</summary>
    public static IEnumerable<string> BuildArtistCandidates(string artistName)
    {
        var normalized = artistName.Trim();
        if (normalized.Length == 0)
            yield break;

        yield return normalized;

        var primary = Track.GetPrimaryArtist(normalized);

        if (!string.IsNullOrWhiteSpace(primary) &&
            !string.Equals(primary, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return primary;
        }
    }

    private static string RemoveWhitespace(string value)
        => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    private static long ReadCount(JsonElement item, string property)
        => item.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var v)
            ? v
            : 0;

    private static string? GetBestImageUrl(JsonElement artistNode)
    {
        foreach (var propertyName in new[] { "picture_xl", "picture_big", "picture_medium" })
        {
            if (!artistNode.TryGetProperty(propertyName, out var node))
                continue;

            var imageUrl = node.GetString();
            if (!string.IsNullOrWhiteSpace(imageUrl) && !IsPlaceholderUrl(imageUrl))
                return UpgradeSize(imageUrl);
        }

        return null;
    }

    /// <summary>The image hash of Deezer's grey "no photo" silhouette: the MD5 of an empty
    /// string. The CDN redirects a photo hash it lacks there (302, same size), so the API's own
    /// URL looks like any photo (measured 10-12: "Xavier Mayne", "Yesir" and six more; the API
    /// response has no field that tells).</summary>
    public const string NoPhotoHash = "d41d8cd98f00b204e9800998ecf8427e";

    /// <summary>Deezer's "no photo" artists carry an empty image hash ("/artist//"), or the
    /// silhouette's <see cref="NoPhotoHash"/> once the CDN redirected there.</summary>
    public static bool IsPlaceholderUrl(string url)
        => url.Contains("/artist//", StringComparison.Ordinal)
           || url.Contains("/images/artist//", StringComparison.Ordinal)
           || url.Contains("/images/artist/" + NoPhotoHash + "/", StringComparison.Ordinal);

    /// <summary>The silhouette as the CDN served it on 10-12, at the sizes Noctis downloads:
    /// length and MD5. Recognising it by its bytes is the fallback: a photo cached before this
    /// check kept only the URL it was asked for (no hint), and a client whose handler does not
    /// report the redirect's target still gets these bytes.</summary>
    private static readonly (long Length, string Md5)[] NoPhotoImages =
    {
        (44_631, "BFD8A5BF261AB328BA9D72EB34B4FAC0"),   // asked at 1800 px (served at 1200)
        (32_022, "CF0B6A5247E606F67470140451774CB5"),   // 1000 px
    };

    /// <summary>True when the bytes are Deezer's silhouette (hashed only at its exact lengths).</summary>
    public static bool IsNoPhotoImage(ReadOnlySpan<byte> image)
    {
        foreach (var (length, md5) in NoPhotoImages)
            if (image.Length == length)
                return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(image)) == md5;
        return false;
    }

    /// <summary><see cref="IsNoPhotoImage"/> for a cached file: only a file of the silhouette's
    /// exact length is read. False when the file cannot be read.</summary>
    public static bool IsNoPhotoFile(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return NoPhotoImages.Any(i => i.Length == length) && IsNoPhotoImage(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
