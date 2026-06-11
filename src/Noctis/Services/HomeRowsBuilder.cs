using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Pure ranking logic for the Home page's time-aware rows, computed entirely
/// from the local play log (no cloud).
/// </summary>
public static class HomeRowsBuilder
{
    /// <summary>Row title for the time-of-day rotation, based on the current hour.</summary>
    public static string DaypartLabel(int hour) => hour switch
    {
        >= 5 and < 12 => "Morning rotation",
        >= 12 and < 17 => "Afternoon rotation",
        >= 17 and < 22 => "Evening rotation",
        _ => "Late night rotation",
    };

    /// <summary>
    /// Tracks the user keeps playing around this time of day: non-skipped plays
    /// within ±2 hours of the current hour over the past 90 days, at least 2 plays.
    /// </summary>
    public static List<Guid> BuildTimeOfDayRotation(
        IReadOnlyList<PlayHistoryEvent> events, DateTime nowLocal, int top = 6)
    {
        var cutoff = nowLocal.AddDays(-90);
        int center = nowLocal.Hour;

        bool InWindow(int hour)
        {
            var diff = Math.Abs(hour - center);
            return Math.Min(diff, 24 - diff) <= 2;
        }

        return events
            .Where(e => !e.Skipped)
            .Select(e => new { e.TrackId, Local = e.PlayedAtUtc.ToLocalTime() })
            .Where(x => x.Local >= cutoff && InWindow(x.Local.Hour))
            .GroupBy(x => x.TrackId)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Take(top)
            .Select(g => g.Key)
            .ToList();
    }

    /// <summary>Most-played tracks of the last two weeks (min 3 non-skipped plays).</summary>
    public static List<Guid> BuildHeavyRotation(
        IReadOnlyList<PlayHistoryEvent> events, DateTime nowLocal, int top = 6)
    {
        var cutoff = nowLocal.AddDays(-14);
        return events
            .Where(e => !e.Skipped && e.PlayedAtUtc.ToLocalTime() >= cutoff)
            .GroupBy(e => e.TrackId)
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .Take(top)
            .Select(g => g.Key)
            .ToList();
    }

    /// <summary>
    /// Tracks recently played again after a long break: a non-skipped play within
    /// the last two weeks whose previous play was at least 60 days earlier.
    /// Ordered by gap length, longest rediscovery first.
    /// </summary>
    public static List<Guid> BuildRediscovered(
        IReadOnlyList<PlayHistoryEvent> events, DateTime nowLocal, int top = 6)
    {
        var recentCutoff = nowLocal.AddDays(-14);
        var minGap = TimeSpan.FromDays(60);

        return events
            .Where(e => !e.Skipped)
            .GroupBy(e => e.TrackId)
            .Select(g =>
            {
                var plays = g.Select(e => e.PlayedAtUtc.ToLocalTime()).OrderBy(t => t).ToList();
                var firstRecent = plays.FirstOrDefault(t => t >= recentCutoff);
                if (firstRecent == default) return (TrackId: g.Key, Gap: TimeSpan.Zero);
                var lastBefore = plays.LastOrDefault(t => t < firstRecent);
                if (lastBefore == default) return (TrackId: g.Key, Gap: TimeSpan.Zero);
                return (TrackId: g.Key, Gap: firstRecent - lastBefore);
            })
            .Where(x => x.Gap >= minGap)
            .OrderByDescending(x => x.Gap)
            .Take(top)
            .Select(x => x.TrackId)
            .ToList();
    }
}
