using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Noctis.Services.YouTube;

/// <summary>
/// Picks a song's music video out of a YouTube search (Discord #feedback 11-25, "Automatic
/// YouTube Music Video Streaming"). Pure scoring, no I/O:
/// <list type="bullet">
/// <item>the song's title and its artist must be in the video's title (or the artist be the channel);</item>
/// <item>a length close to the song's counts for a lot (a music video may run a little long with an intro);</item>
/// <item>"Official (Music) Video", an artist/VEVO/verified channel and a big view count count for it
/// (re-uploads under the artist's name with a few hundred views were the decoys measured 11-25);</item>
/// <item>lyric videos, live takes, covers, slowed/reverb/sped-up/8D/nightcore edits, karaoke,
/// visualizers, "official audio" and "- Topic" art tracks count against, unless the song's own
/// title says the same (a song titled "… (Live)" wants the live video).</item>
/// </list>
/// Nothing under <see cref="Threshold"/> is used: no video beats a wrong one.
/// </summary>
public static partial class MusicVideoMatcher
{
    public const double Threshold = 0.6;

    /// <summary>Words that make a result a different kind of video than the song's music video.</summary>
    private static readonly string[] Decoys =
    {
        "lyrics", "lyric", "letra", "paroles", "live", "cover", "slowed", "reverb", "sped up", "speed up",
        "8d", "nightcore", "karaoke", "instrumental", "remix", "reaction", "reacts", "visualizer", "visualiser",
        "audio", "1 hour", "10 hours", "extended", "fan made", "fanmade", "tutorial", "lesson", "acoustic",
        "teaser", "trailer", "snippet", "bass boosted", "mashup", "piano", "guitar", "drum", "concert",
    };

    /// <summary>The search query for a song: its main artist, title (without remaster/edition
    /// notes) and "official music video".</summary>
    public static string SearchQuery(string? artist, string? title)
    {
        var a = PrimaryCredit(artist);
        var t = StripNotes(title ?? string.Empty).Trim();
        if (t.Length == 0) t = (title ?? string.Empty).Trim();
        return $"{a} {t} official music video".Trim();
    }

    /// <summary>A result's score for the song (higher is better; see the class remarks).</summary>
    public static double Score(YouTubeVideoInfo video, string? artist, string? title, double trackSeconds)
    {
        var vt = Pad(Normalize(video.Title));
        var ch = Normalize(video.Channel ?? string.Empty);
        var fullTitle = Pad(Normalize(title ?? string.Empty));
        var core = Normalize(StripNotes(title ?? string.Empty));
        if (core.Length == 0) core = fullTitle.Trim();
        var artists = MainArtists(artist).Select(Normalize).Where(a => a.Length > 0).ToList();

        double score = 0;

        // Title and artist.
        score += core.Length == 0 || vt.Contains(Pad(core), StringComparison.Ordinal) ? 0.35 : -0.4;
        var artistInTitle = artists.Any(a => vt.Contains(Pad(a), StringComparison.Ordinal));
        var artistIsChannel = artists.Any(a => SameName(a, ch) || SameName(a + " vevo", ch) || SameName(a + "vevo", ch.Replace(" ", "")));
        score += artistInTitle || artistIsChannel ? 0.25 : -0.35;

        // What kind of video it is.
        if (vt.Contains(" official music video ", StringComparison.Ordinal) || vt.Contains(" official video ", StringComparison.Ordinal))
            score += 0.15;
        else if (vt.Contains(" music video ", StringComparison.Ordinal) || vt.Contains(" mv ", StringComparison.Ordinal))
            score += 0.05;
        foreach (var word in Decoys)
        {
            var w = Pad(word);
            if (vt.Contains(w, StringComparison.Ordinal) && !fullTitle.Contains(w, StringComparison.Ordinal))
            {
                score -= 0.45;
                break; // one is enough to say "not the music video"; more only stack noise
            }
        }

        // Who posted it.
        if (ch.EndsWith(" topic", StringComparison.Ordinal)) score -= 0.5; // auto-generated art track: a still image
        if (artistIsChannel || ch.EndsWith("vevo", StringComparison.Ordinal)) score += 0.1;
        if (video.ChannelVerified) score += 0.1;
        if (video.ViewCount is { } views)
            score += views >= 1_000_000 ? 0.05 : views < 1_000 ? -0.1 : 0;

        // Length.
        if (trackSeconds > 0 && video.DurationSeconds is { } secs)
        {
            var diff = Math.Abs(secs - trackSeconds);
            score += diff <= 5 ? 0.2 : diff <= 15 ? 0.12 : diff <= 45 ? 0 : diff <= 90 ? -0.15 : -0.4;
        }
        return score;
    }

    /// <summary>The best result at or above <see cref="Threshold"/> (YouTube's order breaks
    /// ties), or null.</summary>
    public static YouTubeVideoInfo? PickBest(IReadOnlyList<YouTubeVideoInfo> results, string? artist, string? title, double trackSeconds)
    {
        YouTubeVideoInfo? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var r in results)
        {
            var s = Score(r, artist, title, trackSeconds);
            if (s > bestScore + 1e-9) { best = r; bestScore = s; }
        }
        return bestScore >= Threshold ? best : null;
    }

    /// <summary>Results best first (the "Wrong video?" list).</summary>
    public static IReadOnlyList<YouTubeVideoInfo> Rank(IReadOnlyList<YouTubeVideoInfo> results, string? artist, string? title, double trackSeconds) =>
        results.Select((r, i) => (r, i, s: Score(r, artist, title, trackSeconds)))
            .OrderByDescending(x => x.s).ThenBy(x => x.i).Select(x => x.r).ToList();

    /// <summary>
    /// "artist" + "title", lower-case, accents and punctuation gone: the key a song's choice is
    /// stored under, the same on every machine (so choices could one day be shared).
    /// </summary>
    public static string MatchKey(string? artist, string? title) =>
        Normalize(PrimaryCredit(artist)) + "|" + Normalize(StripNotes(title ?? string.Empty));

    // ── Text ──

    private static string Pad(string s) => " " + s + " ";

    private static bool SameName(string a, string b) =>
        a.Length > 0 && (a == b || a.Replace(" ", "") == b.Replace(" ", ""));

    /// <summary>Lower-case, accents dropped, "&amp;" → "and", everything but letters and digits → one space.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var decomposed = text.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (c == '\'' || c == '’')
            {
                // "don't" matches "dont"
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s+-\s+.*\b(remaster(ed)?|version|edit|mono|stereo|mix)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashNoteRegex();

    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();

    /// <summary>The title without "(Remastered 2011)", "[feat. X]", " - Radio Edit", "feat. X".</summary>
    public static string StripNotes(string title)
    {
        var t = BracketRegex().Replace(title, string.Empty);
        t = DashNoteRegex().Replace(t, string.Empty);
        t = FeatRegex().Replace(t, string.Empty);
        return t.Trim();
    }

    [GeneratedRegex(@"\s*(?:,|;|&|/|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b|\bwith\b|\bvs\.?)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplitRegex();

    /// <summary>The credit without its "feat. X" part ("Simon & Garfunkel" stays whole).</summary>
    public static string PrimaryCredit(string? artist) =>
        FeatRegex().Replace(artist ?? string.Empty, string.Empty).Trim();

    /// <summary>The credit as a whole (without "feat.") and each credited artist in it
    /// ("A feat. B, C" → "A", A, B, C): any of them in the video's title counts.</summary>
    public static IEnumerable<string> MainArtists(string? artist) =>
        string.IsNullOrWhiteSpace(artist)
            ? Enumerable.Empty<string>()
            : new[] { PrimaryCredit(artist) }
                .Concat(ArtistSplitRegex().Split(artist).Select(a => a.Trim()))
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
}
