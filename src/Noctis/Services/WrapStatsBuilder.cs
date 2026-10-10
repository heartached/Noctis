using System.Globalization;
using System.Text.Json.Serialization;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>One ranked row in a Wrap top-list.</summary>
public sealed class WrapEntry
{
    public int Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public int Plays { get; init; }
    /// <summary>Cover of the album or song (albums, songs); null when it has none.</summary>
    public string? ArtworkPath { get; init; }
    /// <summary>This row's share of the list's plays, 0-1 (genres: of all genre-tagged plays).</summary>
    public double Share { get; init; }

    /// <summary>Artist portrait from the app's cache, filled in when shown; never stored.</summary>
    [JsonIgnore]
    public string? ImagePath { get; set; }

    /// <summary>Bar length relative to the list's top row (0-1), for proportional bars.</summary>
    [JsonIgnore]
    public double BarFraction { get; set; }

    [JsonIgnore]
    public string PlaysLabel => WrapStatsBuilder.FormatPlays(Plays);

    [JsonIgnore]
    public string ShareLabel => $"{Share * 100:0}%";

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    [JsonIgnore]
    public bool HasArtwork => !string.IsNullOrEmpty(ArtworkPath);

    /// <summary>First letter of the name, for the placeholder portrait.</summary>
    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : char.ToUpperInvariant(Name.Trim()[0]).ToString();
}

/// <summary>One bar of the Wrap timeline (a month of a year, a day of a month).</summary>
public sealed class WrapBucket
{
    /// <summary>First day the bucket covers (local date).</summary>
    public DateTime Start { get; init; }
    public int Plays { get; init; }
    public long Minutes { get; init; }
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

    // ── Report extras (absent from snapshots archived before the 10-09 revamp) ──

    /// <summary>True for a single month (the timeline is then per day).</summary>
    public bool IsMonth { get; init; }
    /// <summary>Plays started in each hour of the day, 0-23 (the listening clock).</summary>
    public int[] PlaysByHour { get; init; } = new int[24];
    /// <summary>Plays per month (year) or per day (month), covering the whole period.</summary>
    public IReadOnlyList<WrapBucket> Timeline { get; init; } = Array.Empty<WrapBucket>();
    /// <summary>Days with at least one play.</summary>
    public int ActiveDays { get; init; }
    /// <summary>Days the averages are taken over: the period so far, from where recording began.</summary>
    public int DaysCovered { get; init; }
    public double AverageMinutesPerDay { get; init; }
    public DateTime? MostActiveDay { get; init; }
    public int MostActiveDayPlays { get; init; }
    public long MostActiveDayMinutes { get; init; }
    /// <summary>Longest run of consecutive days with a play, inside the period.</summary>
    public int LongestStreakDays { get; init; }
    public DateTime? LongestStreakStart { get; init; }
    /// <summary>Artists first heard in this period; null when the log does not reach back
    /// before the period, so "first heard" cannot be told.</summary>
    public int? NewArtists { get; init; }
    /// <summary>Set when the play log only starts inside the period: the first day it covers.</summary>
    public DateTime? RecordedSince { get; init; }
    /// <summary>The same stretch of the previous period, when the log fully covers it.</summary>
    public WrapComparison? Previous { get; init; }
    /// <summary>Logged plays in the period that the next one cut off within
    /// <see cref="WrapStatsBuilder.MinimumPlay"/>: left out of every figure.</summary>
    public int ShortPlays { get; init; }
}

/// <summary>Totals for the matching stretch of the previous period (Sep 1-9 for Oct 1-9).</summary>
public sealed class WrapComparison
{
    public DateTime Start { get; init; }
    /// <summary>Exclusive end (local).</summary>
    public DateTime End { get; init; }
    public int Plays { get; init; }
    public long Minutes { get; init; }
}

/// <summary>
/// Builds Noctis Wrap recaps from the persistent play log. Pure computation —
/// events in, stats out — so it stays unit-testable.
/// </summary>
public static class WrapStatsBuilder
{
    private const int TopCount = 5;

    /// <summary>Average track length assumed for plays whose track left the library.</summary>
    internal static readonly TimeSpan FallbackTrackLength = TimeSpan.FromMinutes(3.5);

    /// <summary>
    /// A logged play the next one cut off sooner than this was never really heard, so the
    /// recap leaves it out (the 30-second rule streaming services count a play by). Noctis
    /// plays one song at a time, so the next play's start is where this one ended at the latest.
    /// The owner's log (10-09) held 8,470 plays of which 4,795 were followed by another within
    /// a second — bursts of hundreds a minute that counted as plays and as full-length minutes.
    /// </summary>
    public static readonly TimeSpan MinimumPlay = TimeSpan.FromSeconds(30);

    /// <summary>"1 play" / "2,513 plays", localized.</summary>
    public static string FormatPlays(int plays) =>
        plays == 1 ? Loc.T("Wrap.OnePlay") : Loc.T("Wrap.PlaysCount", plays.ToString("N0", Loc.Instance.Culture));

    /// <summary>
    /// Computes the recap for a period. <paramref name="month"/> null = whole year.
    /// Event timestamps are interpreted in local time, matching the Statistics page.
    /// <paramref name="nowLocal"/> (default: now) bounds an in-progress period for the
    /// daily average and the comparison with the previous one.
    /// </summary>
    public static WrapStats Build(
        IReadOnlyList<PlayHistoryEvent> events,
        IReadOnlyDictionary<Guid, Track> tracksById,
        int year,
        int? month = null,
        DateTime? nowLocal = null)
    {
        var periodStart = new DateTime(year, month ?? 1, 1);
        var periodEnd = month == null ? periodStart.AddYears(1) : periodStart.AddMonths(1);
        var periodLabel = month == null
            ? year.ToString()
            : periodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        var now = nowLocal ?? DateTime.Now;
        // In-progress periods run to the end of today; finished ones to their end.
        var effectiveEnd = now < periodEnd ? now.Date.AddDays(1) : periodEnd;
        if (effectiveEnd < periodStart) effectiveEnd = periodStart;

        var prevStart = month == null ? periodStart.AddYears(-1) : periodStart.AddMonths(-1);
        var prevEnd = effectiveEnd >= periodEnd ? periodStart : prevStart + (effectiveEnd - periodStart);
        if (prevEnd > periodStart) prevEnd = periodStart;

        var resolver = new TrackResolver(tracksById);
        var primaryArtists = new Dictionary<string, string>(StringComparer.Ordinal);
        string PrimaryArtistKey(string artist)
        {
            if (!primaryArtists.TryGetValue(artist, out var primary))
            {
                primary = Track.GetPrimaryArtist(artist).Trim();
                primaryArtists[artist] = primary;
            }
            return primary;
        }

        var inPeriod = new List<(PlayHistoryEvent Event, DateTime Local, Track? Track, double Minutes)>();
        var artistsBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logStart = DateTime.MaxValue;
        int prevPlays = 0, shortPlays = 0;
        double prevMinutes = 0;

        var log = Chronological(events);
        for (var i = 0; i < log.Count; i++)
        {
            var e = log[i];
            var local = e.PlayedAtUtc.ToLocalTime();
            if (local < logStart) logStart = local;

            var inThisPeriod = local >= periodStart && local < periodEnd;
            if (!inThisPeriod && local >= periodStart) continue;

            // How long this play can have run: until the next one started (unknown for the last).
            TimeSpan? ran = i + 1 < log.Count ? log[i + 1].PlayedAtUtc - e.PlayedAtUtc : null;
            if (ran < MinimumPlay)
            {
                if (inThisPeriod) shortPlays++;
                continue;
            }

            var track = inThisPeriod || (local >= prevStart && local < prevEnd) ? resolver.Resolve(e) : null;
            var minutes = e.Skipped ? 0 : Heard(track, ran).TotalMinutes;
            if (inThisPeriod)
            {
                inPeriod.Add((e, local, track, minutes));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(e.Artist))
                artistsBefore.Add(PrimaryArtistKey(e.Artist));
            if (local >= prevStart && local < prevEnd)
            {
                prevPlays++;
                prevMinutes += minutes;
            }
        }

        if (inPeriod.Count == 0)
            return new WrapStats { PeriodLabel = periodLabel, IsMonth = month != null, ShortPlays = shortPlays };

        double totalMinutes = 0;
        int resolvedPlays = 0, losslessPlays = 0, hiResPlays = 0;
        var byHour = new int[24];
        var days = new Dictionary<DateTime, (int Plays, double Minutes)>();
        foreach (var (e, local, track, minutes) in inPeriod)
        {
            totalMinutes += minutes;
            byHour[local.Hour]++;
            days.TryGetValue(local.Date, out var day);
            days[local.Date] = (day.Plays + 1, day.Minutes + minutes);

            if (track == null) continue;
            resolvedPlays++;
            if (track.IsLossless) losslessPlays++;
            if (track.IsHiResLossless) hiResPlays++;
        }

        // Songs: a library track by its id, a play the library can't place by its name — so
        // one song logged under its old and new id counts once.
        var trackGroups = inPeriod
            .GroupBy(x => x.Track != null
                ? x.Track.Id.ToString("N")
                : "~" + Normalize(x.Event.Title) + "\u001f" + Normalize(x.Event.Artist))
            .ToList();
        var topTracks = trackGroups
            .Select(g => new { Plays = g.Count(), Latest = g.MaxBy(x => x.Local) })
            .OrderByDescending(x => x.Plays)
            .ThenByDescending(x => x.Latest.Local)
            .Take(TopCount)
            .Select((x, i) => new WrapEntry
            {
                Rank = i + 1,
                Name = x.Latest.Track?.Title ?? x.Latest.Event.Title,
                Subtitle = x.Latest.Track?.Artist ?? x.Latest.Event.Artist,
                Plays = x.Plays,
                ArtworkPath = x.Latest.Track?.AlbumArtworkPath,
                Share = x.Plays / (double)inPeriod.Count,
            })
            .ToList();

        var artistGroups = inPeriod
            .Where(x => !string.IsNullOrWhiteSpace(x.Event.Artist))
            .GroupBy(x => PrimaryArtistKey(x.Event.Artist), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var topArtists = artistGroups
            .OrderByDescending(g => g.Count())
            .Take(TopCount)
            .Select((g, i) => new WrapEntry
            {
                Rank = i + 1,
                Name = g.Key,
                Plays = g.Count(),
                Share = g.Count() / (double)inPeriod.Count,
            })
            .ToList();

        // Albums and genres need the library track for metadata; plays it can't place drop out.
        var resolved = inPeriod.Where(x => x.Track != null).Select(x => x.Track!).ToList();

        var albumGroups = resolved
            .Where(t => Track.IsRealAlbumName(t.Album))
            .GroupBy(t => t.AlbumId)
            .OrderByDescending(g => g.Count())
            .ToList();
        var topAlbums = albumGroups
            .Take(TopCount)
            .Select((g, i) =>
            {
                var first = g.First();
                return new WrapEntry
                {
                    Rank = i + 1,
                    Name = first.Album,
                    Subtitle = string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.Artist : first.AlbumArtist,
                    Plays = g.Count(),
                    ArtworkPath = AlbumCover(g),
                    Share = g.Count() / (double)resolved.Count,
                };
            })
            .ToList();

        // A play of a multi-genre track ("Rock; Pop") counts for each of its genres; shares
        // stay out of the plays that have a genre at all (GitHub #123 follow-up, 2026-10-10).
        var genreGroups = resolved
            .SelectMany(t => Track.SplitGenres(t.Genre))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var taggedPlays = resolved.Count(t => !string.IsNullOrWhiteSpace(t.Genre));
        var topGenres = genreGroups
            .OrderByDescending(g => g.Count())
            .Take(TopCount)
            .Select((g, i) => new WrapEntry
            {
                Rank = i + 1,
                Name = g.Key,
                Plays = g.Count(),
                Share = g.Count() / (double)taggedPlays,
            })
            .ToList();

        // Where the averages start: the period's first day when the log reaches back past it,
        // otherwise the first day it recorded anything.
        var coverageStart = logStart < periodStart ? periodStart : logStart.Date;
        var daysCovered = Math.Max(1, (int)Math.Ceiling((effectiveEnd - coverageStart).TotalDays));

        var busiest = days.OrderByDescending(d => d.Value.Plays).ThenBy(d => d.Key).First();
        var (streak, streakStart) = LongestStreak(days.Keys);

        int? newArtists = logStart < periodStart
            ? artistGroups.Count(g => !artistsBefore.Contains(g.Key))
            : null;

        // Only when the log reaches back to the previous stretch's first day: a log that began
        // inside it would compare a partial stretch with a whole one.
        WrapComparison? previous = logStart.Date <= prevStart && prevEnd > prevStart
            ? new WrapComparison
            {
                Start = prevStart,
                End = prevEnd,
                Plays = prevPlays,
                Minutes = (long)Math.Round(prevMinutes),
            }
            : null;

        return new WrapStats
        {
            PeriodLabel = periodLabel,
            TotalPlays = inPeriod.Count,
            TotalMinutes = (long)Math.Round(totalMinutes),
            UniqueTracks = trackGroups.Count,
            UniqueArtists = artistGroups.Count,
            UniqueAlbums = albumGroups.Count,
            LosslessPercent = resolvedPlays > 0 ? losslessPlays * 100.0 / resolvedPlays : 0,
            HiResPercent = resolvedPlays > 0 ? hiResPlays * 100.0 / resolvedPlays : 0,
            TopGenre = topGenres.Count > 0 ? topGenres[0].Name : "—",
            TopTracks = topTracks,
            TopArtists = topArtists,
            TopAlbums = topAlbums,
            TopGenres = topGenres,
            TopAlbumArtworkPath = topAlbums.Count > 0 ? topAlbums[0].ArtworkPath : null,
            IsMonth = month != null,
            PlaysByHour = byHour,
            Timeline = BuildTimeline(days, periodStart, periodEnd, month != null),
            ActiveDays = days.Count,
            DaysCovered = daysCovered,
            AverageMinutesPerDay = totalMinutes / daysCovered,
            MostActiveDay = busiest.Key,
            MostActiveDayPlays = busiest.Value.Plays,
            MostActiveDayMinutes = (long)Math.Round(busiest.Value.Minutes),
            LongestStreakDays = streak,
            LongestStreakStart = streakStart,
            NewArtists = newArtists,
            RecordedSince = coverageStart > periodStart ? coverageStart : null,
            Previous = previous,
            ShortPlays = shortPlays,
        };
    }

    /// <summary>The song's length, or less when the next play started before it could end.</summary>
    internal static TimeSpan Heard(Track? track, TimeSpan? ran)
    {
        var length = track != null && track.Duration > TimeSpan.Zero ? track.Duration : FallbackTrackLength;
        return ran is { } r && r < length ? r : length;
    }

    /// <summary>The log oldest first (as PlayHistoryService keeps it; sorted only if not).</summary>
    internal static IReadOnlyList<PlayHistoryEvent> Chronological(IReadOnlyList<PlayHistoryEvent> events)
    {
        DateTime? previous = null;
        foreach (var e in events)
        {
            if (previous > e.PlayedAtUtc)
                return events.OrderBy(x => x.PlayedAtUtc).ToList();
            previous = e.PlayedAtUtc;
        }
        return events;
    }

    internal static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The album's cover: the artwork most of its played tracks show (a track with
    /// its own embedded cover carries that one instead, see Track.AlbumArtworkPath).</summary>
    private static string? AlbumCover(IEnumerable<Track> tracks) =>
        tracks.Where(t => !string.IsNullOrEmpty(t.AlbumArtworkPath))
            .DistinctBy(t => t.Id)
            .GroupBy(t => t.AlbumArtworkPath!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    private static IReadOnlyList<WrapBucket> BuildTimeline(
        Dictionary<DateTime, (int Plays, double Minutes)> days, DateTime start, DateTime end, bool perDay)
    {
        var buckets = new List<WrapBucket>();
        for (var b = start; b < end; b = perDay ? b.AddDays(1) : b.AddMonths(1))
        {
            var bucketEnd = perDay ? b.AddDays(1) : b.AddMonths(1);
            int plays = 0;
            double minutes = 0;
            foreach (var (day, value) in days)
            {
                if (day < b || day >= bucketEnd) continue;
                plays += value.Plays;
                minutes += value.Minutes;
            }
            buckets.Add(new WrapBucket { Start = b, Plays = plays, Minutes = (long)Math.Round(minutes) });
        }
        return buckets;
    }

    private static (int Days, DateTime? Start) LongestStreak(IEnumerable<DateTime> activeDays)
    {
        var sorted = activeDays.OrderBy(d => d).ToList();
        if (sorted.Count == 0) return (0, null);
        int best = 1, run = 1;
        DateTime bestStart = sorted[0], runStart = sorted[0];
        for (var i = 1; i < sorted.Count; i++)
        {
            if ((sorted[i] - sorted[i - 1]).Days == 1) run++;
            else { run = 1; runStart = sorted[i]; }
            if (run > best) { best = run; bestStart = runStart; }
        }
        return (best, bestStart);
    }

    /// <summary>
    /// Finds the library track behind a logged play. By id first; a play whose id the library
    /// no longer has falls back to its logged title + artist. Track ids are hashes of the file
    /// path (LibraryService.ComputeFileId), so moving or re-adding the library gives every
    /// track a new id while the log keeps the old one — without the fallback, every play from
    /// before the move dropped out of albums, genres, quality and minutes.
    /// </summary>
    internal sealed class TrackResolver
    {
        private readonly IReadOnlyDictionary<Guid, Track> _byId;
        private Dictionary<string, Track>? _byName;
        private readonly Dictionary<Guid, Track?> _resolvedMisses = new();

        public TrackResolver(IReadOnlyDictionary<Guid, Track> byId) => _byId = byId;

        public Track? Resolve(PlayHistoryEvent e)
        {
            if (_byId.TryGetValue(e.TrackId, out var track)) return track;
            if (_resolvedMisses.TryGetValue(e.TrackId, out var cached)
                && (cached == null || Key(cached.Title, cached.Artist) == Key(e.Title, e.Artist)))
                return cached;

            _byName ??= BuildNameIndex();
            _byName.TryGetValue(Key(e.Title, e.Artist), out var match);
            _resolvedMisses[e.TrackId] = match;
            return match;
        }

        private static string Key(string? title, string? artist) => Normalize(title) + "\u001f" + Normalize(artist);

        private Dictionary<string, Track> BuildNameIndex()
        {
            var index = new Dictionary<string, Track>(StringComparer.Ordinal);
            foreach (var t in _byId.Values)
            {
                if (string.IsNullOrWhiteSpace(t.Title)) continue;
                var key = Key(t.Title, t.Artist);
                if (!index.TryGetValue(key, out var held) || IsBetterMatch(t, held))
                    index[key] = t;
            }
            return index;
        }

        /// <summary>Several copies of a song (single and album): credit the album, then the
        /// bigger release, then a stable path order so the pick never changes between runs.</summary>
        private static bool IsBetterMatch(Track candidate, Track held)
        {
            var a = Track.IsRealAlbumName(candidate.Album);
            var b = Track.IsRealAlbumName(held.Album);
            if (a != b) return a;
            if (candidate.TrackCount != held.TrackCount) return candidate.TrackCount > held.TrackCount;
            return string.CompareOrdinal(candidate.FilePath, held.FilePath) < 0;
        }
    }
}
