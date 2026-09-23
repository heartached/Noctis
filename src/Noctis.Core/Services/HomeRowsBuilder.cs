using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Pure ranking logic for the Home page's time-aware rows, computed entirely
/// from the local play log (no cloud).
/// </summary>
public static class HomeRowsBuilder
{
    /// <summary>
    /// Minimum number of tracks a history row needs to be worth showing;
    /// rows shorter than this are hidden entirely.
    /// </summary>
    public const int MinRowItems = 4;

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
        IReadOnlyList<PlayHistoryEvent> events, DateTime nowLocal, int top = 6,
        ISet<Guid>? exclude = null)
    {
        var cutoff = nowLocal.AddDays(-90);
        int center = nowLocal.Hour;

        bool InWindow(int hour)
        {
            var diff = Math.Abs(hour - center);
            return Math.Min(diff, 24 - diff) <= 2;
        }

        return events
            .Where(e => !e.Skipped && (exclude == null || !exclude.Contains(e.TrackId)))
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
        IReadOnlyList<PlayHistoryEvent> events, DateTime nowLocal, int top = 6,
        ISet<Guid>? exclude = null)
    {
        var cutoff = nowLocal.AddDays(-14);
        return events
            .Where(e => !e.Skipped && e.PlayedAtUtc.ToLocalTime() >= cutoff
                        && (exclude == null || !exclude.Contains(e.TrackId)))
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
        IReadOnlyList<PlayHistoryEvent> events, DateTime nowLocal, int top = 6,
        ISet<Guid>? exclude = null)
    {
        var recentCutoff = nowLocal.AddDays(-14);
        var minGap = TimeSpan.FromDays(60);

        return events
            .Where(e => !e.Skipped && (exclude == null || !exclude.Contains(e.TrackId)))
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

    /// <summary>
    /// The newest <paramref name="scan"/> play-log events (the log is oldest-first) as tracks,
    /// newest first; unresolved ids are skipped. Duplicates are left in — callers dedupe by
    /// track or album themselves. Shared by the desktop Home and the phone Library/Home.
    /// </summary>
    public static List<Track> BuildRecentFromLog(IReadOnlyList<PlayHistoryEvent> events, Func<Guid, Track?> resolve, int scan)
    {
        var result = new List<Track>(Math.Min(scan, events.Count));
        var floor = Math.Max(0, events.Count - scan);
        for (var i = events.Count - 1; i >= floor; i--)
        {
            var track = resolve(events[i].TrackId);
            if (track != null) result.Add(track);
        }
        return result;
    }

    /// <summary>
    /// The most recent distinct tracks from a newest-first history: a track that was played
    /// twice keeps only its newest position.
    /// </summary>
    public static List<Track> BuildLastPlayed(IEnumerable<Track> historyNewestFirst, int max)
    {
        var seen = new HashSet<Guid>();
        var result = new List<Track>(max);
        foreach (var t in historyNewestFirst)
        {
            if (!seen.Add(t.Id)) continue;
            result.Add(t);
            if (result.Count >= max) break;
        }
        return result;
    }
}
