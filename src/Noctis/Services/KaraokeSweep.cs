using Noctis.Models;

namespace Noctis.Services;

/// <summary>One word of a karaoke share line: the sanitized token as rendered on the
/// card, plus its absolute track-time window in seconds.</summary>
public readonly record struct KaraokeWord(string Token, double StartSeconds, double EndSeconds);

/// <summary>
/// Per-line karaoke timing for the share-clip frame renderer, parallel to
/// <see cref="LyricCardSpec.Lines"/>. Words null/empty → line-level highlight only
/// (lit from <see cref="StartSeconds"/> on; always lit when that is null too).
/// </summary>
public sealed record KaraokeLine
{
    /// <summary>Absolute line start in track seconds; null = unsynced (always lit).</summary>
    public double? StartSeconds { get; init; }

    /// <summary>Sanitized word tokens with timing; null/empty = no word-level data.</summary>
    public IReadOnlyList<KaraokeWord>? Words { get; init; }
}

/// <summary>
/// Pure math for the share-clip karaoke sweep: per-word reveal progress at a point in
/// time, and mapping the card's wrapped text rows back onto a line's word tokens.
/// Deterministic and font-free so it can be unit-tested.
/// </summary>
public static class KaraokeSweep
{
    /// <summary>
    /// Reveal progress of a word at <paramref name="tSeconds"/>: 0 before it starts,
    /// 1 after it ends, else the elapsed fraction. A zero/negative-length word snaps
    /// to 1 the moment it is reached.
    /// </summary>
    public static double WordProgress(double startSeconds, double endSeconds, double tSeconds)
    {
        if (tSeconds < startSeconds) return 0;
        if (endSeconds <= startSeconds) return 1;
        return Math.Clamp((tSeconds - startSeconds) / (endSeconds - startSeconds), 0, 1);
    }

    /// <summary>Progress sentinel for words far ahead of the sweep — renders no band.</summary>
    public const double InertFuture = -2;

    /// <summary>Progress sentinel for words far behind the sweep — renders fully lit.</summary>
    public const double InertPast = 3;

    /// <summary>
    /// Unclamped reveal progress for the live word layer: like <see cref="WordProgress"/>
    /// but it keeps moving a little past both ends of the word, so the feathered edge can
    /// straddle token boundaries — the trailing half of the band finishes crossing a word
    /// while the leading half is already entering the next one (AMLL behaviour). Far
    /// outside the word it snaps to the inert sentinels so distant words render nothing.
    /// </summary>
    public static double BandProgress(double startSeconds, double endSeconds, double tSeconds)
    {
        if (endSeconds <= startSeconds) return tSeconds < startSeconds ? InertFuture : InertPast;
        var raw = (tSeconds - startSeconds) / (endSeconds - startSeconds);
        if (raw <= -1) return InertFuture;
        if (raw >= 2) return InertPast;
        return raw;
    }

    /// <summary>
    /// <see cref="BandProgress"/> for a word built from several timed syllables (Apple
    /// TTML splits "compromise" into com/pro/mise). The word paints as one unbreakable
    /// cell, so a single linear ramp would run ahead of the voice whenever one syllable
    /// is held longer than its share of characters; instead the reveal is weighted by
    /// each syllable's character count and driven by that syllable's own window.
    ///
    /// Outside the word this defers to <see cref="BandProgress"/>, keeping the
    /// overshoot that lets the feathered edge straddle neighbouring words.
    ///
    /// Weighting is by character count, not glyph width — KaraokeSweep stays font-free
    /// so it can be unit-tested. Within one word the error is a fraction of a glyph.
    /// </summary>
    public static double SyllableBandProgress(
        IReadOnlyList<WordSyllable> syllables,
        double startSeconds, double endSeconds, double tSeconds)
    {
        if (syllables.Count == 0 || tSeconds <= startSeconds || tSeconds >= endSeconds)
            return BandProgress(startSeconds, endSeconds, tSeconds);

        double total = 0;
        foreach (var s in syllables) total += s.Length;
        if (total <= 0) return BandProgress(startSeconds, endSeconds, tSeconds);

        double covered = 0;
        for (int i = 0; i < syllables.Count; i++)
        {
            var s = syllables[i];
            var segStart = s.Start.TotalSeconds;
            // A syllable with no end runs to the next one's start (the last to the
            // word's end) — the same resolution the word list itself uses.
            var segEnd = (s.End?.TotalSeconds)
                ?? (i + 1 < syllables.Count ? syllables[i + 1].Start.TotalSeconds : endSeconds);

            if (tSeconds >= segEnd)
            {
                covered += s.Length;
                continue;
            }
            // Before this syllable opens: the playhead is in the rest between
            // syllables, so the reveal simply holds where the last one left it.
            if (tSeconds > segStart && segEnd > segStart)
                covered += s.Length * (tSeconds - segStart) / (segEnd - segStart);
            break;
        }

        return Math.Clamp(covered / total, 0, 1);
    }

    /// <summary>
    /// Sweep end for a line's final word when neither the word nor the line carries an
    /// explicit end time (start-tag-only enhanced LRC). Bounded by the next synced
    /// line's start so the sweep hands off cleanly, and capped at two seconds past the
    /// word's start so a long instrumental gap doesn't stretch the word into a crawl.
    /// Without this bound the span is zero and the word snaps to lit instead of sweeping.
    /// </summary>
    public static TimeSpan ResolveOpenLastWordEnd(TimeSpan wordStart, TimeSpan? nextLineStart)
    {
        var cap = wordStart + TimeSpan.FromSeconds(2);
        return nextLineStart.HasValue && nextLineStart.Value < cap ? nextLineStart.Value : cap;
    }

    /// <summary>
    /// Maps wrapped card rows back to ranges of <paramref name="lineTokens"/>. Each row
    /// must split (on spaces) into exactly the next run of tokens, and together the rows
    /// must consume every token — otherwise null, and the caller degrades that line to a
    /// whole-line highlight (covers user-edited text and hard-broken oversized words).
    /// </summary>
    public static List<(int Start, int Count)>? MapRowsToTokenRanges(
        IReadOnlyList<string> lineTokens, IReadOnlyList<string> rows)
    {
        var ranges = new List<(int Start, int Count)>(rows.Count);
        int offset = 0;
        foreach (var row in rows)
        {
            var rowTokens = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < rowTokens.Length; i++)
            {
                if (offset + i >= lineTokens.Count || rowTokens[i] != lineTokens[offset + i])
                    return null;
            }
            ranges.Add((offset, rowTokens.Length));
            offset += rowTokens.Length;
        }
        return offset == lineTokens.Count ? ranges : null;
    }
}
