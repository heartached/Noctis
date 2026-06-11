using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Shuffle ordering for playback. Snoozed tracks are excluded entirely. "Not liked" and
/// recently-played tracks stay eligible but get a lower selection weight, so they tend to
/// land later (Apple Music-style "suggest less" / avoid repeats). Uses Efraimidis–Spirakis
/// weighted sampling: each track is keyed by U^(1/w) and sorted descending, which is
/// equivalent to repeatedly drawing without replacement with probability proportional to
/// its weight.
/// </summary>
public static class ShuffleHelper
{
    /// <summary>Selection weight for "not liked" tracks (normal tracks weigh 1.0).</summary>
    private const double DislikedWeight = 0.2;

    /// <summary>Selection weight multiplier for recently-played tracks (avoid repeats).</summary>
    private const double RecentlyPlayedWeight = 0.25;

    public static List<Track> WeightedShuffle(
        IEnumerable<Track> tracks, Random? rng = null, ISet<Guid>? recentlyPlayed = null)
    {
        rng ??= Random.Shared;
        var now = DateTime.UtcNow;
        return tracks
            .Where(t => t.SnoozedUntil == null || t.SnoozedUntil <= now)
            .OrderByDescending(t => Math.Pow(rng.NextDouble(), 1.0 / WeightOf(t, recentlyPlayed)))
            .ToList();
    }

    private static double WeightOf(Track track, ISet<Guid>? recentlyPlayed)
    {
        double w = track.IsDisliked ? DislikedWeight : 1.0;
        if (recentlyPlayed != null && recentlyPlayed.Contains(track.Id)) w *= RecentlyPlayedWeight;
        return w;
    }
}
