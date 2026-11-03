using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Pure selection/validation logic for picking a lyrics candidate from fuzzy
/// LRCLIB /search results. /search is relevance-ordered but never identity-checked,
/// so a low-ranked different song with a richer lyric format used to beat the exact
/// top match and get persisted to a sidecar. Candidates must now match the local
/// track (duration + names) before any format preference applies.
/// </summary>
public static partial class LyricsSearchSelector
{
    /// <summary>Max allowed |candidate − local| duration difference, in seconds.</summary>
    public const double DurationToleranceSeconds = 5.0;

    /// <summary>True when the artist is missing or the "Unknown Artist" library placeholder
    /// (the <see cref="Track.Artist"/> default) — not a name worth sending to a lyrics API.</summary>
    public static bool IsUnknownArtist(string? artist) =>
        string.IsNullOrWhiteSpace(artist) ||
        string.Equals(artist.Trim(), "Unknown Artist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates a search candidate against the local track: duration within
    /// <see cref="DurationToleranceSeconds"/> when both durations are known, plus a
    /// case-insensitive title containment check (and artist, when the artist is known).
    /// </summary>
    public static bool MatchesTrack(LrcLibResult candidate, string? artist, string? title, double durationSeconds)
    {
        if (candidate.Duration > 0 && durationSeconds > 0 &&
            Math.Abs(candidate.Duration - durationSeconds) > DurationToleranceSeconds)
            return false;

        if (!NamesMatch(candidate.TrackName, title))
            return false;

        if (!IsUnknownArtist(artist) && !NamesMatch(candidate.ArtistName, artist) && !ShareAnArtist(candidate.ArtistName, artist))
            return false;

        return true;
    }

    /// <summary>
    /// Two credits naming one of the same artists, each split on the usual joins (", " "&amp;" ";"
    /// "/" "feat."): lrc.red's "Lady Gaga, Bruno Mars" is a library's "Lady Gaga &amp; Bruno Mars".
    /// Containment alone missed every duet credited with another separator. Whole names only —
    /// "Bruno Major" is not "Bruno Mars".
    /// </summary>
    private static bool ShareAnArtist(string? candidate, string? local)
    {
        var mine = SplitCredit(local);
        return mine.Length > 0 && SplitCredit(candidate).Any(c => mine.Contains(c, StringComparer.OrdinalIgnoreCase));
    }

    private static string[] SplitCredit(string? credit) => string.IsNullOrWhiteSpace(credit)
        ? Array.Empty<string>()
        : CreditJoins().Split(credit).Select(a => a.Trim()).Where(a => a.Length > 1).ToArray();

    [GeneratedRegex(@"\s*(?:,|&|;|/|\+|\bfeat\b\.?|\bft\.|\bfeaturing\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CreditJoins();

    /// <summary>
    /// Picks the best candidate that validates against the local track, skipping
    /// instrumental entries. Format preference (word-level > synced > any, or synced-only
    /// when <paramref name="requireSynced"/>) applies within the validated subset.
    /// Returns null when nothing validates — no lyrics beats the wrong song's lyrics.
    /// </summary>
    public static LrcLibResult? PickFromSearchResults(
        IEnumerable<LrcLibResult> results, string? artist, string? title, double durationSeconds,
        bool requireSynced = false)
    {
        var validated = results
            .Where(r => !r.Instrumental && MatchesTrack(r, artist, title, durationSeconds))
            .ToList();

        if (requireSynced)
            return validated.FirstOrDefault(r => r.HasSyncedLyrics);

        return validated.FirstOrDefault(r => r.HasWordSyncedLyricsfile)
            ?? validated.FirstOrDefault(r => r.HasSyncedLyrics || r.HasLyricsfile)
            ?? validated.FirstOrDefault(r => r.HasLyrics);
    }

    /// <summary>
    /// GitHub #162: lyrics that only say the song is instrumental. NetEase and Kugou answer an
    /// instrumental song (or a wrong match pointing at one) with "纯音乐，请欣赏" ("instrumental,
    /// please enjoy"), sometimes under credit lines ("作曲 : …"); shown as lyrics it was the
    /// reporter's junk Chinese line. True when every lyric line is that notice or a credit.
    /// </summary>
    public static bool IsInstrumentalPlaceholder(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return false;
        var notice = false;
        foreach (var raw in lyrics.Split('\n'))
        {
            var line = TimeTags().Replace(raw, string.Empty).Trim();
            if (line.Length == 0) continue;
            var letters = new string(line.Where(char.IsLetterOrDigit).ToArray());
            if (letters.Contains("纯音乐") && letters.Contains("欣赏")) { notice = true; continue; }
            if (CreditLine().IsMatch(line)) continue;
            return false;
        }
        return notice;
    }

    // [mm:ss.xx] / <mm:ss.xx> timing tags and [ar:…]-style header tags.
    [GeneratedRegex(@"\[[^\]]*\]|<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>")]
    private static partial Regex TimeTags();

    // "作曲 : Name", "Composer: Name" — a short label, then a colon.
    [GeneratedRegex(@"^[^:：]{1,12}\s*[:：]")]
    private static partial Regex CreditLine();

    /// <summary>Rank of a word-synced answer — the top of <see cref="FormatRank"/>.</summary>
    public const int WordSyncedRank = 3;

    /// <summary>
    /// How rich a result's lyrics are, for picking between sources: word-synced (a Lyricsfile that
    /// times words, or synced text carrying ELRC word tags) 3, line-synced 2, plain 1, nothing 0.
    /// </summary>
    public static int FormatRank(LrcLibResult? result)
    {
        if (result == null) return 0;
        if (result.HasWordSyncedLyricsfile) return WordSyncedRank;
        if (result.HasSyncedLyrics)
            return EnhancedLrcParser.ContainsWordTags(result.SyncedLyrics) ? WordSyncedRank : 2;
        // A Lyricsfile without word timings still times its lines.
        if (result.HasLyricsfile) return 2;
        return result.HasLyrics ? 1 : 0;
    }

    /// <summary>Trimmed, case-insensitive containment either way — tolerates
    /// "Title (Remastered)" vs "Title" without a fuzzy-matching dependency. Both sides in
    /// Unicode form C: a vowel sign typed as its two parts (Malayalam "ൊ" as "െ" + "ാ", GitHub
    /// #149) or a decomposed "é" is the same name, and ordinal containment saw two.</summary>
    private static bool NamesMatch(string? candidate, string? local)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(local))
            return false;

        var c = candidate.Trim().Normalize(System.Text.NormalizationForm.FormC);
        var l = local.Trim().Normalize(System.Text.NormalizationForm.FormC);
        return c.Contains(l, StringComparison.OrdinalIgnoreCase)
            || l.Contains(c, StringComparison.OrdinalIgnoreCase);
    }
}
