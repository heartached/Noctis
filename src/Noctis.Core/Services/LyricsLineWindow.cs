using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Window math for lyrics too long to put on screen at once (an audiobook transcript: Discord,
/// Luwi 11-12, an 8.5 h book of ~15,000 lines). The lyrics lists realize every line they are
/// given — measured on the page, headless Debug: 3,000 lines took 9.3 s and 811 MB to build,
/// 10,000 lines 29 s and 2.7 GB, 30,000 lines 121 s and 8.3 GB, and every line change cost
/// time in proportion (16 / 49 / 143 ms). Past <see cref="LrcParser.ListMaxLines"/> lines the
/// view model keeps them all and shows <see cref="Size"/> of them around the playback position,
/// sliding the window as playback, a seek or the reader's own scrolling approaches its edge.
/// Lyrics of up to <see cref="LrcParser.ListMaxLines"/> lines are never windowed. Pure functions.
/// </summary>
public static class LyricsLineWindow
{
    /// <summary>Lines shown at once (~20 minutes of a spoken book).</summary>
    public const int Size = 600;

    /// <summary>Where a window placed around the sung line puts it: this many lines from the
    /// window's top, so twice as much of what comes next is already on the page.</summary>
    public const int Behind = Size / 3;

    /// <summary>At a quiet moment (no line change for a while), the window slides once fewer
    /// than this many lines are left ahead of (or behind) the sung line: small steps of
    /// ~<see cref="Size"/> − <see cref="Behind"/> − <see cref="SoftAhead"/> lines, each built while
    /// nothing on screen is moving.</summary>
    public const int SoftAhead = 350;

    /// <summary>Lines kept behind the sung line before a quiet slide back.</summary>
    public const int SoftBehind = 60;

    /// <summary>The window slides at once, quiet or not, when the sung line is this close to
    /// (or past) either edge: a seek, or lines too fast for a quiet moment.</summary>
    public const int HardMargin = 20;

    /// <summary>Lines the window moves when the reader scrolls to one of its edges.</summary>
    public const int ScrollStep = 200;

    /// <summary>More lines than a list shows at once: the view model windows them.</summary>
    public static bool IsWindowed(int total) => total > LrcParser.ListMaxLines;

    /// <summary>
    /// Index of the last synced line that starts at or before <paramref name="position"/> — the
    /// line playback is in — or 0 before the first. Lines are in start order with untimed ones
    /// after them (as the parsers sort them); binary search over the timed run.
    /// </summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        var hi = lines.Count - 1;
        while (hi >= 0 && lines[hi].Timestamp is null) hi--;
        int lo = 0, found = 0;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Timestamp is { } t && t <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        return found;
    }

    /// <summary>The window start that puts line <paramref name="index"/> <see cref="Behind"/>
    /// lines from the top, kept inside the list.</summary>
    public static int StartAround(int index, int total) =>
        Math.Clamp(index - Behind, 0, Math.Max(0, total - Size));

    /// <summary>The window's line count at <paramref name="start"/>.</summary>
    public static int CountAt(int start, int total) => Math.Max(0, Math.Min(Size, total - start));

    /// <summary>
    /// Where the window starting at <paramref name="start"/> should move for playback in line
    /// <paramref name="index"/>, or null to stay. Outside the window or within
    /// <see cref="HardMargin"/> of an edge: now. Fewer than <see cref="SoftAhead"/> lines ahead
    /// or <see cref="SoftBehind"/> behind: only when <paramref name="quiet"/>. Never past either
    /// end of the list, so the first and last lines sit at the window's edges without a slide.
    /// </summary>
    public static int? PlaybackMove(int start, int total, int index, bool quiet)
    {
        if (!IsWindowed(total)) return null;
        var end = start + CountAt(start, total);
        var ahead = end - 1 - index;
        var behind = index - start;
        var atTop = start == 0;
        var atBottom = end >= total;
        var hard = index < start || index >= end
                   || (ahead < HardMargin && !atBottom) || (behind < HardMargin && !atTop);
        var soft = (ahead < SoftAhead && !atBottom) || (behind < SoftBehind && !atTop);
        if (!hard && !(soft && quiet)) return null;
        var next = StartAround(index, total);
        return next == start ? null : next;
    }

    /// <summary>The window start <see cref="ScrollStep"/> lines up (<paramref name="direction"/>
    /// &lt; 0) or down from <paramref name="start"/>, kept inside the list; null at that end.</summary>
    public static int? ScrollMove(int start, int total, int direction)
    {
        if (!IsWindowed(total) || direction == 0) return null;
        var next = Math.Clamp(start + Math.Sign(direction) * ScrollStep, 0, Math.Max(0, total - Size));
        return next == start ? null : next;
    }
}
