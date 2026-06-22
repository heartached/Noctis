namespace Noctis.Services;

/// <summary>One lyric line as seen by the clip-timing calculator: its playback
/// timestamp (null when the lyrics aren't synced) and whether it's selected.</summary>
public readonly record struct ShareClipLine(TimeSpan? Timestamp, bool Selected);

/// <summary>
/// The audio window for a lyric share clip: where to start in the song and how long
/// to play. Pure/deterministic so it can be unit-tested without audio or ffmpeg.
/// </summary>
public readonly record struct ShareClipTiming(double StartSeconds, double DurationSeconds)
{
    /// <summary>Floor so a single short line isn't a jarringly brief clip.</summary>
    public const double MinSeconds = 6;
    /// <summary>Cap so a long selection still produces a postable clip.</summary>
    public const double MaxSeconds = 60;
    /// <summary>Used when the end can't be derived (unsynced, or final line selected).</summary>
    public const double FallbackSeconds = 15;

    /// <summary>
    /// Synced: start at the earliest selected line's timestamp and run until the line
    /// after the last selected line begins (clamped to [Min, Max]); if the selection
    /// reaches the final line there's no next timestamp, so use the fallback length.
    /// Unsynced: start at the current playback position (or 0) for the fallback length.
    /// </summary>
    public static ShareClipTiming Compute(IReadOnlyList<ShareClipLine> lines, double? currentPositionSeconds)
    {
        double fallbackStart = Math.Max(0, currentPositionSeconds is > 0 ? currentPositionSeconds.Value : 0);

        var selectedIndices = new List<int>();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Selected) selectedIndices.Add(i);

        if (selectedIndices.Count == 0)
            return new ShareClipTiming(fallbackStart, FallbackSeconds);

        // Earliest timestamp among the selected, synced lines.
        double? start = null;
        foreach (var i in selectedIndices)
            if (lines[i].Timestamp is { } ts)
                start = start is { } s ? Math.Min(s, ts.TotalSeconds) : ts.TotalSeconds;

        if (start is not { } startSeconds)
            return new ShareClipTiming(fallbackStart, FallbackSeconds); // unsynced selection

        startSeconds = Math.Max(0, startSeconds);

        // End = first timestamped line after the last selected index.
        int lastSelected = selectedIndices[^1];
        double? end = null;
        for (int i = lastSelected + 1; i < lines.Count; i++)
            if (lines[i].Timestamp is { } ts) { end = ts.TotalSeconds; break; }

        double duration = end is { } e
            ? Math.Clamp(e - startSeconds, MinSeconds, MaxSeconds)
            : FallbackSeconds;

        return new ShareClipTiming(startSeconds, duration);
    }
}
