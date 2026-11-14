namespace Noctis.Services.LyricsStudio;

/// <summary>Which times a nudge moves: start and end together, or one edge.</summary>
public enum NudgeEdge { Whole, Start, End }

/// <summary>
/// One review line's times as the Studio keeps them: word starts in order (a word ends where
/// the next one starts, the last one at <see cref="End"/>), so moving one word's end is moving
/// the next word's start.
/// </summary>
public sealed record NudgeLine(IReadOnlyList<TimeSpan> WordStarts, TimeSpan End)
{
    public bool SameTimes(NudgeLine other) => End == other.End && WordStarts.SequenceEqual(other.WordStarts);
}

/// <summary>What stopped a nudge short of its step (GitHub #237).</summary>
public enum NudgeLimit
{
    /// <summary>It moved the full step.</summary>
    None,
    /// <summary>0:00, the start of the song.</summary>
    Zero,
    /// <summary>The song's end.</summary>
    SongEnd,
    /// <summary>The previous line's start (lines keep their order).</summary>
    PreviousLine,
    /// <summary>The next line's start.</summary>
    NextLine,
    /// <summary>The word before (for a line's end: its last word).</summary>
    PreviousWord,
    /// <summary>The word after.</summary>
    NextWord,
    /// <summary>The word's own start: its end cannot go before it.</summary>
    ThisWord,
    /// <summary>The line's own start (a spread line's end keeps 10 ms per word).</summary>
    LineStart,
    /// <summary>The line's own end.</summary>
    LineEnd,
}

/// <summary>Which limit stopped a nudge, and the time it sits at (the neighbour's start, the line end, the song's end).</summary>
public readonly record struct NudgeClamp(NudgeLimit Limit, TimeSpan At)
{
    public static NudgeClamp None => default;
    public bool IsClamped => Limit != NudgeLimit.None;
}

/// <summary>
/// The rules behind the Studio's nudge buttons and keys (user ask, Discord "Manual lyrics
/// editing" 10-14). One nudge moves the selected line, or one word of it, by a step:
/// <list type="bullet">
/// <item>Clamp, never push: only the chosen times move. A nudge that would cross a limit stops
/// at it; a time already past that limit (a file with crossed lines) stays put rather than
/// jump, and a nudge away from it moves by the step only.</item>
/// <item>Nothing goes below 0:00 or past the song's end (when its length is known).</item>
/// <item>A line's start stays between the previous and next lines' starts, so lines never
/// change order. A line's end may run into the next line (duets and overlapping lines are
/// real); it only stays after its own last word.</item>
/// <item>A word stays inside its line and in order: every word keeps at least
/// <see cref="Gap"/> (10 ms, the precision LRC writes) after the one before it, and the line
/// end stays at least that far past its last word.</item>
/// <item>A whole line moves with its words; a line edge on a line with no word timings
/// (words spread evenly) re-spreads its words over the new span.</item>
/// </list>
/// </summary>
public static class TimingNudge
{
    /// <summary>The least time kept between neighbouring starts, and between a last word and its line end.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(10);

    /// <summary>The small step (LRC's 10 ms precision) and the large one.</summary>
    public static readonly TimeSpan FineStep = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan CoarseStep = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Unbounded = TimeSpan.MaxValue / 4;

    /// <summary>
    /// The line's times after one nudge; the same times when nothing may move.
    /// </summary>
    /// <param name="wordIndex">The word to move, or -1 for the whole line.</param>
    /// <param name="previousStart">The previous line's start; null for the first line.</param>
    /// <param name="nextStart">The next line's start; null for the last line.</param>
    /// <param name="duration">The song's length; zero or less when unknown (no upper limit).</param>
    /// <param name="spread">The line's words are an even spread, not timed: a line edge re-spreads them.</param>
    public static NudgeLine Apply(NudgeLine line, int wordIndex, NudgeEdge edge, TimeSpan delta,
        TimeSpan? previousStart, TimeSpan? nextStart, TimeSpan duration, bool spread = false) =>
        Apply(line, wordIndex, edge, delta, previousStart, nextStart, duration, out _, spread);

    /// <summary>
    /// The same nudge, also telling which limit stopped it short of its step (GitHub #237: a
    /// blocked nudge said nothing); <see cref="NudgeClamp.None"/> when it moved the full step.
    /// </summary>
    public static NudgeLine Apply(NudgeLine line, int wordIndex, NudgeEdge edge, TimeSpan delta,
        TimeSpan? previousStart, TimeSpan? nextStart, TimeSpan duration, out NudgeClamp clamp, bool spread = false)
    {
        clamp = NudgeClamp.None;
        var s = line.WordStarts.ToArray();
        var end = line.End;
        var n = s.Length;
        if (n == 0 || delta == TimeSpan.Zero || wordIndex >= n) return line;

        var floor = previousStart is { } p && p + Gap > TimeSpan.Zero
            ? new Bound(p + Gap, NudgeLimit.PreviousLine, p)
            : new Bound(TimeSpan.Zero, NudgeLimit.Zero, TimeSpan.Zero);
        var endCeiling = new Bound(duration > TimeSpan.Zero ? duration : Unbounded, NudgeLimit.SongEnd, duration);
        var startCeiling = nextStart is { } q ? new Bound(q - Gap, NudgeLimit.NextLine, q) : endCeiling.Shift(-Gap);
        var lineEnd = new Bound(end, NudgeLimit.LineEnd, end);

        if (wordIndex < 0)
        {
            switch (edge)
            {
                case NudgeEdge.Whole:
                {
                    var d = Move(TimeSpan.Zero, delta, floor.Shift(-s[0]), Lower(startCeiling.Shift(-s[0]), endCeiling.Shift(-end)), ref clamp);
                    for (var i = 0; i < n; i++) s[i] += d;
                    end += d;
                    break;
                }
                case NudgeEdge.Start when spread:
                {
                    var start = Move(s[0], delta, floor, Lower(startCeiling, lineEnd.Shift(-Gap * n)), ref clamp);
                    Spread(s, start, end);
                    break;
                }
                case NudgeEdge.Start:
                    s[0] = Move(s[0], delta, floor, Lower(startCeiling, (n > 1 ? new Bound(s[1], NudgeLimit.NextWord, s[1]) : lineEnd).Shift(-Gap)), ref clamp);
                    break;
                case NudgeEdge.End when spread:
                    end = Move(end, delta, new Bound(s[0] + Gap * n, NudgeLimit.LineStart, s[0]), endCeiling, ref clamp);
                    Spread(s, s[0], end);
                    break;
                default:
                    end = Move(end, delta, new Bound(s[^1] + Gap, NudgeLimit.PreviousWord, s[^1]), endCeiling, ref clamp);
                    break;
            }
            return Result(line, s, end);
        }

        var w = wordIndex;
        // Where this word's start may go: after the word before it (or, for the first word,
        // after the previous line's start); before the word after it (or the line end).
        var startLow = w == 0 ? floor : new Bound(s[w - 1] + Gap, NudgeLimit.PreviousWord, s[w - 1]);
        var startHigh = (w + 1 < n ? new Bound(s[w + 1], NudgeLimit.NextWord, s[w + 1]) : lineEnd).Shift(-Gap);
        if (w == 0) startHigh = Lower(startHigh, startCeiling);
        // Where its end may go: the next word's start, kept before the word after that (or the
        // line end); for the last word, the line end itself.
        var endHigh = w + 1 < n
            ? (w + 2 < n ? new Bound(s[w + 2], NudgeLimit.NextWord, s[w + 2]) : lineEnd).Shift(-Gap)
            : endCeiling;
        var ownStart = new Bound(s[w] + Gap, NudgeLimit.ThisWord, s[w]);

        switch (edge)
        {
            case NudgeEdge.Start:
                s[w] = Move(s[w], delta, startLow, startHigh, ref clamp);
                break;
            case NudgeEdge.End when w + 1 < n:
                s[w + 1] = Move(s[w + 1], delta, ownStart, endHigh, ref clamp);
                break;
            case NudgeEdge.End:
                end = Move(end, delta, ownStart, endHigh, ref clamp);
                break;
            default:
            {
                var wordEnd = w + 1 < n ? s[w + 1] : end;
                var high = endHigh.Shift(-wordEnd);
                if (w == 0) high = Lower(high, startCeiling.Shift(-s[w]));
                var d = Move(TimeSpan.Zero, delta, startLow.Shift(-s[w]), high, ref clamp);
                s[w] += d;
                if (w + 1 < n) s[w + 1] += d;
                else end += d;
                break;
            }
        }
        return Result(line, s, end);
    }

    /// <summary>One limit of a move: where it is, what it is, and the time to name for it.</summary>
    private readonly record struct Bound(TimeSpan Value, NudgeLimit Limit, TimeSpan At)
    {
        public Bound Shift(TimeSpan by) => this with { Value = Value + by };
    }

    private static Bound Lower(Bound a, Bound b) => a.Value <= b.Value ? a : b;

    /// <summary>
    /// <paramref name="value"/> moved by <paramref name="delta"/>: a later move stops at
    /// <paramref name="high"/>, an earlier one at <paramref name="low"/>. A value already past
    /// the limit it moves toward stays put; one past the other limit moves back by the step only.
    /// A stop short of the step names its limit in <paramref name="clamp"/>.
    /// </summary>
    private static TimeSpan Move(TimeSpan value, TimeSpan delta, Bound low, Bound high, ref NudgeClamp clamp)
    {
        var target = value + delta;
        if (delta > TimeSpan.Zero && target > high.Value)
        {
            clamp = new NudgeClamp(high.Limit, high.At);
            return Max(value, high.Value);
        }
        if (delta < TimeSpan.Zero && target < low.Value)
        {
            clamp = new NudgeClamp(low.Limit, low.At);
            return Min(value, low.Value);
        }
        return target;
    }

    private static void Spread(TimeSpan[] starts, TimeSpan start, TimeSpan end)
    {
        var slice = (end - start) / starts.Length;
        for (var i = 0; i < starts.Length; i++) starts[i] = start + slice * i;
    }

    private static NudgeLine Result(NudgeLine line, TimeSpan[] starts, TimeSpan end)
    {
        var moved = new NudgeLine(starts, end);
        return moved.SameTimes(line) ? line : moved;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
