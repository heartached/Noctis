using System.Diagnostics;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Noctis Wrap revamp (owner 10-09): the numbers behind the Last.fm-style report, and the
/// inconsistent figures in the owner's screenshot ("22 Albums explored" beside 3,050 unique
/// tracks, a top album with 17 plays under a top artist with 2,513, Hi-Res 0%).
/// </summary>
public class WrapReportTests
{
    private readonly ITestOutputHelper _output;
    public WrapReportTests(ITestOutputHelper output) => _output = output;

    private static Track Song(string title, string artist, string album, string genre = "Pop",
        double minutes = 4, bool hiRes = false, string? albumArtist = null, string? artwork = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        AlbumArtist = albumArtist ?? artist,
        Album = album,
        AlbumId = Track.ComputeAlbumId(albumArtist ?? artist, album),
        Genre = genre,
        Duration = TimeSpan.FromMinutes(minutes),
        Codec = "FLAC",
        FilePath = $"A:/music/{artist}/{album}/{title}.flac",
        BitsPerSample = hiRes ? 24 : 16,
        SampleRate = hiRes ? 96000 : 44100,
        AlbumArtworkPath = artwork,
    };

    private static PlayHistoryEvent Play(Track t, DateTime local, Guid? loggedId = null, bool skipped = false) => new()
    {
        TrackId = loggedId ?? t.Id,
        Title = t.Title,
        Artist = t.Artist,
        PlayedAtUtc = local.ToUniversalTime(),
        Skipped = skipped,
    };

    private static Dictionary<Guid, Track> Library(params Track[] tracks) => tracks.ToDictionary(t => t.Id);

    private static DateTime Local(int year, int month, int day, int hour = 12) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Local);

    /// <summary>
    /// Bug (owner 10-09 screenshot, reproduced on the owner's own play log: 8,410 of 8,470
    /// events carried ids the library no longer has). A track's id is a hash of its file path
    /// (LibraryService.ComputeFileId), so re-adding the library from another folder gives every
    /// track a new id while the play log keeps the old ones. Artists and songs are counted from
    /// the names the log stores, but albums, genres, lossless/Hi-Res and minutes were counted
    /// only from events whose id still resolved: a few dozen recent plays. The same song played
    /// before and after the move also counted as two unique tracks.
    /// </summary>
    [Fact]
    public void PlaysLoggedBeforeTheLibraryMoved_StillCountTowardAlbumsGenresAndQuality()
    {
        var a = Song("Leave the Door Open", "Silk Sonic", "An Evening With Silk Sonic", "R&B/Soul", minutes: 4, hiRes: true);
        var b = Song("Skate", "Silk Sonic", "An Evening With Silk Sonic", "R&B/Soul", minutes: 3);
        var lib = Library(a, b);
        var oldIdA = Guid.NewGuid();
        var oldIdB = Guid.NewGuid();

        var events = new List<PlayHistoryEvent>();
        for (var i = 0; i < 8; i++) events.Add(Play(a, Local(2026, 7, 1 + i), loggedId: oldIdA));
        for (var i = 0; i < 4; i++) events.Add(Play(b, Local(2026, 7, 10 + i), loggedId: oldIdB));
        events.Add(Play(b, Local(2026, 10, 8))); // after the re-add: the current id

        var stats = WrapStatsBuilder.Build(events, lib, 2026);

        // One tuple so a failure shows every figure at once.
        Assert.Equal(
            (Plays: 13, UniqueTracks: 2, Albums: 1, TopAlbumPlays: 13, TopGenrePlays: 13,
                HiRes: 8 * 100.0 / 13, Lossless: 100.0, TopSongPlays: 8, SecondSongPlays: 5, Minutes: 8L * 4 + 5 * 3),
            (stats.TotalPlays, stats.UniqueTracks, stats.UniqueAlbums, stats.TopAlbums[0].Plays, stats.TopGenres[0].Plays,
                stats.HiResPercent, stats.LosslessPercent, stats.TopTracks[0].Plays, stats.TopTracks[1].Plays, stats.TotalMinutes));
    }

    /// <summary>
    /// Bug (owner's log, 10-09): 4,795 of its 8,470 plays were followed by the next one within
    /// a second (Jul 16 09:00-10:00 alone: 2,136 "plays", 5.6 ms apart, shuffling through the
    /// library), and each counted as a play and as a full-length listen. Spread over a big
    /// catalogue they made Taylor Swift the top artist with 2,513 plays while no song of hers
    /// reached the top five (1,786 of her plays were such bursts). A play the next one cut off
    /// within 30 seconds is now left out, and a play's minutes stop where the next one started.
    /// </summary>
    [Fact]
    public void PlaysCutOffWithinSeconds_DoNotCount()
    {
        var hit = Song("Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", minutes: 4);
        var catalogue = Enumerable.Range(0, 40).Select(i => Song($"Track {i}", "Taylor Swift", "Midnights", minutes: 4)).ToArray();
        var lib = Library(catalogue.Append(hit).ToArray());

        var events = new List<PlayHistoryEvent>();
        for (var d = 0; d < 10; d++) events.Add(Play(hit, Local(2026, 7, 1 + d, 20)));
        var burst = Local(2026, 7, 16, 9);
        for (var i = 0; i < catalogue.Length; i++) events.Add(Play(catalogue[i], burst.AddMilliseconds(i * 6)));
        events.Add(Play(hit, Local(2026, 7, 16, 9).AddMinutes(2))); // the last burst play ran 2 minutes
        events = events.OrderBy(e => e.PlayedAtUtc).ToList();

        var stats = WrapStatsBuilder.Build(events, lib, 2026);

        Assert.Equal(
            (Plays: 12, TopArtist: "Juice WRLD", TopArtistPlays: 11, Minutes: 11L * 4 + 2),
            (stats.TotalPlays, stats.TopArtists[0].Name, stats.TopArtists[0].Plays, stats.TotalMinutes));
    }

    /// <summary>A play whose id is gone and whose name matches two copies of the song (the
    /// single and the album) is credited to the album, the same pick every time.</summary>
    [Fact]
    public void UnplacedPlay_MatchingSeveralCopies_GoesToTheAlbumCopy()
    {
        var single = Song("Sunflower", "Post Malone", "Sunflower - Single");
        single.TrackCount = 1;
        var album = Song("Sunflower", "Post Malone", "Hollywood's Bleeding");
        album.TrackCount = 17;
        var lib = Library(single, album);
        var events = new[] { Play(album, Local(2026, 3, 1), loggedId: Guid.NewGuid()) };

        var stats = WrapStatsBuilder.Build(events, lib, 2026, nowLocal: Local(2026, 3, 2));

        Assert.Equal("Hollywood's Bleeding", stats.TopAlbums.Single().Name);
    }

    /// <summary>A play the library cannot place at all still counts as a song and an artist,
    /// with the average track length, and stays out of albums, genres and quality.</summary>
    [Fact]
    public void UnplaceablePlay_CountsAsSongAndArtist_Only()
    {
        var kept = Song("Kept", "Artist", "Album", "Pop", minutes: 4);
        var gone = new PlayHistoryEvent
        {
            TrackId = Guid.NewGuid(), Title = "Gone", Artist = "Ghost",
            PlayedAtUtc = Local(2026, 3, 1).ToUniversalTime(),
        };
        var stats = WrapStatsBuilder.Build(new[] { gone, Play(kept, Local(2026, 3, 2)) }, Library(kept), 2026,
            nowLocal: Local(2026, 3, 3));

        Assert.Equal(2, stats.UniqueTracks);
        Assert.Equal(2, stats.UniqueArtists);
        Assert.Equal(1, stats.UniqueAlbums);
        Assert.Equal(1, stats.TopGenres.Single().Plays);
        Assert.Equal(8, stats.TotalMinutes); // 4 + 3.5, rounded
    }

    /// <summary>The "Unknown Album" placeholder is a shared bucket, not an album the user explored.</summary>
    [Fact]
    public void UnknownAlbumPlaceholder_IsNotAnAlbum()
    {
        var a = Song("A", "X", "Unknown Album");
        var b = Song("B", "X", "Real");
        var stats = WrapStatsBuilder.Build(new[] { Play(a, Local(2026, 3, 1)), Play(a, Local(2026, 3, 1, 13)), Play(b, Local(2026, 3, 2)) },
            Library(a, b), 2026, nowLocal: Local(2026, 3, 3));

        Assert.Equal(1, stats.UniqueAlbums);
        Assert.Equal("Real", stats.TopAlbums.Single().Name);
    }

    /// <summary>The album row carries the cover most of its played songs show, and the share
    /// card's tint follows the top album.</summary>
    [Fact]
    public void AlbumRows_CarryTheAlbumCover()
    {
        var a = Song("A", "X", "LP", artwork: "C:/covers/lp.jpg");
        var b = Song("B", "X", "LP", artwork: "C:/covers/lp.jpg");
        var c = Song("C", "X", "LP", artwork: "C:/covers/c-own.jpg");
        var events = new[] { Play(c, Local(2026, 3, 1)), Play(c, Local(2026, 3, 1, 13)), Play(a, Local(2026, 3, 2)), Play(b, Local(2026, 3, 3)) };

        var stats = WrapStatsBuilder.Build(events, Library(a, b, c), 2026, nowLocal: Local(2026, 3, 4));

        Assert.Equal("C:/covers/lp.jpg", stats.TopAlbums[0].ArtworkPath);
        Assert.Equal("C:/covers/lp.jpg", stats.TopAlbumArtworkPath);
        Assert.Equal("C:/covers/c-own.jpg", stats.TopTracks[0].ArtworkPath);
    }

    [Fact]
    public void ListeningClock_CountsPlaysByLocalHour()
    {
        var t = Song("A", "X", "LP");
        var events = new[]
        {
            Play(t, Local(2026, 3, 1, 23)), Play(t, Local(2026, 3, 2, 23)), Play(t, Local(2026, 3, 3, 0)),
            Play(t, Local(2026, 3, 3, 7), skipped: true),
        };

        var stats = WrapStatsBuilder.Build(events, Library(t), 2026, nowLocal: Local(2026, 3, 4));

        Assert.Equal(24, stats.PlaysByHour.Length);
        Assert.Equal(2, stats.PlaysByHour[23]);
        Assert.Equal(1, stats.PlaysByHour[0]);
        Assert.Equal(1, stats.PlaysByHour[7]);
        Assert.Equal(4, stats.PlaysByHour.Sum());
    }

    [Fact]
    public void Timeline_IsPerMonthForAYear_AndPerDayForAMonth()
    {
        var t = Song("A", "X", "LP");
        var events = new[] { Play(t, Local(2026, 2, 3)), Play(t, Local(2026, 2, 3, 15)), Play(t, Local(2026, 2, 28)), Play(t, Local(2026, 11, 1)) };
        var lib = Library(t);

        var year = WrapStatsBuilder.Build(events, lib, 2026, nowLocal: Local(2026, 12, 31));
        Assert.Equal(12, year.Timeline.Count);
        Assert.Equal(new[] { 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0 }, year.Timeline.Select(b => b.Plays));
        Assert.False(year.IsMonth);

        var month = WrapStatsBuilder.Build(events, lib, 2026, 2, nowLocal: Local(2026, 12, 31));
        Assert.Equal(28, month.Timeline.Count);
        Assert.Equal(2, month.Timeline[2].Plays);
        Assert.Equal(1, month.Timeline[27].Plays);
        Assert.Equal(new DateTime(2026, 2, 3), month.Timeline[2].Start);
        Assert.True(month.IsMonth);
    }

    [Fact]
    public void Days_ActiveMostActiveAndLongestStreak()
    {
        var t = Song("A", "X", "LP", minutes: 3);
        var events = new List<PlayHistoryEvent>
        {
            // Mar 2-4: a 3-day run; Mar 10-13: a 4-day run (the longest); Mar 11 busiest.
            Play(t, Local(2026, 3, 2)), Play(t, Local(2026, 3, 3)), Play(t, Local(2026, 3, 4)),
            Play(t, Local(2026, 3, 10)), Play(t, Local(2026, 3, 11)), Play(t, Local(2026, 3, 11, 14)),
            Play(t, Local(2026, 3, 11, 15)), Play(t, Local(2026, 3, 12)), Play(t, Local(2026, 3, 13)),
        };

        var stats = WrapStatsBuilder.Build(events, Library(t), 2026, 3, nowLocal: Local(2026, 4, 2));

        Assert.Equal(7, stats.ActiveDays);
        Assert.Equal(new DateTime(2026, 3, 11), stats.MostActiveDay);
        Assert.Equal(3, stats.MostActiveDayPlays);
        Assert.Equal(9, stats.MostActiveDayMinutes);
        Assert.Equal(4, stats.LongestStreakDays);
        Assert.Equal(new DateTime(2026, 3, 10), stats.LongestStreakStart);
    }

    /// <summary>The daily average runs over the days the log covers: from the period's start
    /// when the log reaches back past it, from the first recorded day otherwise (which the
    /// header then names), and only up to today in a period still running.</summary>
    [Fact]
    public void DailyAverage_CoversTheRecordedPartOfThePeriodSoFar()
    {
        var t = Song("A", "X", "LP", minutes: 10);
        // The log starts on Jun 11 (the owner's real log does); "today" is Jun 20.
        var events = new[] { Play(t, Local(2026, 6, 11)), Play(t, Local(2026, 6, 15)) };

        var year = WrapStatsBuilder.Build(events, Library(t), 2026, nowLocal: Local(2026, 6, 20));
        Assert.Equal(10, year.DaysCovered); // Jun 11-20
        Assert.Equal(2.0, year.AverageMinutesPerDay, 3);
        Assert.Equal(new DateTime(2026, 6, 11), year.RecordedSince);

        var withHistory = events.Prepend(Play(t, Local(2025, 12, 31))).ToArray();
        var full = WrapStatsBuilder.Build(withHistory, Library(t), 2026, nowLocal: Local(2026, 6, 20));
        Assert.Equal(171, full.DaysCovered); // Jan 1 - Jun 20
        Assert.Null(full.RecordedSince);

        var finished = WrapStatsBuilder.Build(withHistory, Library(t), 2025, nowLocal: Local(2026, 6, 20));
        Assert.Equal(1, finished.DaysCovered); // the log only reaches Dec 31 of 2025
    }

    /// <summary>"New artists" needs the log to reach back before the period; otherwise it is
    /// unknown (null), not "everyone".</summary>
    [Fact]
    public void NewArtists_AreThoseNeverHeardBefore_WhenTheLogReachesBack()
    {
        var old = Song("Old", "Taylor Swift", "LP");
        var fresh = Song("Fresh", "Chase Atlantic", "BEAUTY IN DEATH");
        var duet = Song("Duet", "Taylor Swift", "LP2");
        var lib = Library(old, fresh, duet);
        var thisMonth = new[] { Play(old, Local(2026, 10, 2)), Play(fresh, Local(2026, 10, 3)), Play(duet, Local(2026, 10, 4)) };

        var unknown = WrapStatsBuilder.Build(thisMonth, lib, 2026, 10, nowLocal: Local(2026, 10, 9));
        Assert.Null(unknown.NewArtists);

        var withHistory = thisMonth.Prepend(Play(old, Local(2026, 9, 1))).ToArray();
        var known = WrapStatsBuilder.Build(withHistory, lib, 2026, 10, nowLocal: Local(2026, 10, 9));
        Assert.Equal(1, known.NewArtists);
        Assert.Equal(2, known.UniqueArtists);
    }

    /// <summary>A month still running is compared with the same days of the previous month
    /// (Oct 1-9 against Sep 1-9), a finished one with the whole previous month; no comparison
    /// when the log does not cover the previous stretch.</summary>
    [Fact]
    public void Previous_ComparesTheSameStretch_OnlyWhenTheLogCoversIt()
    {
        var t = Song("A", "X", "LP", minutes: 2);
        var lib = Library(t);
        var events = new[]
        {
            Play(t, Local(2026, 9, 1)), Play(t, Local(2026, 9, 9, 22)), Play(t, Local(2026, 9, 10)), Play(t, Local(2026, 9, 25)),
            Play(t, Local(2026, 10, 2)), Play(t, Local(2026, 10, 8)), Play(t, Local(2026, 10, 9)),
        };

        var running = WrapStatsBuilder.Build(events, lib, 2026, 10, nowLocal: Local(2026, 10, 9, 18));
        Assert.NotNull(running.Previous);
        Assert.Equal(new DateTime(2026, 9, 1), running.Previous!.Start);
        Assert.Equal(new DateTime(2026, 9, 10), running.Previous.End);
        Assert.Equal(2, running.Previous.Plays);
        Assert.Equal(4, running.Previous.Minutes);

        var finished = WrapStatsBuilder.Build(events, lib, 2026, 10, nowLocal: Local(2026, 11, 5));
        Assert.Equal(new DateTime(2026, 10, 1), finished.Previous!.End);
        Assert.Equal(4, finished.Previous.Plays);

        // The log starts on Sep 9: Sep 1-9 isn't fully recorded, so no comparison.
        var trimmed = WrapStatsBuilder.Build(events.Skip(1).ToArray(), lib, 2026, 10, nowLocal: Local(2026, 10, 9, 18));
        Assert.Null(trimmed.Previous);

        // A year compares with the previous year only when the log reaches back to its January.
        var year = WrapStatsBuilder.Build(events, lib, 2026, nowLocal: Local(2026, 10, 9));
        Assert.Null(year.Previous);
    }

    [Fact]
    public void Genres_CarryTheirShareOfTaggedPlays()
    {
        var rap = Song("A", "X", "LP", "Hip-Hop/Rap");
        var rnb = Song("B", "Y", "LP2", "R&B/Soul");
        var untagged = Song("C", "Z", "LP3", "");
        var events = new[]
        {
            Play(rap, Local(2026, 3, 1)), Play(rap, Local(2026, 3, 2)), Play(rap, Local(2026, 3, 3)),
            Play(rnb, Local(2026, 3, 4)), Play(untagged, Local(2026, 3, 5)),
        };

        var stats = WrapStatsBuilder.Build(events, Library(rap, rnb, untagged), 2026, nowLocal: Local(2026, 3, 6));

        Assert.Equal("Hip-Hop/Rap", stats.TopGenre);
        Assert.Equal(0.75, stats.TopGenres[0].Share, 3);
        Assert.Equal(0.25, stats.TopGenres[1].Share, 3);
        Assert.Equal("75%", stats.TopGenres[0].ShareLabel);
    }

    [Fact]
    public void PlaysLabel_IsSingularForOne_AndGroupsThousands()
    {
        Assert.Equal("1 play", new WrapEntry { Plays = 1 }.PlaysLabel);
        Assert.Equal("0 plays", new WrapEntry { Plays = 0 }.PlaysLabel);
        Assert.Equal("2,513 plays", new WrapEntry { Plays = 2513 }.PlaysLabel);
    }

    /// <summary>Snapshots archived before the revamp have none of the report fields; they load
    /// with empty defaults (no chart data) instead of failing. New snapshots keep them, and the
    /// display-only artist portrait is never written.</summary>
    [Fact]
    public void ArchivedSnapshots_OldAndNew_RoundTrip()
    {
        const string legacy = """
            {"PeriodLabel":"2025","TotalPlays":12,"TotalMinutes":40,"TopArtists":[{"Rank":1,"Name":"X","Subtitle":"","Plays":12}]}
            """;
        var old = JsonSerializer.Deserialize<WrapStats>(legacy)!;
        Assert.Equal(24, old.PlaysByHour.Length);
        Assert.Empty(old.Timeline);
        Assert.Null(old.Previous);
        Assert.Equal("12 plays", old.TopArtists[0].PlaysLabel);

        var t = Song("A", "X", "LP", artwork: "C:/a.jpg");
        var built = WrapStatsBuilder.Build(new[] { Play(t, Local(2026, 3, 1, 21)) }, Library(t), 2026, nowLocal: Local(2026, 3, 2));
        built.TopArtists[0].ImagePath = "C:/portrait.jpg";
        var json = JsonSerializer.Serialize(built);
        Assert.DoesNotContain("portrait", json);
        var back = JsonSerializer.Deserialize<WrapStats>(json)!;
        Assert.Equal(1, back.PlaysByHour[21]);
        Assert.Equal(12, back.Timeline.Count);
        Assert.Equal("C:/a.jpg", back.TopAlbums[0].ArtworkPath);
    }

    /// <summary>
    /// Performance: a synthetic 50,000-play year over a 10,000-track library, a fifth of the
    /// plays logged under ids from before a library move (the name fallback's worst case).
    /// The build runs on the thread pool in the dialog; this pins it to well under a second.
    /// </summary>
    [Fact]
    public void Build_FiftyThousandPlays_IsFast()
    {
        var rng = new Random(7);
        var tracks = Enumerable.Range(0, 10_000)
            .Select(i => Song($"Song {i}", $"Artist {i % 400}", $"Album {i % 900}", $"Genre {i % 25}",
                minutes: 2 + i % 4, hiRes: i % 9 == 0))
            .ToArray();
        var lib = Library(tracks);
        var events = new List<PlayHistoryEvent>(50_000);
        var start = Local(2026, 1, 1, 0);
        for (var i = 0; i < 50_000; i++)
        {
            var t = tracks[(int)Math.Min(tracks.Length - 1, Math.Abs(rng.NextDouble() * rng.NextDouble()) * tracks.Length)];
            events.Add(Play(t, start.AddMinutes(i * 8.5), loggedId: i % 5 == 0 ? Guid.NewGuid() : null));
        }

        WrapStatsBuilder.Build(events, lib, 2026, nowLocal: Local(2026, 10, 9)); // JIT warm-up
        var sw = Stopwatch.StartNew();
        var stats = WrapStatsBuilder.Build(events, lib, 2026, nowLocal: Local(2026, 10, 9));
        sw.Stop();
        var month = Stopwatch.StartNew();
        WrapStatsBuilder.Build(events, lib, 2026, 6, nowLocal: Local(2026, 10, 9));
        month.Stop();
        _output.WriteLine($"50k plays / 10k tracks: year {sw.ElapsedMilliseconds} ms, month {month.ElapsedMilliseconds} ms");

        Assert.Equal(events.Count(e => e.PlayedAtUtc.ToLocalTime().Year == 2026), stats.TotalPlays);
        Assert.Equal(100, stats.LosslessPercent); // every play placed, the moved ones by name
        Assert.True(sw.ElapsedMilliseconds < 1500, $"year build took {sw.ElapsedMilliseconds} ms");
    }
}
