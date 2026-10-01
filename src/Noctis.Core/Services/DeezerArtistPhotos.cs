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
                if (rank > bestRank)
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

    /// <summary>
    /// The photo's bytes, or null when the server refuses, answers with something other than an
    /// image, or the body fails the magic-byte check. A 1800 px URL that is refused is retried
    /// at Deezer's 1000 px original. Network errors and cancellation propagate.
    /// </summary>
    public async Task<byte[]?> DownloadImageAsync(string imageUrl, CancellationToken ct = default)
    {
        var bytes = await DownloadImageOnceAsync(imageUrl, ct).ConfigureAwait(false);
        // Should Deezer ever refuse the large size, the 1000px original still exists.
        if (bytes == null && imageUrl.Contains("/1800x1800-", StringComparison.Ordinal))
            bytes = await DownloadImageOnceAsync(imageUrl.Replace("/1800x1800-", "/1000x1000-", StringComparison.Ordinal), ct).ConfigureAwait(false);
        return bytes;
    }

    private async Task<byte[]?> DownloadImageOnceAsync(string imageUrl, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            return null;

        var imageData = await HttpSafety
            .ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, token).ConfigureAwait(false);
        // Magic-byte check: an error/HTML page must never be cached as artwork.
        if (imageData.Length == 0 || !HttpSafety.LooksLikeImage(imageData))
            return null;
        return imageData;
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

    /// <summary>How well a search hit's name matches the asked one: 0 equal (diacritics folded,
    /// case ignored), 1 equal without spaces, 2 starts with it, 3 contains it, 10 otherwise,
    /// 100 nameless.</summary>
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

        if (result.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (result.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;

        return 10;
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

    /// <summary>Deezer's "no photo" artists carry an empty image hash ("/artist//").</summary>
    public static bool IsPlaceholderUrl(string url)
        => url.Contains("/artist//", StringComparison.Ordinal)
           || url.Contains("/images/artist//", StringComparison.Ordinal);
}
