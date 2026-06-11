using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class ShuffleHelperTests
{
    [Fact]
    public void WeightedShuffle_KeepsAllTracks()
    {
        var tracks = Enumerable.Range(0, 20)
            .Select(i => new Track { Title = $"T{i}", IsDisliked = i % 4 == 0 })
            .ToList();

        var shuffled = ShuffleHelper.WeightedShuffle(tracks, new Random(1));

        Assert.Equal(tracks.Count, shuffled.Count);
        Assert.Equal(tracks.OrderBy(t => t.Title), shuffled.OrderBy(t => t.Title));
    }

    [Fact]
    public void WeightedShuffle_SuggestsDislikedTracksLess()
    {
        var liked = new Track { Title = "Liked" };
        var disliked = new Track { Title = "Disliked", IsDisliked = true };
        var rng = new Random(42);

        int dislikedFirst = 0;
        const int runs = 2000;
        for (int i = 0; i < runs; i++)
        {
            var order = ShuffleHelper.WeightedShuffle(new[] { liked, disliked }, rng);
            if (order[0] == disliked) dislikedFirst++;
        }

        // With weight 0.2 vs 1.0 the disliked track should lead ~17% of the time;
        // assert well under the 50% an unweighted shuffle would give.
        Assert.InRange(dislikedFirst / (double)runs, 0.05, 0.30);
    }

    private static Track T() => new() { Id = Guid.NewGuid() };

    [Fact]
    public void ExcludesSnoozedTracks()
    {
        var normal = T();
        var snoozed = T();
        snoozed.SnoozedUntil = DateTime.UtcNow.AddDays(10);
        var result = ShuffleHelper.WeightedShuffle(new[] { normal, snoozed });
        Assert.Contains(result, t => t.Id == normal.Id);
        Assert.DoesNotContain(result, t => t.Id == snoozed.Id);
    }

    [Fact]
    public void ExpiredSnoozeIsEligible()
    {
        var t = T();
        t.SnoozedUntil = DateTime.UtcNow.AddDays(-1);
        Assert.Contains(ShuffleHelper.WeightedShuffle(new[] { t }), x => x.Id == t.Id);
    }

    [Fact]
    public void RecentlyPlayedRanksLowerOnAverage()
    {
        var recent = T();
        var fresh = T();
        var recentlyPlayed = new HashSet<Guid> { recent.Id };
        int freshFirst = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var order = ShuffleHelper.WeightedShuffle(new[] { recent, fresh }, new Random(seed), recentlyPlayed);
            if (order[0].Id == fresh.Id) freshFirst++;
        }
        Assert.True(freshFirst > 130, $"fresh-first {freshFirst}/200 should exceed ~65%");
    }
}
