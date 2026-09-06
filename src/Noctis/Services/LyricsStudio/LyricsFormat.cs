using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services.LyricsStudio;

/// <summary>What a track's lyrics already carry: nothing, plain text, line-timed LRC, or word-timed enhanced LRC.</summary>
public enum LyricsFormat
{
    None,
    Plain,
    /// <summary>One <c>[mm:ss.xx]</c> time per line.</summary>
    Lrc,
    /// <summary>Line times plus inline <c>&lt;mm:ss.xx&gt;</c> word tags.</summary>
    Elrc,
}

/// <summary>
/// Tells LRC and ELRC apart. Lyrics Studio uses it to label each queued song, to decide
/// whether a song already has the format being produced, and to pick the songs the
/// library-wide entry points offer.
/// </summary>
public static class LyricsFormatDetector
{
    public static LyricsFormat Detect(string? plain, string? synced)
    {
        if (!string.IsNullOrWhiteSpace(synced) && LyricsTextHelper.ContainsTimestamps(synced))
            return HasWordTags(synced) ? LyricsFormat.Elrc : LyricsFormat.Lrc;
        if (!string.IsNullOrWhiteSpace(synced) || !string.IsNullOrWhiteSpace(plain))
            return LyricsFormat.Plain;
        return LyricsFormat.None;
    }

    /// <summary>Sidecar-aware: a .elrc / .lrc next to the file wins over the stored text.</summary>
    public static LyricsFormat Detect(Track track) => ExistingLyricsLoader.DetectFormat(track);

    /// <summary>True when <paramref name="existing"/> already gives what a run with this output setting would write.</summary>
    public static bool AlreadyHas(LyricsFormat existing, bool wordTimings) =>
        wordTimings ? existing == LyricsFormat.Elrc : existing is LyricsFormat.Lrc or LyricsFormat.Elrc;

    public static string Label(LyricsFormat format) => format switch
    {
        LyricsFormat.Elrc => "has word timings (ELRC)",
        LyricsFormat.Lrc => "has line timings (LRC)",
        LyricsFormat.Plain => "plain lyrics, no timings",
        _ => "no lyrics yet",
    };

    /// <summary>A word tag on any line that also carries a line timestamp — a lone stray tag in plain text does not make a file ELRC.</summary>
    private static bool HasWordTags(string synced)
    {
        foreach (var raw in synced.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '[') continue;
            var close = line.IndexOf(']');
            if (close < 0) continue;
            if (EnhancedLrcParser.ContainsWordTags(line[(close + 1)..])) return true;
        }
        return false;
    }
}
