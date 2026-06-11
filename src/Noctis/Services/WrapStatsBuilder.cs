using System.Globalization;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>One ranked row in a Wrap top-list.</summary>
public sealed class WrapEntry
{
    public int Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public int Plays { get; init; }
    public string PlaysLabel => Plays == 1 ? "1 play" : $"{Plays} plays";
}

/// <summary>Computed listening recap for a year or a single month.</summary>
public sealed class WrapStats
{
    public string PeriodLabel { get; init; } = string.Empty;
    public int TotalPlays { get; init; }
    public long TotalMinutes { get; init; }
    public int UniqueTracks { get; init; }
    public int UniqueArtists { get; init; }
    public int UniqueAlbums { get; init; }
    /// <summary>Percent of resolvable plays that were lossless files (0-100).</summary>
    public double LosslessPercent { get; init; }
    /// <summary>Percent of resolvable plays that were hi-res lossless (0-100).</summary>
    public double HiResPercent { get; init; }
    public string TopGenre { get; init; } = "—";
    public IReadOnlyList<WrapEntry> TopTracks { get; init; } = Array.Empty<WrapEntry>();
    public IReadOnlyList<WrapEntry> TopArtists { get; init; } = Array.Empty<WrapEntry>();
    public IReadOnlyList<WrapEntry> TopAlbums { get; init; } = Array.Empty<WrapEntry>();
    public IReadOnlyList<WrapEntry> TopGenres { get; init; } = Array.Empty<WrapEntry>();
    /// <summary>Artwork of the most-played album, used to tint the share card.</summary>
    public string? TopAlbumArtworkPath { get; init; }
}

/// <summary>
/// Builds Noctis Wrap recaps from the persistent play log. Pure computation —
/// events in, stats out — so it stays unit-testable.
/// </summary>
public static class WrapStatsBuilder
{
    private const int TopCount = 5;

    /// <summary>Average track length assumed for plays whose track left the library.</summary>
    private static readonly TimeSpan FallbackTrackLength = TimeSpan.FromMinutes(3.5);

    /// <summary>
    /// Computes the recap for a period. <paramref name="month"/> null = whole year.
    /// Event timestamps are interpreted in local time, matching the Statistics page.
    /// </summary>
    public static WrapStats Build(
        IReadOnlyList<PlayHistoryEvent> events,
        IReadOnlyDictionary<Guid, Track> tracksById,
        int year,
        int? month = null)
    {
        var inPeriod = events.Where(e =>
        {
            var local = e.PlayedAtUtc.ToLocalTime();
            return local.Year == year && (month == null || local.Month == month);
        }).ToList();

        var periodLabel = month == null
            ? year.ToString()
            : new DateTime(year, month.Value, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        if (inPeriod.Count == 0)
            return new WrapStats { PeriodLabel = periodLabel };

        double totalMinutes = 0;
        int resolvedPlays = 0, losslessPlays = 0, hiResPlays = 0;
        foreach (var e in inPeriod)
        {
            tracksById.TryGetValue(e.TrackId, out var track);
            if (!e.Skipped)
            {
                var length = track != null && track.Duration > TimeSpan.Zero
                    ? track.Duration
                    : FallbackTrackLength;
                totalMinutes += length.TotalMinutes;
            }
            if (track == null) continue;
            resolvedPlays++;
            if (track.IsLossless) losslessPlays++;
            if (track.IsHiResLossless) hiResPlays++;
        }

        var topTracks = inPeriod
            .GroupBy(e => e.TrackId)
            .Select(g => new { Latest = g.OrderByDescending(e => e.PlayedAtUtc).First(), Plays = g.Count() })
            .OrderByDescending(x => x.Plays)
            .Take(TopCount)
            .Select((x, i) => new WrapEntry
            {
                Rank = i + 1,
                Name = x.Latest.Title,
                Subtitle = x.Latest.Artist,
                Plays = x.Plays,
            })
            .ToList();

        var topArtists = inPeriod
            .Where(e => !string.IsNullOrWhiteSpace(e.Artist))
            .GroupBy(e => Track.GetPrimaryArtist(e.Artist).Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(TopCount)
            .Select((g, i) => new WrapEntry { Rank = i + 1, Name = g.Key, Plays = g.Count() })
            .ToList();

        // Albums and genres need the library track for metadata; unresolvable plays drop out.
        var resolved = inPeriod
            .Select(e => new { Event = e, Track = tracksById.GetValueOrDefault(e.TrackId) })
            .Where(x => x.Track != null)
            .ToList();

        var albumGroups = resolved
            .Where(x => !string.IsNullOrWhiteSpace(x.Track!.Album))
            .GroupBy(x => x.Track!.AlbumId)
            .OrderByDescending(g => g.Count())
            .ToList();

        var topAlbums = albumGroups
            .Take(TopCount)
            .Select((g, i) => new WrapEntry
            {
                Rank = i + 1,
                Name = g.First().Track!.Album,
                Subtitle = g.First().Track!.AlbumArtist,
                Plays = g.Count(),
            })
            .ToList();

        var topGenres = resolved
            .Where(x => !string.IsNullOrWhiteSpace(x.Track!.Genre))
            .GroupBy(x => x.Track!.Genre.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(TopCount)
            .Select((g, i) => new WrapEntry { Rank = i + 1, Name = g.Key, Plays = g.Count() })
            .ToList();

        return new WrapStats
        {
            PeriodLabel = periodLabel,
            TotalPlays = inPeriod.Count,
            TotalMinutes = (long)Math.Round(totalMinutes),
            UniqueTracks = inPeriod.Select(e => e.TrackId).Distinct().Count(),
            UniqueArtists = inPeriod
                .Where(e => !string.IsNullOrWhiteSpace(e.Artist))
                .Select(e => Track.GetPrimaryArtist(e.Artist).Trim().ToLowerInvariant())
                .Distinct()
                .Count(),
            UniqueAlbums = albumGroups.Count,
            LosslessPercent = resolvedPlays > 0 ? losslessPlays * 100.0 / resolvedPlays : 0,
            HiResPercent = resolvedPlays > 0 ? hiResPlays * 100.0 / resolvedPlays : 0,
            TopGenre = topGenres.Count > 0 ? topGenres[0].Name : "—",
            TopTracks = topTracks,
            TopArtists = topArtists,
            TopAlbums = topAlbums,
            TopGenres = topGenres,
            TopAlbumArtworkPath = albumGroups.Count > 0
                ? albumGroups[0].First().Track!.AlbumArtworkPath
                : null,
        };
    }

    /// <summary>
    /// Random placeholder stats for previewing/tuning the Wrap UI and share card
    /// without needing a year of real play history.
    /// </summary>
    public static WrapStats BuildTestData()
    {
        var rng = Random.Shared;
        string[] artists = { "Within Temptation", "Nightwish", "Lacuna Coil", "Gracie Abrams", "New West", "Arctic Monkeys", "Massive Attack", "Epica" };
        string[] tracks = { "Those Eyes", "Close To You", "Ghost Love Score", "Our Solemn Hour", "Heaven", "Storytime", "505", "Teardrop" };
        string[] albums = { "The Silent Force", "Once", "Comalies", "The Secret of Us", "AM", "Mezzanine" };
        string[] genres = { "Symphonic Metal", "Indie Pop", "Gothic Metal", "Alternative Rock", "Trip-Hop" };

        List<WrapEntry> Pick(string[] pool, bool subtitled)
        {
            var picked = pool.OrderBy(_ => rng.Next()).Take(TopCount).ToArray();
            var plays = Enumerable.Range(0, TopCount)
                .Select(_ => rng.Next(12, 320))
                .OrderByDescending(p => p)
                .ToArray();
            return picked.Select((name, i) => new WrapEntry
            {
                Rank = i + 1,
                Name = name,
                Subtitle = subtitled ? artists[rng.Next(artists.Length)] : string.Empty,
                Plays = plays[i],
            }).ToList();
        }

        var topGenres = Pick(genres, subtitled: false);
        return new WrapStats
        {
            PeriodLabel = DateTime.Now.Year.ToString(),
            TotalPlays = rng.Next(1_500, 18_000),
            TotalMinutes = rng.Next(5_000, 90_000),
            UniqueTracks = rng.Next(300, 2_500),
            UniqueArtists = rng.Next(60, 600),
            UniqueAlbums = rng.Next(40, 400),
            LosslessPercent = rng.Next(20, 100),
            HiResPercent = rng.Next(0, 40),
            TopGenre = topGenres[0].Name,
            TopTracks = Pick(tracks, subtitled: true),
            TopArtists = Pick(artists, subtitled: false),
            TopAlbums = Pick(albums, subtitled: true),
            TopGenres = topGenres,
        };
    }
}
