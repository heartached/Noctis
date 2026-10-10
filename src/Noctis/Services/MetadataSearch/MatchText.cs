using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// Text normalization and similarity for scoring search candidates (owner 10-08: Search metadata
/// revamp). Store titles carry decoration the user's tags often don't — "(feat. X)", "- Remastered
/// 2011", "(Radio Edit)" — so titles are compared on their base form, and the version markers
/// are compared separately: a remaster is the same song, a live take or a remix is not.
/// </summary>
public static partial class MatchText
{
    // Markers that make a different recording (or an imitation of it). A query without the
    // marker should not settle on a candidate that has one, and vice versa.
    private static readonly string[] DistinctMarkers =
    {
        "live", "remix", "mix", "edit", "acoustic", "instrumental", "acapella", "a cappella",
        "karaoke", "demo", "unplugged", "extended", "radio", "club", "dub", "slowed", "sped up",
        "reverb", "cover", "made famous", "tribute", "originally performed", "lullaby", "rehearsal",
        "session", "drumless", "nightcore", "remixed",
    };

    // Words that only decorate a segment (same audio or a negligible difference). Segments made
    // of these are stripped from the base title without counting as a different version.
    private static readonly HashSet<string> BenignWords = new(StringComparer.Ordinal)
    {
        "remaster", "remastered", "remasterizado", "deluxe", "super", "special", "expanded", "edition",
        "anniversary", "version", "explicit", "clean", "album", "single", "mono", "stereo", "bonus",
        "track", "original", "digital", "lp", "ep", "collector", "collectors", "limited", "complete",
        "definitive", "reissue", "release", "year", "years", "studio", "mix", "mixed",
    };

    // Filler that never identifies a version on its own.
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "from", "for", "by", "in", "on", "at", "to", "with", "s",
    };

    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s+[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketFeaturing();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFeaturing();

    [GeneratedRegex(@"[\(\[]([^\)\]]*)[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketSegment();

    [GeneratedRegex(@"\s+[-–—]\s+([^-–—]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DashSegment();

    [GeneratedRegex(@"\s*(?:,|&|;|/|\+|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|\bvs\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistSeparators();

    /// <summary>Lower-case letters and digits only, diacritics folded ("Beyoncé" == "beyonce"),
    /// "&amp;" read as "and". Non-Latin scripts keep their letters.</summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var decomposed = s.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Removes featured-artist credits from a title.</summary>
    public static string StripFeaturing(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var s = BracketFeaturing().Replace(title, string.Empty);
        return TrailingFeaturing().Replace(s, string.Empty).Trim();
    }

    /// <summary>
    /// Splits a title into its folded base and the "different recording" markers found in its
    /// bracketed or " - " suffix segments: "One More Time (Short Radio Edit)" → ("onemoretime",
    /// {radio, edit}); "Song - Remastered 2011" → ("song", {}).
    /// </summary>
    public static (string Base, IReadOnlySet<string> Markers) AnalyzeTitle(string? title)
    {
        var markers = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(title)) return (string.Empty, markers);

        var s = StripFeaturing(title);
        s = BracketSegment().Replace(s, m => IsVersionSegment(m.Groups[1].Value, markers) ? " " : m.Value);
        // Repeat for stacked suffixes ("Song - Live - 2004 Remaster").
        for (var i = 0; i < 3; i++)
        {
            var m = DashSegment().Match(s);
            if (!m.Success || !IsVersionSegment(m.Groups[1].Value, markers)) break;
            s = s[..m.Index];
        }

        var folded = Fold(s);
        return (folded.Length > 0 ? folded : Fold(title), markers);
    }

    // True when a suffix segment describes a version/edition rather than being part of the title.
    // Distinct markers it contains are collected. A segment carrying only decoration keywords
    // ("Edition", "Version") still names a different take when it has other words in it:
    // "(Drumless Edition)", "(Taylor's Version)", "(Salsa Version)" — those words become the marker,
    // while "(2011 Remaster)" and "(10th Anniversary Edition)" add nothing.
    private static bool IsVersionSegment(string segment, HashSet<string> markers)
    {
        var lower = " " + segment.ToLowerInvariant() + " ";
        var distinct = new List<string>();
        foreach (var m in DistinctMarkers)
        {
            if (!ContainsWord(lower, m)) continue;
            // "Original Mix" / "Album Mix" are the plain recording.
            if (m == "mix" && IsPlainVersion(lower)) continue;
            distinct.Add(m == "a cappella" ? "acapella" : m);
        }

        var words = WordSplit().Split(lower).Where(w => w.Length > 0).ToList();
        var benign = words.Any(BenignWords.Contains);
        var yearOnly = Regex.IsMatch(segment.Trim(), @"^(?:\d{4})$");
        if (distinct.Count == 0 && !benign && !yearOnly) return false;

        if (distinct.Count > 0)
        {
            markers.UnionWith(distinct);
            return true;
        }
        var extra = words.Where(w => !BenignWords.Contains(w) && !FillerWords.Contains(w) && !IsNumberish(w)).ToList();
        if (extra.Count > 0) markers.Add(string.Join(" ", extra));
        return true;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordSplit();

    // "2011", "10th", "25", "1st" — dates and ordinals of editions.
    private static bool IsNumberish(string w)
        => w.Length > 0 && char.IsDigit(w[0]) && w.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') is "" or "st" or "nd" or "rd" or "th";

    private static bool IsPlainVersion(string lower)
        => ContainsWord(lower, "original mix") || ContainsWord(lower, "album mix") ||
           ContainsWord(lower, "stereo mix") || ContainsWord(lower, "mono mix");

    private static bool ContainsWord(string haystack, string word)
    {
        var idx = 0;
        while ((idx = haystack.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            var before = idx == 0 || !char.IsLetter(haystack[idx - 1]);
            var end = idx + word.Length;
            var after = end >= haystack.Length || !char.IsLetter(haystack[end]);
            if (before && after) return true;
            idx = end;
        }
        return false;
    }

    /// <summary>Similarity in [0,1] from Levenshtein distance over already-folded strings.</summary>
    public static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (a == b) return 1.0;
        var maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)FuzzyTrackMatcher.Levenshtein(a, b) / maxLen;
    }

    /// <summary>Base-title similarity (feat./remaster/edition decoration ignored).</summary>
    public static double TitleSimilarity(string? a, string? b)
        => Ratio(AnalyzeTitle(a).Base, AnalyzeTitle(b).Base);

    /// <summary>Album similarity with edition suffixes stripped (AlbumTitleNormalizer) and the
    /// raw titles compared too, so "Discovery (Deluxe)" matches "Discovery" and a genuinely
    /// suffixed title still matches itself exactly.</summary>
    public static double AlbumSimilarity(string? a, string? b)
    {
        var raw = Ratio(Fold(a), Fold(b));
        // AlbumTitleNormalizer also drops iTunes' " - Single" / " - EP" naming. A match only
        // after stripping editions ranks just under an exact one, so "Album" prefers "Album"
        // over "Album (Drumless Edition)".
        var norm = Ratio(Fold(AlbumTitleNormalizer.Normalize(a)), Fold(AlbumTitleNormalizer.Normalize(b)));
        return Math.Max(raw, norm * 0.97);
    }

    /// <summary>Individual credited names: "A feat. B &amp; C" → [A, B, C] (folded).</summary>
    public static IReadOnlyList<string> SplitArtists(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<string>();
        return ArtistSeparators().Split(artist)
            .Select(Fold)
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Artist similarity: the whole credit compared, or the best primary-name pairing — so
    /// "Daft Punk" matches "Daft Punk feat. Pharrell Williams" and "Beyonce" matches "Beyoncé".
    /// </summary>
    public static double ArtistSimilarity(string? a, string? b)
    {
        var whole = Ratio(Fold(a), Fold(b));
        var na = SplitArtists(a);
        var nb = SplitArtists(b);
        if (na.Count == 0 || nb.Count == 0) return whole;
        // Primary names carry most of the identity; any other overlap is a weaker signal.
        var primary = Ratio(na[0], nb[0]);
        var best = 0.0;
        foreach (var x in na)
            foreach (var y in nb)
                best = Math.Max(best, Ratio(x, y));
        return Math.Max(whole, Math.Max(primary, best * 0.9));
    }

    /// <summary>Uppercase/hyphen-insensitive ISRC compare ("GB-DUW-00-00053" == "GBDUW0000053").</summary>
    public static bool SameIsrc(string? a, string? b)
    {
        var x = NormalizeCode(a);
        var y = NormalizeCode(b);
        return x.Length >= 12 && x == y;
    }

    /// <summary>Barcode compare ignoring leading zeros (UPC-A vs EAN-13 of the same code).</summary>
    public static bool SameBarcode(string? a, string? b)
    {
        var x = NormalizeCode(a).TrimStart('0');
        var y = NormalizeCode(b).TrimStart('0');
        return x.Length >= 8 && x == y;
    }

    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var sb = new StringBuilder(code.Length);
        foreach (var ch in code)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Duration closeness in [0,1]: within 2 s is the same edit, beyond 30 s is not.</summary>
    public static double DurationScore(TimeSpan a, TimeSpan b)
    {
        var d = Math.Abs((a - b).TotalSeconds);
        if (d <= 2) return 1.0;
        if (d <= 5) return 0.85;
        if (d <= 10) return 0.6;
        if (d <= 30) return 0.25;
        return 0.0;
    }

    /// <summary>Parses "yyyy", "yyyy-MM", "yyyy-MM-dd" or an ISO timestamp into the contract's date
    /// text and year. Returns ("", null) for anything else.</summary>
    public static (string Date, int? Year) ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4) return (string.Empty, null);
        if (!int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year < 1000)
            return (string.Empty, null);
        var date = value.Length >= 10 && value[4] == '-' && value[7] == '-' ? value[..10]
                 : value.Length >= 7 && value[4] == '-' ? value[..7]
                 : value[..4];
        // Deezer reports unknown dates as "0000-00-00"; MusicBrainz never does, but guard anyway.
        if (date.EndsWith("-00-00", StringComparison.Ordinal)) date = date[..4];
        else if (date.EndsWith("-00", StringComparison.Ordinal)) date = date[..7];
        return (date, year);
    }
}
