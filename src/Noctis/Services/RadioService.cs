using Noctis.Models;

namespace Noctis.Services;

/// <summary>Builds an endless similar-tracks queue from the user's own library.</summary>
public sealed class RadioService : IRadioService
{
    public IReadOnlyList<Track> BuildSimilar(Track seed, IEnumerable<Track> library, int count, ISet<Guid> exclude)
    {
        var now = DateTime.UtcNow;
        var rng = Random.Shared;
        return library
            .Where(t => t.Id != seed.Id && !exclude.Contains(t.Id) && !t.IsDisliked
                        && (t.SnoozedUntil == null || t.SnoozedUntil <= now))
            .Select(t => (Track: t, Score: Score(seed, t)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score + rng.NextDouble() * 0.05)
            .Take(count)
            .Select(x => x.Track)
            .ToList();
    }

    private static double Score(Track seed, Track c)
    {
        double score = 0;
        if (!string.IsNullOrWhiteSpace(seed.Genre) && string.Equals(seed.Genre, c.Genre, StringComparison.OrdinalIgnoreCase))
            score += 3.0;
        if (string.Equals(Track.GetPrimaryArtist(seed.Artist), Track.GetPrimaryArtist(c.Artist), StringComparison.OrdinalIgnoreCase))
            score += 2.5;
        else if (string.Equals(seed.AlbumArtist, c.AlbumArtist, StringComparison.OrdinalIgnoreCase))
            score += 1.5;
        if (seed.Year > 0 && c.Year > 0)
        {
            int gap = Math.Abs(seed.Year - c.Year);
            if (gap <= 2) score += 1.5; else if (gap <= 5) score += 1.0; else if (gap <= 10) score += 0.5;
        }
        if (seed.Bpm > 0 && c.Bpm > 0 && AutoMixKeyTempo.GetNormalizedBpmDifference(seed.Bpm, c.Bpm) <= 8)
            score += 1.0;
        if (!string.IsNullOrWhiteSpace(seed.MusicalKey) && !string.IsNullOrWhiteSpace(c.MusicalKey)
            && AutoMixKeyTempo.AreKeysCompatible(seed.MusicalKey, c.MusicalKey))
            score += 0.75;
        return score;
    }
}
