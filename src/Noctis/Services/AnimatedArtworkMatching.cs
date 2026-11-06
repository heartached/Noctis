using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Ranking for the animated-artwork chooser. The old search kept only candidates whose title
/// was exactly the wanted one and then handed over the first album with a loop, so the chooser
/// could only ever show one album. This scores every candidate instead, so the right edition
/// sorts first and the near misses (the deluxe, the clean version, the single) sit behind it
/// for the user to pick from. Internal for tests (InternalsVisibleTo Noctis.Tests).
/// </summary>
internal static class AnimatedArtworkMatcher
{
    // Edition and format wrappers a local tag and the store routinely spell differently.
    // Only release-format words: "(Taylor's Version)" or "(The Til Dawn Edition)" name a
    // distinct release, so they stay and only score as a partial match.
    private static readonly Regex EditionWrapperRegex = new(
        @"[\(\[]\s*(?:(?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?|(?:super\s+)?deluxe(?:\s+(?:edition|version))?|" +
        @"expanded(?:\s+edition)?|special\s+edition|extended(?:\s+(?:version|edition))?|" +
        @"bonus\s+track(?:s)?(?:\s+version)?|video\s+version|explicit(?:\s+version)?|clean(?:\s+version)?|" +
        @"(?:\d+(?:st|nd|rd|th)?\s+)?anniversary(?:\s+edition)?|(?:feat|ft|featuring)\.?\s+[^\)\]]*)\s*[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Store suffixes: "Espresso - Single", "Fruitcake - EP".
    private static readonly Regex StoreSuffixRegex = new(
        @"\s+[-–—]\s+(?:single|ep)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A bare trailing "feat. X" with no brackets.
    private static readonly Regex TrailingFeatRegex = new(
        @"\s+(?:feat|ft|featuring)\.?\s+.+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Records that share a title with the real album and are never what people want.
    private static readonly string[] JunkWords =
    {
        "karaoke", "tribute", "lullaby", "lullabies", "made famous", "in the style of",
        "instrumental versions", "piano versions", "8 bit", "8-bit", "music box",
    };

    /// <summary>Lowercase, accents folded, "&amp;" read as "and", punctuation to single spaces.</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            // Apostrophes join a word ("n'", "Taylor's") rather than split it.
            if (c is '\'' or '’' or '‘' or 'ʼ')
                continue;
            sb.Append(c == '&' ? " and " : char.ToLowerInvariant(c));
        }

        var folded = Regex.Replace(sb.ToString(), @"[^\p{L}\p{Nd}]+", " ");
        return Regex.Replace(folded, @"\s+", " ").Trim();
    }

    /// <summary>The title with edition/format wrappers and featured credits removed, normalized.</summary>
    internal static string BaseTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var s = StoreSuffixRegex.Replace(value.Trim(), string.Empty);
        // Repeat: "(Deluxe) [Remastered]" carries two wrappers.
        for (var i = 0; i < 3; i++)
        {
            var next = EditionWrapperRegex.Replace(s, " ");
            if (next == s) break;
            s = next;
        }
        s = TrailingFeatRegex.Replace(s, string.Empty);
        s = StoreSuffixRegex.Replace(s.Trim(), string.Empty);
        return Normalize(s);
    }

    private static string[] Tokens(string normalized)
        => normalized.Length == 0 ? Array.Empty<string>() : normalized.Split(' ');

    /// <summary>How well a store title matches a wanted title, 0..1.</summary>
    internal static double TitleSimilarity(string? candidateTitle, string? wantedTitle)
    {
        var got = Normalize(candidateTitle);
        var want = Normalize(wantedTitle);
        if (got.Length == 0 || want.Length == 0)
            return 0;
        if (got == want)
            return 1.0;

        var gotBase = BaseTitle(candidateTitle);
        var wantBase = BaseTitle(wantedTitle);
        if (gotBase.Length > 0 && gotBase == wantBase)
            return 0.92;

        return Dice(Tokens(gotBase.Length > 0 ? gotBase : got), Tokens(wantBase.Length > 0 ? wantBase : want)) * 0.8;
    }

    /// <summary>
    /// How well a free-text query ("Happier Than Ever Billie Eilish", "billie eilish - happier
    /// than ever", or just the title) names a candidate, 0..1. The title has to be in the query
    /// whole; whatever is left has to be the candidate's artist.
    /// </summary>
    internal static double TypedSimilarity(string? candidateTitle, string? candidateArtist, string? query)
    {
        var titleOnly = TitleSimilarity(candidateTitle, query);
        var q = Tokens(Normalize(query)).ToList();
        if (q.Count == 0)
            return titleOnly;

        // The whole title first ("Short n' Sweet Deluxe Sabrina Carpenter" names the deluxe),
        // then the title without its edition wrapper, a step lower: typing the plain title
        // with the artist has to rank the plain album above its deluxe edition.
        var full = TitleWithArtist(Tokens(Normalize(candidateTitle)), candidateArtist, q, exactTitle: 0.95);
        var plain = TitleWithArtist(Tokens(BaseTitle(candidateTitle)), candidateArtist, q, exactTitle: 0.9);
        return Math.Max(titleOnly, Math.Max(full, plain));
    }

    private static double TitleWithArtist(string[] title, string? candidateArtist, List<string> query, double exactTitle)
    {
        if (title.Length == 0)
            return 0;

        var rest = new List<string>(query);
        var titleHits = 0;
        foreach (var t in title)
        {
            if (rest.Remove(t)) titleHits++;
        }
        rest.RemoveAll(t => t == "by");

        var titleCover = (double)titleHits / title.Length;
        if (rest.Count == 0)
            return titleCover >= 1 ? exactTitle - 0.03 : titleCover * 0.7;

        var artist = new HashSet<string>(Tokens(Normalize(candidateArtist)), StringComparer.Ordinal);
        var restCover = (double)rest.Count(artist.Contains) / rest.Count;
        return titleCover >= 1 && restCover >= 1
            ? exactTitle
            : titleCover * (0.35 + 0.5 * restCover);
    }

    /// <summary>How well a candidate's credit matches the wanted artist, 0..1.</summary>
    internal static double ArtistSimilarity(string? candidateArtist, string? wantedArtist)
    {
        var got = Normalize(candidateArtist);
        var want = Normalize(Track.GetPrimaryArtist(wantedArtist));
        if (got.Length == 0 || want.Length == 0)
            return 0;
        if (got == want)
            return 1.0;
        if ($" {got} ".Contains($" {want} ", StringComparison.Ordinal) ||
            $" {want} ".Contains($" {got} ", StringComparison.Ordinal))
            return 0.85;
        return Dice(Tokens(got), Tokens(want)) * 0.6;
    }

    internal static bool LooksLikeJunk(string? candidateTitle, string? candidateArtist, string? wanted)
    {
        var text = $"{candidateTitle} {candidateArtist}".ToLowerInvariant();
        var asked = (wanted ?? string.Empty).ToLowerInvariant();
        return JunkWords.Any(w => text.Contains(w, StringComparison.Ordinal) && !asked.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// Relevance of one candidate, higher is better. <paramref name="sourceRank"/> is the
    /// position the store's own search gave it and only breaks near-ties.
    /// </summary>
    internal static double Score(
        string? candidateTitle, string? candidateArtist,
        string? wantedTitle, string? wantedArtist, string? preferArtist,
        bool typed, int sourceRank = 0)
    {
        double score;
        if (typed)
        {
            score = TypedSimilarity(candidateTitle, candidateArtist, wantedTitle) * 100;
            // A typed query is about whatever was typed, but between equally good titles the
            // artist of the track being edited is the likelier one.
            score += ArtistSimilarity(candidateArtist, preferArtist) * 8;
        }
        else
        {
            score = TitleSimilarity(candidateTitle, wantedTitle) * 100;
            score += ArtistSimilarity(candidateArtist, wantedArtist) * 40;
        }

        if (LooksLikeJunk(candidateTitle, candidateArtist, wantedTitle))
            score -= 30;

        return score - Math.Max(0, sourceRank) * 0.5;
    }

    /// <summary><see cref="TitleSimilarity"/> at or above this: the same title, or the same one
    /// with only an edition/format wrapper ("(Deluxe)", "- Single") around it.</summary>
    internal const double SameTitle = 0.9;

    /// <summary><see cref="ArtistSimilarity"/> at or above this: the same credit, or one
    /// containing the other as whole words ("Lady Gaga" in "Lady Gaga &amp; Bruno Mars").</summary>
    internal const double SameArtist = 0.85;

    /// <summary>
    /// A small bonus for the store edition whose year and song count agree with the local
    /// album, 0..5. Below the 8-point gap between an exact title and an edition wrapper
    /// (100 vs 92), so it only orders editions that already match equally well: the original
    /// before the 2011 remaster, the 12-song album before its 20-song deluxe. Unknowns (0)
    /// add nothing.
    /// </summary>
    internal static double EditionTieBreak(int candidateYear, int wantedYear, int candidateTracks, int wantedTracks)
    {
        double bonus = 0;
        if (candidateYear > 0 && wantedYear > 0)
        {
            var years = Math.Abs(candidateYear - wantedYear);
            bonus += years == 0 ? 3 : years == 1 ? 1.5 : 0;
        }
        if (candidateTracks > 0 && wantedTracks > 0)
        {
            var tracks = Math.Abs(candidateTracks - wantedTracks);
            bonus += tracks == 0 ? 2 : tracks <= 2 ? 1 : 0;
        }
        return bonus;
    }

    /// <summary>The title part of <see cref="Score"/> on its own, for the relevance cut.</summary>
    internal static double TitleRelevance(string? candidateTitle, string? candidateArtist, string? wantedTitle, bool typed)
        => typed
            ? TypedSimilarity(candidateTitle, candidateArtist, wantedTitle)
            : TitleSimilarity(candidateTitle, wantedTitle);

    private static double Dice(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var pool = new List<string>(b);
        var shared = 0;
        foreach (var t in a)
        {
            if (pool.Remove(t)) shared++;
        }
        return 2.0 * shared / (a.Count + b.Count);
    }
}

/// <summary>What a pasted Apple Music address points at.</summary>
internal enum AppleMusicLinkKind
{
    /// <summary>A bare numeric ID: an album, or a song /lookup resolves to its album.</summary>
    Id,
    Album,
    Song,
    Playlist,
}

/// <summary>
/// A parsed Apple Music link: album (/album/slug/id, ?i= names a track on it), song
/// (/song/slug/id), playlist (/playlist/slug/pl.…) or a bare numeric ID. Only real apple.com
/// hosts count, so an album name that merely contains a number is never read as an ID.
/// </summary>
internal sealed record AppleMusicLink(AppleMusicLinkKind Kind, string Storefront, long Id, string? PageUrl)
{
    private static readonly Regex StorefrontRegex = new("^[a-z]{2}$", RegexOptions.Compiled);

    internal static bool TryParse(string? input, out AppleMusicLink link)
    {
        link = null!;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        if (trimmed.All(char.IsAsciiDigit))
        {
            if (trimmed.Length < 6 || !long.TryParse(trimmed, out var bare))
                return false;
            link = new AppleMusicLink(AppleMusicLinkKind.Id, "us", bare, null);
            return true;
        }

        var text = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !(uri.Host.Equals("apple.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var storefront = segments.Length > 0 && StorefrontRegex.IsMatch(segments[0].ToLowerInvariant())
            ? segments[0].ToLowerInvariant()
            : "us";

        // Links to things that are not an album: their id would look up an artist or a video.
        if (segments.Any(s => s.Equals("artist", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals("music-video", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals("curator", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals("station", StringComparison.OrdinalIgnoreCase)))
            return false;

        var kindIndex = Array.FindIndex(segments, s =>
            s.Equals("album", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("song", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("playlist", StringComparison.OrdinalIgnoreCase));
        var kindWord = kindIndex >= 0 ? segments[kindIndex].ToLowerInvariant() : string.Empty;

        if (kindWord == "playlist")
        {
            var last = segments[^1];
            if (!last.StartsWith("pl.", StringComparison.OrdinalIgnoreCase))
                return false;
            link = new AppleMusicLink(AppleMusicLinkKind.Playlist, storefront, 0,
                $"https://music.apple.com{uri.AbsolutePath}");
            return true;
        }

        // The ID is the last numeric path segment (".../<slug>/1500776322" or ".../id1500776322"),
        // never the first long digit run: the slug is the title and can be a number itself
        // (Vnuk's "1000000 - EP" is /album/1000000-ep/1628626875), and ?i= names a track.
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i].StartsWith("id", StringComparison.OrdinalIgnoreCase)
                ? segments[i][2..]
                : segments[i];
            if (segment.Length < 6 || !segment.All(char.IsAsciiDigit) || !long.TryParse(segment, out var id))
                continue;

            var kind = kindWord == "song" ? AppleMusicLinkKind.Song
                : kindWord == "album" ? AppleMusicLinkKind.Album
                : AppleMusicLinkKind.Id;
            var page = kind == AppleMusicLinkKind.Album && uri.Host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase)
                ? $"https://music.apple.com{uri.AbsolutePath}"
                : null;
            link = new AppleMusicLink(kind, storefront, id, page);
            return true;
        }

        return false;
    }
}

/// <summary>
/// The motion artwork an Apple Music album (or playlist) page carries. Apple serves two crops:
/// <c>videoArtwork → motionDetailSquare</c> (the square loop) and <c>tallVideoArtwork →
/// motionDetailTall</c> (a 3:4 portrait loop for full-screen phones), each with an HLS master
/// and a still preview frame.
/// </summary>
internal sealed record MotionArtworkPage(
    string? SquareVideoUrl,
    string? SquarePosterUrl,
    string? TallVideoUrl,
    string? TallPosterUrl,
    string? Title,
    string? Artist,
    string? ImageUrl)
{
    private static readonly Regex MotionKeyRegex = new(
        "\"motionDetail(?<kind>Square|Tall)\"\\s*:\\s*\\{", RegexOptions.Compiled);

    private static readonly Regex MetaRegex = new(
        "<meta\\s+(?:name|property)=\"(?<k>apple:title|og:image)\"\\s+content=\"(?<v>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeaderArtistRegex = new(
        "\"subtitleLinks\"\\s*:\\s*\\[\\s*\\{\\s*\"title\"\\s*:\\s*\"(?<a>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    internal bool HasMotion => SquareVideoUrl != null || TallVideoUrl != null;

    internal static MotionArtworkPage Parse(string html)
    {
        string? squareVideo = null, squarePoster = null, tallVideo = null, tallPoster = null;

        foreach (Match m in MotionKeyRegex.Matches(html))
        {
            var square = m.Groups["kind"].Value == "Square";
            if (square ? squareVideo != null : tallVideo != null)
                continue; // the page header comes first; later ones belong to other shelves

            var (video, poster) = ReadMotionObject(html, m.Index + m.Length - 1);
            if (video == null || !ITunesArtworkService.IsAppleMediaHost(video))
                continue;
            if (square) { squareVideo = video; squarePoster = poster; }
            else { tallVideo = video; tallPoster = poster; }
        }

        string? title = null, image = null;
        foreach (Match m in MetaRegex.Matches(html))
        {
            var value = WebUtility.HtmlDecode(m.Groups["v"].Value);
            if (m.Groups["k"].Value.Equals("apple:title", StringComparison.OrdinalIgnoreCase)) title ??= value;
            else image ??= value;
        }

        string? artist = null;
        var a = HeaderArtistRegex.Match(html);
        if (a.Success)
        {
            try { artist = JsonSerializer.Deserialize<string>($"\"{a.Groups["a"].Value}\""); }
            catch (JsonException) { artist = a.Groups["a"].Value; }
        }

        return new MotionArtworkPage(squareVideo, squarePoster, tallVideo, tallPoster,
            string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(artist) ? null : artist,
            string.IsNullOrWhiteSpace(image) ? null : image);
    }

    /// <summary>Reads <c>{"previewFrame":{…"url":…},"video":"…"}</c> starting at the brace.</summary>
    private static (string? Video, string? Poster) ReadMotionObject(string html, int braceIndex)
    {
        try
        {
            // One JSON value from here on; whatever follows it on the page is not read.
            var bytes = Encoding.UTF8.GetBytes(html.Substring(braceIndex, Math.Min(html.Length - braceIndex, 64 * 1024)));
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            string? video = null, poster = null;
            if (root.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.String)
                video = v.GetString();
            if (root.TryGetProperty("previewFrame", out var f) && f.ValueKind == JsonValueKind.Object &&
                f.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                poster = u.GetString();
            return (string.IsNullOrWhiteSpace(video) ? null : video, string.IsNullOrWhiteSpace(poster) ? null : poster);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>Fills an Apple image template ("…/{w}x{h}bb.{f}") at the given square size.</summary>
    internal static string? SizeImageTemplate(string? template, int size)
    {
        if (string.IsNullOrWhiteSpace(template))
            return null;
        if (!template.Contains("{w}", StringComparison.Ordinal))
            return template;
        return template
            .Replace("{w}", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{h}", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{c}", "bb", StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);
    }
}
