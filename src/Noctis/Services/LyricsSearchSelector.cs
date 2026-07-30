using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Pure selection/validation logic for picking a lyrics candidate from fuzzy
/// LRCLIB /search results. /search is relevance-ordered but never identity-checked,
/// so a low-ranked different song with a richer lyric format used to beat the exact
/// top match and get persisted to a sidecar. Candidates must now match the local
/// track (duration + names) before any format preference applies.
/// </summary>
public static class LyricsSearchSelector
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

        if (!IsUnknownArtist(artist) && !NamesMatch(candidate.ArtistName, artist))
            return false;

        return true;
    }

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

        return validated.FirstOrDefault(r => r.HasLyricsfile)
            ?? validated.FirstOrDefault(r => r.HasSyncedLyrics)
            ?? validated.FirstOrDefault(r => r.HasLyrics);
    }

    /// <summary>Trimmed, case-insensitive containment either way — tolerates
    /// "Title (Remastered)" vs "Title" without a fuzzy-matching dependency.</summary>
    private static bool NamesMatch(string? candidate, string? local)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(local))
            return false;

        var c = candidate.Trim();
        var l = local.Trim();
        return c.Contains(l, StringComparison.OrdinalIgnoreCase)
            || l.Contains(c, StringComparison.OrdinalIgnoreCase);
    }
}
