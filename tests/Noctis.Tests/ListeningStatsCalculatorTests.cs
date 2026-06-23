using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ListeningStatsCalculatorTests
{
    private static Track MakeTrack(double minutes = 4) => new()
    {
        Title = "Song",
        Artist = "Artist",
        Duration = TimeSpan.FromMinutes(minutes),
    };

    private static PlayHistoryEvent Play(Guid trackId, DateTime localTime, bool skipped = false) => new()
    {
        TrackId = trackId,
        PlayedAtUtc = localTime.ToUniversalTime(),
        Skipped = skipped,
    };

    [Fact]
    public void Compute_NoEvents_ReturnsZeroStats()
    {
        var stats = ListeningStatsCalculator.Compute(
            Array.Empty<PlayHistoryEvent>(), new Dictionary<Guid, Track>());

        Assert.Equal(0, stats.TotalPlays);
        Assert.Equal(0, stats.CompletedPlays);
        Assert.Equal(0, stats.SkippedPlays);
        Assert.Equal(0, stats.TimeListenedTicks);
        Assert.Equal(0, stats.AvgListenedTrackLengthTicks);
        Assert.Equal(0, stats.CurrentStreakDays);
        Assert.Equal(0, stats.LongestStreakDays);
        Assert.Equal(0, stats.PlaysThisWeek);
        Assert.Equal(0, stats.PlaysLastWeek);
    }

    [Fact]
    public void Compute_TimeListened_ExcludesSkippedPlays()
    {
        var t = MakeTrack(minutes: 4);
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var day = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Local);
        var events = new List<PlayHistoryEvent>
        {
            Play(t.Id, day),
            Play(t.Id, day),
            Play(t.Id, day, skipped: true),
        };

        var stats = ListeningStatsCalculator.Compute(events, byId, day);

        Assert.Equal(3, stats.TotalPlays);
        Assert.Equal(2, stats.CompletedPlays);
        Assert.Equal(1, stats.SkippedPlays);
        // 2 completed × 4 min, skipped contributes nothing.
        Assert.Equal(TimeSpan.FromMinutes(8).Ticks, stats.TimeListenedTicks);
    }

    [Fact]
    public void Compute_DeletedTrack_UsesFallbackLength()
    {
        // Track not present in the library dictionary (since-deleted file).
        var ghostId = Guid.NewGuid();
        var day = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Local);
        var events = new List<PlayHistoryEvent> { Play(ghostId, day) };

        var stats = ListeningStatsCalculator.Compute(events, new Dictionary<Guid, Track>(), day);

        Assert.Equal(1, stats.CompletedPlays);
        Assert.Equal(TimeSpan.FromMinutes(3.5).Ticks, stats.TimeListenedTicks);
    }

    [Fact]
    public void Compute_AvgListenedTrackLength_IsTimeOverCompletedPlays()
    {
        var t2 = MakeTrack(minutes: 2);
        var t6 = MakeTrack(minutes: 6);
        var byId = new Dictionary<Guid, Track> { [t2.Id] = t2, [t6.Id] = t6 };
        var day = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Local);
        var events = new List<PlayHistoryEvent>
        {
            Play(t2.Id, day),
            Play(t6.Id, day),
            Play(t6.Id, day, skipped: true), // ignored: skipped
        };

        var stats = ListeningStatsCalculator.Compute(events, byId, day);

        // (2 + 6) min over 2 completed plays = 4 min average.
        Assert.Equal(TimeSpan.FromMinutes(4).Ticks, stats.AvgListenedTrackLengthTicks);
    }

    [Fact]
    public void Compute_Streak_CountsConsecutiveDaysEndingToday()
    {
        var t = MakeTrack();
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var today = new DateTime(2026, 6, 18, 20, 0, 0, DateTimeKind.Local);
        var events = new List<PlayHistoryEvent>
        {
            Play(t.Id, today),
            Play(t.Id, today.AddDays(-1)),
            Play(t.Id, today.AddDays(-2)),
            // gap on -3
            Play(t.Id, today.AddDays(-4)),
        };

        var stats = ListeningStatsCalculator.Compute(events, byId, today);

        Assert.Equal(3, stats.CurrentStreakDays);
        Assert.Equal(3, stats.LongestStreakDays);
    }

    [Fact]
    public void Compute_CurrentStreak_SurvivesIfPlayedYesterdayButNotToday()
    {
        var t = MakeTrack();
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var today = new DateTime(2026, 6, 18, 9, 0, 0, DateTimeKind.Local);
        var events = new List<PlayHistoryEvent>
        {
            Play(t.Id, today.AddDays(-1)),
            Play(t.Id, today.AddDays(-2)),
        };

        var stats = ListeningStatsCalculator.Compute(events, byId, today);

        Assert.Equal(2, stats.CurrentStreakDays);
    }

    [Fact]
    public void Compute_CurrentStreak_ResetsWhenLastPlayOlderThanYesterday()
    {
        var t = MakeTrack();
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var today = new DateTime(2026, 6, 18, 9, 0, 0, DateTimeKind.Local);
        var events = new List<PlayHistoryEvent>
        {
            Play(t.Id, today.AddDays(-3)),
            Play(t.Id, today.AddDays(-4)),
        };

        var stats = ListeningStatsCalculator.Compute(events, byId, today);

        Assert.Equal(0, stats.CurrentStreakDays);
        Assert.Equal(2, stats.LongestStreakDays);
    }

    [Fact]
    public void Compute_WeekOverWeek_CountsRolling7DayWindows()
    {
        var t = MakeTrack();
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var today = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Local);
        var events = new List<PlayHistoryEvent>
        {
            Play(t.Id, today),              // this week
            Play(t.Id, today.AddDays(-6)),  // this week (boundary)
            Play(t.Id, today.AddDays(-7)),  // last week (boundary)
            Play(t.Id, today.AddDays(-13)), // last week (boundary)
            Play(t.Id, today.AddDays(-14)), // older
        };

        var stats = ListeningStatsCalculator.Compute(events, byId, today);

        Assert.Equal(2, stats.PlaysThisWeek);
        Assert.Equal(2, stats.PlaysLastWeek);
    }
}
