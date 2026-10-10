using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Honest listening figures derived from the persistent play log, shared by the
/// Settings → Statistics tab and the standalone Statistics page so the two
/// surfaces never disagree. Time reflects what was actually played: skipped
/// events contribute nothing.
/// </summary>
public sealed class ListeningStats
{
    /// <summary>Total play events in the log (started plays, including skips).</summary>
    public int TotalPlays { get; init; }

    /// <summary>Non-skipped events — plays heard past the skip threshold.</summary>
    public int CompletedPlays { get; init; }

    /// <summary>Events the user skipped away from early.</summary>
    public int SkippedPlays { get; init; }

    /// <summary>Sum of completed plays' track durations. Excludes skipped plays.</summary>
    public long TimeListenedTicks { get; init; }

    /// <summary>Average length of a completed play (TimeListened / CompletedPlays).</summary>
    public long AvgListenedTrackLengthTicks { get; init; }

    /// <summary>Consecutive days with at least one play, ending today or yesterday.</summary>
    public int CurrentStreakDays { get; init; }

    /// <summary>Longest run of consecutive days with at least one play.</summary>
    public int LongestStreakDays { get; init; }

    /// <summary>Plays in the rolling 7 days ending today.</summary>
    public int PlaysThisWeek { get; init; }

    /// <summary>Plays in the 7 days before this week.</summary>
    public int PlaysLastWeek { get; init; }
}

/// <summary>
/// Finds the library track a play-log event belongs to: by id, and when that id is gone,
/// by title + artist (the log stores both beside the id). A thin handle on the Wrap's own
/// resolver (<see cref="WrapStatsBuilder.TrackResolver"/>), so the page and the Wrap place
/// every play on the same track.
///
/// Track ids are a hash of the file path, so a re-added folder gives every song a new id
/// while the log keeps the old one. On the owner's dev profile (10-09) only 60 of 8,470
/// events still matched an id: 99% of plays counted the 3.5-minute fallback and dropped out
/// of every album, cover and duration lookup.
///
/// Not thread-safe (it memoizes per event id): build one per computation.
/// </summary>
public sealed class PlayEventResolver
{
    private readonly WrapStatsBuilder.TrackResolver _resolver;

    public PlayEventResolver(IEnumerable<Track> tracks)
    {
        var byId = new Dictionary<Guid, Track>();
        foreach (var t in tracks) byId[t.Id] = t;
        _resolver = new WrapStatsBuilder.TrackResolver(byId);
    }

    public PlayEventResolver(IReadOnlyDictionary<Guid, Track> tracksById) =>
        _resolver = new WrapStatsBuilder.TrackResolver(tracksById);

    /// <summary>The library track the event was a play of, or null when it left the library.</summary>
    public Track? Resolve(PlayHistoryEvent e) => _resolver.Resolve(e);
}

/// <summary>
/// Pure computation — events in, stats out — so it stays unit-testable. Event
/// timestamps are interpreted in local time, matching the Statistics page and
/// <see cref="WrapStatsBuilder"/>.
/// </summary>
public static class ListeningStatsCalculator
{
    /// <summary>Track length assumed for plays whose track left the library: the Wrap's.</summary>
    internal static TimeSpan FallbackTrackLength => WrapStatsBuilder.FallbackTrackLength;

    public static ListeningStats Compute(
        IReadOnlyList<PlayHistoryEvent> events,
        IReadOnlyDictionary<Guid, Track> tracksById,
        DateTime? nowLocal = null)
        => events.Count == 0
            ? new ListeningStats()
            : Compute(events, new PlayEventResolver(tracksById), nowLocal);

    public static ListeningStats Compute(
        IReadOnlyList<PlayHistoryEvent> events,
        PlayEventResolver resolver,
        DateTime? nowLocal = null)
    {
        if (events.Count == 0)
            return new ListeningStats();

        var today = (nowLocal ?? DateTime.Now).Date;

        int completed = 0, skipped = 0;
        long listenedTicks = 0;
        int thisWeek = 0, lastWeek = 0;
        var playDays = new HashSet<DateTime>();

        foreach (var e in events)
        {
            var localDate = e.PlayedAtUtc.ToLocalTime().Date;
            playDays.Add(localDate);

            if (e.Skipped)
            {
                skipped++;
            }
            else
            {
                completed++;
                listenedTicks += PlayedLength(resolver.Resolve(e)).Ticks;
            }

            var daysAgo = (today - localDate).Days;
            if (daysAgo is >= 0 and <= 6) thisWeek++;
            else if (daysAgo is >= 7 and <= 13) lastWeek++;
        }

        var (current, longest) = ComputeStreaks(playDays, today);

        return new ListeningStats
        {
            TotalPlays = events.Count,
            CompletedPlays = completed,
            SkippedPlays = skipped,
            TimeListenedTicks = listenedTicks,
            AvgListenedTrackLengthTicks = completed > 0 ? listenedTicks / completed : 0,
            CurrentStreakDays = current,
            LongestStreakDays = longest,
            PlaysThisWeek = thisWeek,
            PlaysLastWeek = lastWeek,
        };
    }

    internal static TimeSpan PlayedLength(Track? track) =>
        track != null && track.Duration > TimeSpan.Zero ? track.Duration : FallbackTrackLength;

    /// <summary>
    /// Current streak counts back from today (or yesterday, as a grace day if the
    /// user hasn't played yet today); longest scans every run of consecutive days.
    /// </summary>
    internal static (int current, int longest) ComputeStreaks(HashSet<DateTime> playDays, DateTime today)
    {
        if (playDays.Count == 0) return (0, 0);

        var sorted = playDays.OrderBy(d => d).ToList();

        int longest = 1, run = 1;
        for (var i = 1; i < sorted.Count; i++)
        {
            run = (sorted[i] - sorted[i - 1]).Days == 1 ? run + 1 : 1;
            if (run > longest) longest = run;
        }

        DateTime? anchor =
            playDays.Contains(today) ? today :
            playDays.Contains(today.AddDays(-1)) ? today.AddDays(-1) :
            null;

        var current = 0;
        if (anchor is { } day)
        {
            while (playDays.Contains(day))
            {
                current++;
                day = day.AddDays(-1);
            }
        }

        return (current, longest);
    }
}

/// <summary>The window the Statistics page summarizes.</summary>
public enum ListeningPeriod
{
    Last7Days,
    Last30Days,
    ThisYear,
    AllTime,
}

/// <summary>One ranked artist, album or song of a <see cref="ListeningReport"/>.</summary>
public sealed class ListeningRank
{
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public int Plays { get; init; }

    /// <summary>A library track behind the entry (the most-played one), null when the
    /// plays belong to songs that left the library. Source of covers and the album link.</summary>
    public Track? Track { get; init; }

    /// <summary>Album entries: the library album id.</summary>
    public Guid AlbumId { get; init; }
}

/// <summary>A song the user tends to skip, for the Most Skipped list.</summary>
public sealed class ListeningSkip
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public int Plays { get; init; }
    public int Skips { get; init; }
    public double Rate => Plays > 0 ? (double)Skips / Plays : 0;
    public Track? Track { get; init; }
}

/// <summary>One logged play inside the period, with the library track it resolved to.</summary>
public readonly record struct ListeningPlay(PlayHistoryEvent Event, Track? Track, DateTime PlayedLocal);

/// <summary>
/// Everything the Statistics page shows about listening, for one period, from ONE source:
/// the play log. The page used to take Total Plays and Top Artists/Albums from
/// <see cref="Track.PlayCount"/> and everything else from the log, so the tiles contradicted
/// each other (60 total plays beside 102 this week) and the Wrap (6ix9ine 18 vs Taylor Swift
/// 2,513) whenever play counts and the log diverge — a re-added folder resets the counts and
/// keeps the log.
///
/// A play is a logged event that ran at least <see cref="ListeningReportBuilder.MinimumPlay"/>
/// (until the next one started), skipped or not, which is what the Wrap counts. Listening time
/// counts the plays that weren't skipped, each for as long as it can have run.
/// </summary>
public sealed class ListeningReport
{
    public ListeningPeriod Period { get; init; }

    /// <summary>First local day of the period; null for <see cref="ListeningPeriod.AllTime"/>.</summary>
    public DateTime? StartLocal { get; init; }

    public int Plays { get; init; }
    public int CompletedPlays { get; init; }
    public int SkippedPlays { get; init; }
    public long ListenedTicks { get; init; }

    /// <summary>Logged plays in the period cut off within <see cref="ListeningReportBuilder.MinimumPlay"/>:
    /// not counted anywhere.</summary>
    public int ShortPlays { get; init; }

    /// <summary>The same, across the whole log.</summary>
    public int LoggedShortPlays { get; init; }

    /// <summary>Plays in the comparison window (the previous 7 / 30 days, or the same span of
    /// last year); null for all time, and when the log doesn't reach back that far.</summary>
    public int? PreviousPlays { get; init; }

    public int UniqueTracks { get; init; }
    public int UniqueArtists { get; init; }

    /// <summary>Streaks span the whole log, whatever the period.</summary>
    public int CurrentStreakDays { get; init; }
    public int LongestStreakDays { get; init; }

    /// <summary>Whole log: how many events it holds and when the oldest was played.</summary>
    public int LoggedPlays { get; init; }
    public DateTime? FirstPlayLocal { get; init; }

    /// <summary>Plays per local hour of day (24 entries) inside the period.</summary>
    public IReadOnlyList<int> HourCounts { get; init; } = new int[24];

    public IReadOnlyList<ListeningRank> TopArtists { get; init; } = Array.Empty<ListeningRank>();
    public IReadOnlyList<ListeningRank> TopAlbums { get; init; } = Array.Empty<ListeningRank>();
    public IReadOnlyList<ListeningRank> TopTracks { get; init; } = Array.Empty<ListeningRank>();
    public IReadOnlyList<ListeningSkip> MostSkipped { get; init; } = Array.Empty<ListeningSkip>();

    /// <summary>The period's plays, newest first.</summary>
    public IReadOnlyList<ListeningPlay> Recent { get; init; } = Array.Empty<ListeningPlay>();

    /// <summary>Latest logged play (UTC) per resolved library track, across the whole log.</summary>
    public IReadOnlyDictionary<Guid, DateTime> LastPlayedUtc { get; init; } = new Dictionary<Guid, DateTime>();
}

/// <summary>Builds a <see cref="ListeningReport"/>. Pure: events, library and clock in.</summary>
public static class ListeningReportBuilder
{
    public const int TopArtistCount = 6;
    public const int TopListCount = 8;
    public const int MostSkippedCount = 5;

    /// <summary>A song needs this many plays in the period before its skip rate means anything.</summary>
    public const int MinPlaysForSkipRate = 3;

    /// <summary>
    /// A logged play the next one cut off sooner than this was never really heard, so it is
    /// left out of every figure: the Wrap's rule (<see cref="WrapStatsBuilder.MinimumPlay"/>).
    /// The owner's dev log (10-09) held 8,470 plays of which 4,795 were followed by another
    /// within a second (2,164 of them on Jul 16): bursts that counted as plays and full time.
    /// </summary>
    public static TimeSpan MinimumPlay => WrapStatsBuilder.MinimumPlay;

    /// <summary>
    /// The period's first day and its comparison window (both inclusive local dates).
    /// 7 / 30 days compare with the 7 / 30 days before; this year compares with last year up
    /// to the same day; all time has no comparison.
    /// </summary>
    public static (DateTime? Start, DateTime? PrevStart, DateTime? PrevEnd) Range(ListeningPeriod period, DateTime today)
    {
        today = today.Date;
        switch (period)
        {
            case ListeningPeriod.Last7Days:
                return (today.AddDays(-6), today.AddDays(-13), today.AddDays(-7));
            case ListeningPeriod.Last30Days:
                return (today.AddDays(-29), today.AddDays(-59), today.AddDays(-30));
            case ListeningPeriod.ThisYear:
                var jan1 = new DateTime(today.Year, 1, 1);
                return (jan1, jan1.AddYears(-1), today.AddYears(-1));
            default:
                return (null, null, null);
        }
    }

    private sealed class Tally
    {
        public int Plays;
        public int Skips;
        public DateTime LatestUtc;
        public PlayHistoryEvent? Latest;
        public Track? Track;
        private int _facePlays;
        private Dictionary<Guid, int>? _trackPlays;

        public void Add(PlayHistoryEvent e, Track? track)
        {
            Plays++;
            if (e.Skipped) Skips++;
            if (Latest == null || e.PlayedAtUtc >= LatestUtc)
            {
                Latest = e;
                LatestUtc = e.PlayedAtUtc;
            }
            if (track == null) return;
            if (Track == null)
            {
                Track = track;
                _facePlays = 1;
                return;
            }
            if (_trackPlays == null && ReferenceEquals(Track, track))
            {
                _facePlays++;
                return;
            }
            // More than one library track in this bucket (an artist or an album): keep the
            // most-played one as its face. A song's bucket never gets here.
            _trackPlays ??= new Dictionary<Guid, int> { [Track.Id] = _facePlays };
            var plays = _trackPlays[track.Id] = _trackPlays.GetValueOrDefault(track.Id) + 1;
            if (plays > _trackPlays[Track.Id])
                Track = track;
        }
    }

    public static ListeningReport Build(
        IReadOnlyList<PlayHistoryEvent> events,
        PlayEventResolver resolver,
        ListeningPeriod period,
        DateTime? nowLocal = null)
    {
        var today = (nowLocal ?? DateTime.Now).Date;
        var (start, prevStart, prevEnd) = Range(period, today);

        var hours = new int[24];
        var playDays = new HashSet<DateTime>();
        var lastPlayed = new Dictionary<Guid, DateTime>();
        var songs = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var artists = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var albums = new Dictionary<Guid, Tally>();
        var recent = new List<ListeningPlay>();
        int plays = 0, completed = 0, skipped = 0, previous = 0, shortInPeriod = 0, shortLogged = 0;
        long listened = 0;
        DateTime? first = null;

        var log = WrapStatsBuilder.Chronological(events);
        for (var i = 0; i < log.Count; i++)
        {
            var e = log[i];
            var local = e.PlayedAtUtc.ToLocalTime();
            var date = local.Date;
            if (first == null || local < first) first = local;
            var inPeriod = start == null || (date >= start && date <= today);

            // How long this play can have run: until the next one started (unknown for the last).
            TimeSpan? ran = i + 1 < log.Count ? log[i + 1].PlayedAtUtc - e.PlayedAtUtc : null;
            if (ran < MinimumPlay)
            {
                shortLogged++;
                if (inPeriod) shortInPeriod++;
                continue;
            }

            playDays.Add(date);
            var track = resolver.Resolve(e);
            if (track != null && (!lastPlayed.TryGetValue(track.Id, out var seen) || e.PlayedAtUtc > seen))
                lastPlayed[track.Id] = e.PlayedAtUtc;

            if (prevStart != null && date >= prevStart && date <= prevEnd)
                previous++;

            if (!inPeriod)
                continue;

            plays++;
            hours[local.Hour]++;
            if (e.Skipped)
            {
                skipped++;
            }
            else
            {
                completed++;
                listened += WrapStatsBuilder.Heard(track, ran).Ticks;
            }
            recent.Add(new ListeningPlay(e, track, local));

            // A library track by its id; a play the library cannot place by its name, so one
            // song logged under several old ids counts once. The Wrap's song key.
            var songKey = track != null
                ? track.Id.ToString("N")
                : "~" + WrapStatsBuilder.Normalize(e.Title) + "\u001f" + WrapStatsBuilder.Normalize(e.Artist);
            if (!songs.TryGetValue(songKey, out var song)) songs[songKey] = song = new Tally();
            song.Add(e, track);

            // Artists exactly as the Wrap counts them: the logged credit's primary artist.
            if (!string.IsNullOrWhiteSpace(e.Artist))
            {
                var artistName = Track.GetPrimaryArtist(e.Artist).Trim();
                if (!artists.TryGetValue(artistName, out var artist)) artists[artistName] = artist = new Tally();
                artist.Add(e, track);
            }

            if (track != null && Track.IsRealAlbumName(track.Album))
            {
                if (!albums.TryGetValue(track.AlbumId, out var album)) albums[track.AlbumId] = album = new Tally();
                album.Add(e, track);
            }
        }

        // Newest first. The log is appended in order, but a clock change can leave it unsorted.
        recent.Sort((a, b) => b.Event.PlayedAtUtc.CompareTo(a.Event.PlayedAtUtc));

        var (current, longest) = ListeningStatsCalculator.ComputeStreaks(playDays, today);

        return new ListeningReport
        {
            Period = period,
            StartLocal = start,
            Plays = plays,
            CompletedPlays = completed,
            SkippedPlays = skipped,
            ShortPlays = shortInPeriod,
            LoggedShortPlays = shortLogged,
            ListenedTicks = listened,
            // Compared only when the log reaches back to the comparison window's first day, as
            // the Wrap does: a log that began inside it would compare a partial window with a whole.
            PreviousPlays = prevStart != null && first is { } logStart && logStart.Date <= prevStart ? previous : null,
            UniqueTracks = songs.Count,
            UniqueArtists = artists.Count,
            CurrentStreakDays = current,
            LongestStreakDays = longest,
            LoggedPlays = events.Count,
            FirstPlayLocal = first,
            HourCounts = hours,
            TopArtists = Rank(artists, TopArtistCount, (name, t) => new ListeningRank
            {
                Name = name, Plays = t.Plays, Track = t.Track,
            }),
            TopAlbums = Rank(albums, TopListCount, (id, t) => new ListeningRank
            {
                Name = t.Track!.Album,
                Subtitle = string.IsNullOrWhiteSpace(t.Track.AlbumArtist) ? t.Track.Artist : t.Track.AlbumArtist,
                Plays = t.Plays,
                Track = t.Track,
                AlbumId = id,
            }),
            TopTracks = Rank(songs, TopListCount, (_, t) => new ListeningRank
            {
                Name = t.Track?.Title ?? t.Latest!.Title,
                Subtitle = t.Track?.Artist ?? t.Latest!.Artist,
                Plays = t.Plays,
                Track = t.Track,
                AlbumId = t.Track?.AlbumId ?? Guid.Empty,
            }),
            MostSkipped = songs.Values
                .Where(t => t.Plays >= MinPlaysForSkipRate && t.Skips > 0)
                .OrderByDescending(t => (double)t.Skips / t.Plays)
                .ThenByDescending(t => t.Skips)
                .ThenByDescending(t => t.LatestUtc)
                .Take(MostSkippedCount)
                .Select(t => new ListeningSkip
                {
                    Title = t.Track?.Title ?? t.Latest!.Title,
                    Artist = t.Track?.Artist ?? t.Latest!.Artist,
                    Plays = t.Plays,
                    Skips = t.Skips,
                    Track = t.Track,
                })
                .ToList(),
            Recent = recent,
            LastPlayedUtc = lastPlayed,
        };
    }

    /// <summary>Most plays first; ties go to the more recently played, then by name.</summary>
    private static List<ListeningRank> Rank<TKey>(Dictionary<TKey, Tally> tallies, int take,
        Func<TKey, Tally, ListeningRank> make) where TKey : notnull =>
        tallies
            .OrderByDescending(kv => kv.Value.Plays)
            .ThenByDescending(kv => kv.Value.LatestUtc)
            .Take(take)
            .Select(kv => make(kv.Key, kv.Value))
            .ToList();
}
