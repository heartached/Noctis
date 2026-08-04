using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Tag-based Autoplay selection: when the play queue is exhausted by a natural track
/// end, picks library tracks similar to the just-ended one. Similarity is strictly
/// tiered — same genre first, same primary artist only when the genre tier yields
/// nothing — and never falls back to random whole-library picks. Selection is a
/// plain O(n) scan over the in-memory library (no I/O, no sorting), cheap enough
/// to run inline on the advance path even for six-figure libraries.
/// </summary>
public sealed class AutoplayService : IAutoplayService
{
    public IReadOnlyList<Track> PickSimilar(Track seed, IReadOnlyList<Track> library, int count, ISet<Guid> exclude)
    {
        if (count <= 0 || library.Count == 0)
            return Array.Empty<Track>();

        // Genre tier: whole-string compare, trimmed + case-insensitive — the same
        // convention WrapStatsBuilder uses to group genres (nothing in the app
        // splits multi-genre strings, so "Rock; Alternative" is one genre here too).
        var genre = seed.Genre.AsSpan().Trim();
        var pool = genre.Length > 0 ? CollectGenreMatches(genre, seed, library, exclude) : null;

        // Artist tier: only when the seed has no genre or the genre pool came up empty.
        if (pool == null || pool.Count == 0)
        {
            var primary = Track.GetPrimaryArtist(seed.Artist);
            if (primary.Length == 0)
                return Array.Empty<Track>();
            pool = CollectArtistMatches(primary, seed, library, exclude);
        }

        if (pool.Count == 0)
            return Array.Empty<Track>();

        // Partial Fisher–Yates: uniformly random picks without shuffling the whole pool.
        var rng = Random.Shared;
        var take = Math.Min(count, pool.Count);
        for (int i = 0; i < take; i++)
        {
            int j = rng.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool.GetRange(0, take);
    }

    private static List<Track> CollectGenreMatches(
        ReadOnlySpan<char> genre, Track seed, IReadOnlyList<Track> library, ISet<Guid> exclude)
    {
        var pool = new List<Track>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < library.Count; i++)
        {
            var t = library[i];
            if (!IsEligible(t, seed, exclude, now))
                continue;
            if (genre.Equals(t.Genre.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase))
                pool.Add(t);
        }
        return pool;
    }

    private static List<Track> CollectArtistMatches(
        string primary, Track seed, IReadOnlyList<Track> library, ISet<Guid> exclude)
    {
        var pool = new List<Track>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < library.Count; i++)
        {
            var t = library[i];
            if (!IsEligible(t, seed, exclude, now))
                continue;
            // GetPrimaryArtist is a regex split — too heavy to run per track on a
            // 100k scan. The primary artist is always the artist string's first
            // token, so a free span prefix test rejects nearly everything and the
            // real parse only confirms the token boundary on plausible matches
            // ("Foo" must not match "Foobar").
            if (!t.Artist.AsSpan().TrimStart().StartsWith(primary, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(Track.GetPrimaryArtist(t.Artist), primary, StringComparison.OrdinalIgnoreCase))
                pool.Add(t);
        }
        return pool;
    }

    private static bool IsEligible(Track t, Track seed, ISet<Guid> exclude, DateTime now)
        => t.Id != seed.Id
           && !exclude.Contains(t.Id)
           && !t.IsDisliked
           && (t.SnoozedUntil == null || t.SnoozedUntil <= now);
}
