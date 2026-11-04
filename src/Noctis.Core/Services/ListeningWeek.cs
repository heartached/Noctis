using Noctis.Models;

namespace Noctis.Services;

/// <summary>The last seven local days of the play log: listening time per day, the total and the play count.</summary>
/// <param name="Days">The seven local dates, oldest first; the last is today.</param>
/// <param name="DayTicks">Time listened on each of <paramref name="Days"/>.</param>
public sealed record ListeningWeek(IReadOnlyList<DateTime> Days, IReadOnlyList<long> DayTicks, int Plays)
{
    public long TotalTicks => DayTicks.Sum();

    public static ListeningWeek Empty(DateTime today) => new(
        Enumerable.Range(-6, 7).Select(i => today.Date.AddDays(i)).ToList(), new long[7], 0);
}

/// <summary>
/// Settings → Listening History's "This Week". A completed play counts the song's length (a
/// song of unknown length counts <see cref="FallbackLength"/>), a skip counts as a play and no
/// time: the Statistics page's rule (ListeningStatsCalculator). Days are the user's local ones.
/// </summary>
public static class ListeningWeekCalculator
{
    public static readonly TimeSpan FallbackLength = PlayListening.FallbackLength;

    /// <param name="events">The log, oldest first.</param>
    /// <param name="lengthOf">A song's length by id; null or zero when unknown.</param>
    public static ListeningWeek Compute(IReadOnlyList<PlayHistoryEvent> events, Func<Guid, TimeSpan?> lengthOf,
        DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(nowUtc), zone).Date;
        var first = today.AddDays(-6);
        var ticks = new long[7];
        var plays = 0;
        // Oldest first, so walk back and stop once past the window (a day's slack covers any zone's offset).
        var stopBefore = AsUtc(first).AddDays(-1);
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            var utc = AsUtc(e.PlayedAtUtc);
            if (utc < stopBefore) break;
            var day = (TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date - first).Days;
            if (day is < 0 or > 6) continue;
            plays++;
            if (e.Skipped) continue;
            ticks[day] += PlayListening.Heard(e, lengthOf(e.TrackId)).Ticks;
        }
        return new ListeningWeek(Enumerable.Range(0, 7).Select(i => first.AddDays(i)).ToList(), ticks, plays);
    }

    /// <summary>"6 h 12 min", "12 min", "45 s" (under a minute); "0 min" for none.</summary>
    public static string FormatTotal(long ticks)
    {
        var t = TimeSpan.FromTicks(Math.Max(0, ticks));
        if (t.TotalMinutes < 1) return t.TotalSeconds < 1 ? "0 min" : $"{(int)t.TotalSeconds} s";
        var h = (int)t.TotalHours;
        return h == 0 ? $"{t.Minutes} min" : t.Minutes == 0 ? $"{h} h" : $"{h} h {t.Minutes} min";
    }

    /// <summary>The Settings row's value: "6 h this week", "12 min this week"; "" without plays.</summary>
    public static string FormatSummary(ListeningWeek week)
    {
        if (week.Plays == 0) return string.Empty;
        var t = TimeSpan.FromTicks(week.TotalTicks);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h this week" : $"{Math.Max(1, (int)t.TotalMinutes)} min this week";
    }

    /// <summary>"Now", "12 min", "1 h", "3 d": how long ago a play was.</summary>
    public static string FormatAgo(DateTime playedUtc, DateTime nowUtc)
    {
        var ago = AsUtc(nowUtc) - AsUtc(playedUtc);
        if (ago < TimeSpan.FromMinutes(1)) return "Now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} h";
        if (ago < TimeSpan.FromDays(60)) return $"{(int)ago.TotalDays} d";
        return $"{(int)(ago.TotalDays / 30)} mo";
    }

    private static DateTime AsUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
