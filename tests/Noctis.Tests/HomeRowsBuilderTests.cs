using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class HomeRowsBuilderTests
{
    private static readonly DateTime Now = new(2026, 6, 11, 9, 0, 0, DateTimeKind.Local);

    private static PlayHistoryEvent Play(Guid id, DateTime local, bool skipped = false) => new()
    {
        TrackId = id,
        PlayedAtUtc = local.ToUniversalTime(),
        Skipped = skipped,
    };

    [Theory]
    [InlineData(8, "Morning rotation")]
    [InlineData(14, "Afternoon rotation")]
    [InlineData(19, "Evening rotation")]
    [InlineData(23, "Late night rotation")]
    [InlineData(2, "Late night rotation")]
    public void DaypartLabel_MapsHours(int hour, string expected)
    {
        Assert.Equal(expected, HomeRowsBuilder.DaypartLabel(hour));
    }

    [Fact]
    public void TimeOfDayRotation_PrefersTracksPlayedInWindow()
    {
        var morning = Guid.NewGuid();
        var evening = Guid.NewGuid();
        var events = new List<PlayHistoryEvent>
        {
            // Morning track: 3 plays around 9:00 on recent days
            Play(morning, Now.AddDays(-1)),
            Play(morning, Now.AddDays(-3).AddHours(1)),
            Play(morning, Now.AddDays(-5).AddHours(-1)),
            // Evening track: plays at 20:00 — outside the ±2h window of 9:00
            Play(evening, Now.AddDays(-1).AddHours(11)),
            Play(evening, Now.AddDays(-2).AddHours(11)),
        };

        var ids = HomeRowsBuilder.BuildTimeOfDayRotation(events, Now);

        Assert.Contains(morning, ids);
        Assert.DoesNotContain(evening, ids);
    }

    [Fact]
    public void TimeOfDayRotation_RequiresAtLeastTwoPlays_AndIgnoresSkips()
    {
        var once = Guid.NewGuid();
        var skipped = Guid.NewGuid();
        var events = new List<PlayHistoryEvent>
        {
            Play(once, Now.AddDays(-1)),
            Play(skipped, Now.AddDays(-1), skipped: true),
            Play(skipped, Now.AddDays(-2), skipped: true),
        };

        Assert.Empty(HomeRowsBuilder.BuildTimeOfDayRotation(events, Now));
    }

    [Fact]
    public void HeavyRotation_RanksByRecentPlays()
    {
        var heavy = Guid.NewGuid();
        var light = Guid.NewGuid();
        var old = Guid.NewGuid();
        var events = new List<PlayHistoryEvent>();
        for (int i = 0; i < 5; i++) events.Add(Play(heavy, Now.AddDays(-i - 1)));
        for (int i = 0; i < 3; i++) events.Add(Play(light, Now.AddDays(-i - 1)));
        for (int i = 0; i < 9; i++) events.Add(Play(old, Now.AddDays(-30 - i))); // outside window

        var ids = HomeRowsBuilder.BuildHeavyRotation(events, Now);

        Assert.Equal(new[] { heavy, light }, ids);
    }

    [Fact]
    public void Rediscovered_RequiresLongGapBeforeRecentPlay()
    {
        var rediscovered = Guid.NewGuid();
        var steady = Guid.NewGuid();
        var events = new List<PlayHistoryEvent>
        {
            // Played 100 days ago, then again 3 days ago → rediscovered.
            Play(rediscovered, Now.AddDays(-100)),
            Play(rediscovered, Now.AddDays(-3)),
            // Played continuously → not a rediscovery.
            Play(steady, Now.AddDays(-20)),
            Play(steady, Now.AddDays(-3)),
        };

        var ids = HomeRowsBuilder.BuildRediscovered(events, Now);

        Assert.Equal(new[] { rediscovered }, ids);
    }

    [Fact]
    public void Rediscovered_IgnoresTracksWithNoEarlierPlay()
    {
        var fresh = Guid.NewGuid();
        var events = new List<PlayHistoryEvent> { Play(fresh, Now.AddDays(-2)) };

        Assert.Empty(HomeRowsBuilder.BuildRediscovered(events, Now));
    }
}
