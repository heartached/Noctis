using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Shuffle ordering for playback. Tracks flagged "not liked" stay eligible but get a much
/// lower selection weight, so they tend to land late in the shuffled order (Apple
/// Music-style "suggest less"). Uses Efraimidis–Spirakis weighted sampling: each track
/// is keyed by U^(1/w) and sorted descending, which is equivalent to repeatedly drawing
/// without replacement with probability proportional to its weight.
/// </summary>
public static class ShuffleHelper
{
    /// <summary>Selection weight for "not liked" tracks (normal tracks weigh 1.0).</summary>
    private const double DislikedWeight = 0.2;

    public static List<Track> WeightedShuffle(IEnumerable<Track> tracks, Random? rng = null)
    {
        rng ??= Random.Shared;
        return tracks
            .OrderByDescending(t => Math.Pow(rng.NextDouble(), 1.0 / WeightOf(t)))
            .ToList();
    }

    private static double WeightOf(Track track) => track.IsDisliked ? DislikedWeight : 1.0;
}
