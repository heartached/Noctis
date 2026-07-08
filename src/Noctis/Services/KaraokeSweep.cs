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
