using Noctis.Models;

namespace Noctis.Services;

/// <summary>What a mix starts from.</summary>
public enum MixSeedKind
{
    Song,
    Artist,
    Genre,
}

/// <summary>A mix's seed: a song, or an artist / genre by name.</summary>
public sealed record MixSeed(MixSeedKind Kind, Track? Song = null, string? Name = null)
{
    public static MixSeed FromSong(Track song) => new(MixSeedKind.Song, song);
    public static MixSeed FromArtist(string artist) => new(MixSeedKind.Artist, Name: artist);
    public static MixSeed FromGenre(string genre) => new(MixSeedKind.Genre, Name: genre);

    /// <summary>The playlist's name: "Mix from Hello", "Adele Mix", "Rock Mix".</summary>
    public string PlaylistName => Kind switch
    {
        MixSeedKind.Song => $"Mix from {Song?.Title}",
        _ => $"{Name} Mix",
    };
}

/// <summary>
/// Discord (Cosmo, 11-02): "an easier way to make playlists, maybe by genre, or an AI DJ makes
/// one". No AI and nothing online: a mix is picked from the user's own library by the same
/// similarity the Track Radio uses (<see cref="RadioService"/>: shared genre, artist, era,
/// tempo and key), leaning toward songs the user plays, rates and loves, with no artist or
/// album taking over, then ordered so the same artist doesn't play twice in a row and the
/// tempo moves in small steps where BPM tags exist. Disliked and snoozed songs stay out.
/// </summary>
public static class MixPlaylistBuilder
{
    public const int DefaultSize = 50;
    /// <summary>Most songs one artist gets in a song or genre mix.</summary>
    public const int MaxPerArtist = 4;
    /// <summary>Most songs from one album.</summary>
    public const int MaxPerAlbum = 3;

    /// <summary>
    /// Builds the mix. A song seed opens it; <paramref name="randomSeed"/> varies the pick
    /// between runs (same value, same mix — for tests).
    /// </summary>
    public static IReadOnlyList<Track> Build(MixSeed seed, IEnumerable<Track> library, int size = DefaultSize, int? randomSeed = null)
    {
        if (size <= 0) return Array.Empty<Track>();
        var rng = randomSeed is { } s ? new Random(s) : Random.Shared;
        var now = DateTime.UtcNow;
        var pool = library
            .Where(t => !t.IsDisliked && (t.SnoozedUntil == null || t.SnoozedUntil <= now))
            .DistinctBy(t => t.Id)
            .ToList();

        Func<Track, double> score;
        int perArtist = MaxPerArtist;
        string? seedArtist = null;
        switch (seed.Kind)
        {
            case MixSeedKind.Song when seed.Song is { } song:
            {
                var genres = Track.SplitGenres(song.Genre);
                score = t => t.Id == song.Id ? 0 : RadioService.Score(song, genres, t);
                break;
            }
            case MixSeedKind.Artist when !string.IsNullOrWhiteSpace(seed.Name):
            {
                seedArtist = seed.Name.Trim();
                var own = pool.Where(t => IsArtist(t, seedArtist!)).ToList();
                if (own.Count == 0) return Array.Empty<Track>();
                // The artist's own songs lead; similar artists fill the rest by the artist's
                // usual genres (weighted by how much of their music carries each) and era.
                var genreWeight = own.SelectMany(t => Track.SplitGenres(t.Genre))
                    .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => (double)g.Count() / own.Count, StringComparer.OrdinalIgnoreCase);
                var years = own.Where(t => t.Year > 0).Select(t => t.Year).OrderBy(y => y).ToList();
                var midYear = years.Count > 0 ? years[years.Count / 2] : 0;
                perArtist = Math.Max(MaxPerArtist, size / 3);
                score = t =>
                {
                    double sc = IsArtist(t, seedArtist!) ? 4.0 : 0;
                    var g = Track.SplitGenres(t.Genre).Select(x => genreWeight.GetValueOrDefault(x)).DefaultIfEmpty(0).Max();
                    sc += 3.0 * g;
                    if (sc <= 0) return 0;
                    if (midYear > 0 && t.Year > 0)
                    {
                        var gap = Math.Abs(midYear - t.Year);
                        sc += gap <= 3 ? 1.0 : gap <= 8 ? 0.5 : 0;
                    }
                    return sc;
                };
                break;
            }
            case MixSeedKind.Genre when !string.IsNullOrWhiteSpace(seed.Name):
            {
                var genre = new[] { seed.Name.Trim() };
                score = t => Track.SharesGenre(genre, t.Genre) ? 3.0 : 0;
                break;
            }
            default:
                return Array.Empty<Track>();
        }

        var ranked = pool
            .Select(t => (Track: t, Score: score(t)))
            .Where(x => x.Score > 0)
            .Select(x => (x.Track, Score: x.Score + Taste(x.Track) + rng.NextDouble() * 0.6))
            .OrderByDescending(x => x.Score)
            .ToList();

        var picked = new List<Track>(size);
        var byArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byAlbum = new Dictionary<Guid, int>();
        if (seed.Kind == MixSeedKind.Song && seed.Song is { } first && !first.IsDisliked)
            Take(first);
        foreach (var (t, _) in ranked)
        {
            if (picked.Count >= size) break;
            var artist = ArtistKey(t);
            var cap = seedArtist != null && IsArtist(t, seedArtist) ? perArtist : MaxPerArtist;
            if (byArtist.GetValueOrDefault(artist) >= cap) continue;
            if (t.AlbumId != Guid.Empty && byAlbum.GetValueOrDefault(t.AlbumId) >= MaxPerAlbum && seedArtist == null) continue;
            Take(t);
        }

        return Order(picked);

        void Take(Track t)
        {
            picked.Add(t);
            var a = ArtistKey(t);
            byArtist[a] = byArtist.GetValueOrDefault(a) + 1;
            if (t.AlbumId != Guid.Empty) byAlbum[t.AlbumId] = byAlbum.GetValueOrDefault(t.AlbumId) + 1;
        }
    }

    /// <summary>The user's own signal: loved, rated, played. At most about +2, so it tips
    /// between similar songs rather than overriding similarity.</summary>
    internal static double Taste(Track t)
    {
        double s = 0;
        if (t.IsFavorite) s += 0.75;
        if (t.Rating >= 4) s += 0.5;
        else if (t.Rating is > 0 and <= 2) s -= 0.5;
        if (t.PlayCount > 0) s += Math.Min(0.75, Math.Log10(1 + t.PlayCount) / 2);
        return s;
    }

    /// <summary>
    /// Keeps the first song first (a song seed), then picks each next song from the closest
    /// few still waiting: never the artist just played when anything else is left, and the
    /// smallest tempo jump where both have a BPM.
    /// </summary>
    internal static IReadOnlyList<Track> Order(IReadOnlyList<Track> picked)
    {
        if (picked.Count <= 2) return picked.ToList();
        var left = picked.ToList();
        var result = new List<Track>(picked.Count);
        var current = left[0];
        left.RemoveAt(0);
        result.Add(current);
        while (left.Count > 0)
        {
            // Look a few ahead in score order so the mix still opens with its best matches.
            var window = left.Take(6).ToList();
            var next = window
                .OrderBy(t => string.Equals(ArtistKey(t), ArtistKey(current), StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(t => current.Bpm > 0 && t.Bpm > 0 ? AutoMixKeyTempo.GetNormalizedBpmDifference(current.Bpm, t.Bpm) : 0)
                .ThenBy(t => left.IndexOf(t))
                .First();
            left.Remove(next);
            result.Add(next);
            current = next;
        }
        return result;
    }

    private static string ArtistKey(Track t) => Track.GetPrimaryArtist(t.Artist);

    private static bool IsArtist(Track t, string artist)
        => string.Equals(ArtistKey(t), artist, StringComparison.OrdinalIgnoreCase)
           || string.Equals(Track.GetPrimaryArtist(t.AlbumArtist), artist, StringComparison.OrdinalIgnoreCase);
}
