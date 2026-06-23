using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Honest listening figures derived from the persistent play log, shared by the
/// Settings → Statistics tab and the standalone Statistics page so the two
/// surfaces never disagree. Time reflects what was actually played: skipped
/// events contribute nothing.
/// </summary>
public sealed class ListeningStats
{
    /// <summary>Total play events in the log (started plays, including skips).</summary>
    public int TotalPlays { get; init; }

    /// <summary>Non-skipped events — plays heard past the skip threshold.</summary>
    public int CompletedPlays { get; init; }

    /// <summary>Events the user skipped away from early.</summary>
    public int SkippedPlays { get; init; }

    /// <summary>Sum of completed plays' track durations. Excludes skipped plays.</summary>
    public long TimeListenedTicks { get; init; }

    /// <summary>Average length of a completed play (TimeListened / CompletedPlays).</summary>
    public long AvgListenedTrackLengthTicks { get; init; }

    /// <summary>Consecutive days with at least one play, ending today or yesterday.</summary>
    public int CurrentStreakDays { get; init; }

    /// <summary>Longest run of consecutive days with at least one play.</summary>
    public int LongestStreakDays { get; init; }

    /// <summary>Plays in the rolling 7 days ending today.</summary>
    public int PlaysThisWeek { get; init; }

    /// <summary>Plays in the 7 days before this week.</summary>
    public int PlaysLastWeek { get; init; }
}

/// <summary>
/// Pure computation — events in, stats out — so it stays unit-testable. Event
/// timestamps are interpreted in local time, matching the Statistics page and
/// <see cref="WrapStatsBuilder"/>.
/// </summary>
public static class ListeningStatsCalculator
{
    /// <summary>Track length assumed for plays whose track left the library.
    /// Matches <see cref="WrapStatsBuilder"/> so listening time stays consistent.</summary>
    private static readonly TimeSpan FallbackTrackLength = TimeSpan.FromMinutes(3.5);

    public static ListeningStats Compute(
        IReadOnlyList<PlayHistoryEvent> events,
        IReadOnlyDictionary<Guid, Track> tracksById,
        DateTime? nowLocal = null)
    {
        if (events.Count == 0)
            return new ListeningStats();

        var today = (nowLocal ?? DateTime.Now).Date;

        int completed = 0, skipped = 0;
        long listenedTicks = 0;
        int thisWeek = 0, lastWeek = 0;
        var playDays = new HashSet<DateTime>();

        foreach (var e in events)
        {
            var localDate = e.PlayedAtUtc.ToLocalTime().Date;
            playDays.Add(localDate);

            if (e.Skipped)
            {
                skipped++;
            }
            else
            {
                completed++;
                var length = tracksById.TryGetValue(e.TrackId, out var track) && track.Duration > TimeSpan.Zero
                    ? track.Duration
                    : FallbackTrackLength;
                listenedTicks += length.Ticks;
            }

            var daysAgo = (today - localDate).Days;
            if (daysAgo is >= 0 and <= 6) thisWeek++;
            else if (daysAgo is >= 7 and <= 13) lastWeek++;
        }

        var (current, longest) = ComputeStreaks(playDays, today);

        return new ListeningStats
        {
            TotalPlays = events.Count,
            CompletedPlays = completed,
            SkippedPlays = skipped,
            TimeListenedTicks = listenedTicks,
            AvgListenedTrackLengthTicks = completed > 0 ? listenedTicks / completed : 0,
            CurrentStreakDays = current,
            LongestStreakDays = longest,
            PlaysThisWeek = thisWeek,
            PlaysLastWeek = lastWeek,
        };
    }

    /// <summary>
    /// Current streak counts back from today (or yesterday, as a grace day if the
    /// user hasn't played yet today); longest scans every run of consecutive days.
    /// </summary>
    private static (int current, int longest) ComputeStreaks(HashSet<DateTime> playDays, DateTime today)
    {
        if (playDays.Count == 0) return (0, 0);

        var sorted = playDays.OrderBy(d => d).ToList();

        int longest = 1, run = 1;
        for (var i = 1; i < sorted.Count; i++)
        {
            run = (sorted[i] - sorted[i - 1]).Days == 1 ? run + 1 : 1;
            if (run > longest) longest = run;
        }

        DateTime? anchor =
            playDays.Contains(today) ? today :
            playDays.Contains(today.AddDays(-1)) ? today.AddDays(-1) :
            null;

        var current = 0;
        if (anchor is { } day)
        {
            while (playDays.Contains(day))
            {
                current++;
                day = day.AddDays(-1);
            }
        }

        return (current, longest);
    }
}
