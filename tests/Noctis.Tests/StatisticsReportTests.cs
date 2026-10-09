using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Every aggregate of the Statistics page, built by <see cref="ListeningReportBuilder"/>.</summary>
public class StatisticsReportTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 15, 30, 0); // a Friday, local

    private static Track Song(string title, string artist, string album = "Album", double minutes = 4) => new()
    {
        Title = title, Artist = artist, Album = album, AlbumArtist = artist,
        AlbumId = DeterministicGuid(album + "|" + artist),
        Duration = TimeSpan.FromMinutes(minutes),
    };

    private static Guid DeterministicGuid(string s)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return new Guid(bytes);
    }

    private static PlayHistoryEvent At(Track t, DateTime local, bool skipped = false) => new()
    {
        TrackId = t.Id, Title = t.Title, Artist = t.Artist, PlayedAtUtc = local.ToUniversalTime(), Skipped = skipped,
    };

    private static ListeningReport Build(IEnumerable<Track> tracks, IReadOnlyList<PlayHistoryEvent> events,
        ListeningPeriod period) =>
        ListeningReportBuilder.Build(events, new PlayEventResolver(tracks), period, Now);

    [Fact]
    public void Last7Days_CoversTodayAndTheSixDaysBefore_AndComparesWithTheSevenBefore()
    {
        var t = Song("A", "X");
        var events = new[]
        {
            At(t, Now.Date.AddHours(1)),               // today
            At(t, Now.Date.AddDays(-6).AddHours(23)),  // first day of the window
            At(t, Now.Date.AddDays(-7).AddHours(12)),  // previous window, last day
            At(t, Now.Date.AddDays(-13).AddHours(0)),  // previous window, first day
            At(t, Now.Date.AddDays(-14).AddHours(9)),  // outside both
        };

        var r = Build(new[] { t }, events, ListeningPeriod.Last7Days);

        Assert.Equal(Now.Date.AddDays(-6), r.StartLocal);
        Assert.Equal(2, r.Plays);
        Assert.Equal(2, r.PreviousPlays);
        Assert.Equal(5, r.LoggedPlays);
    }

    [Fact]
    public void ThisYear_ComparesWithLastYearUpToTheSameDay()
    {
        var t = Song("A", "X");
        var events = new[]
        {
            At(t, new DateTime(2024, 12, 30, 10, 0, 0)), // the log reaches back past Jan 1 2025
            At(t, new DateTime(2025, 3, 1, 10, 0, 0)),
            At(t, new DateTime(2025, 10, 9, 22, 0, 0)), // same day last year: counts
            At(t, new DateTime(2025, 10, 10, 10, 0, 0)), // past it: doesn't
            At(t, new DateTime(2026, 1, 1, 0, 30, 0)),
        };

        var r = Build(new[] { t }, events, ListeningPeriod.ThisYear);

        Assert.Equal(1, r.Plays);
        Assert.Equal(2, r.PreviousPlays);
    }

    [Fact]
    public void NoComparison_WhenTheLogStartsInsideThePreviousWindow()
    {
        var t = Song("A", "X");
        // The log begins in June: "this year vs the same span of last year" has no last year.
        var r = Build(new[] { t }, new[] { At(t, new DateTime(2026, 6, 11, 9, 0, 0)), At(t, Now.AddHours(-2)) },
            ListeningPeriod.ThisYear);

        Assert.Null(r.PreviousPlays);
        Assert.Equal(2, r.Plays);
    }

    [Fact]
    public void AllTime_HasNoComparison_AndKnowsWhenTheLogStarts()
    {
        var t = Song("A", "X");
        var first = new DateTime(2026, 6, 11, 6, 8, 0);
        var r = Build(new[] { t }, new[] { At(t, first), At(t, Now.AddHours(-1)) }, ListeningPeriod.AllTime);

        Assert.Null(r.PreviousPlays);
        Assert.Null(r.StartLocal);
        Assert.Equal(first, r.FirstPlayLocal);
        Assert.Equal(2, r.Plays);
    }

    [Fact]
    public void PlaysCutOffWithin30Seconds_CountNowhere_AndTimeStopsAtTheNextPlay()
    {
        var t = Song("A", "X", minutes: 4);
        var start = Now.AddHours(-5);
        var events = new[]
        {
            At(t, start),                                   // next starts 10 s later: not a play
            At(t, start.AddSeconds(10)),                    // runs 2 min, then the next starts
            At(t, start.AddSeconds(10).AddMinutes(2)),      // the last: runs on, full length
        };

        var r = Build(new[] { t }, events, ListeningPeriod.Last7Days);

        Assert.Equal(2, r.Plays);
        Assert.Equal(1, r.ShortPlays);
        Assert.Equal(1, r.LoggedShortPlays);
        Assert.Equal(TimeSpan.FromMinutes(2 + 4).Ticks, r.ListenedTicks);
        Assert.Equal(2, r.Recent.Count);
        Assert.Equal(2, r.HourCounts.Sum());
    }

    [Fact]
    public void ListeningTime_CountsOnlyUnskippedPlays_SkipsStillCountAsPlays()
    {
        var t = Song("A", "X", minutes: 5);
        var r = Build(new[] { t }, new[] { At(t, Now.AddHours(-3)), At(t, Now.AddHours(-2), skipped: true) },
            ListeningPeriod.Last7Days);

        Assert.Equal(2, r.Plays);
        Assert.Equal(1, r.CompletedPlays);
        Assert.Equal(1, r.SkippedPlays);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, r.ListenedTicks);
    }

    [Fact]
    public void HourCounts_UseLocalTime_AndOnlyThePeriod()
    {
        var t = Song("A", "X");
        var r = Build(new[] { t }, new[]
        {
            At(t, Now.Date.AddHours(22).AddMinutes(5)),
            At(t, Now.Date.AddDays(-1).AddHours(22).AddMinutes(50)),
            At(t, Now.Date.AddHours(7)),
            At(t, Now.Date.AddDays(-40).AddHours(3)), // outside 30 days
        }, ListeningPeriod.Last30Days);

        Assert.Equal(24, r.HourCounts.Count);
        Assert.Equal(2, r.HourCounts[22]);
        Assert.Equal(1, r.HourCounts[7]);
        Assert.Equal(0, r.HourCounts[3]);
    }

    [Fact]
    public void TopLists_RankByPlays_TiesGoToTheMoreRecent()
    {
        var a = Song("Song A", "Artist A", "Album A");
        var b = Song("Song B", "Artist B", "Album B");
        var c = Song("Song C", "Artist B", "Album B");
        var events = new List<PlayHistoryEvent>
        {
            At(a, Now.AddHours(-10)), At(a, Now.AddHours(-9)),
            At(b, Now.AddHours(-8)), At(c, Now.AddHours(-1)),
        };

        var r = Build(new[] { a, b, c }, events, ListeningPeriod.Last7Days);

        Assert.Equal(new[] { "Artist B", "Artist A" }, r.TopArtists.Select(x => x.Name));
        Assert.Equal(new[] { 2, 2 }, r.TopArtists.Select(x => x.Plays));
        Assert.Equal(new[] { "Album B", "Album A" }, r.TopAlbums.Select(x => x.Name)); // tie: B played last
        Assert.Equal(b.AlbumId, r.TopAlbums[0].AlbumId);
        Assert.Equal(new[] { "Song A", "Song C", "Song B" }, r.TopTracks.Select(x => x.Name));
        Assert.Equal(2, r.UniqueArtists);
        Assert.Equal(3, r.UniqueTracks);
    }

    [Fact]
    public void AlbumAndArtistEntries_UseTheirMostPlayedTrackAsTheFace()
    {
        var b = Song("Deep Cut", "Artist", "LP");
        var c = Song("Hit", "Artist", "LP");
        b.AlbumArtworkPath = "b.jpg";
        c.AlbumArtworkPath = "c.jpg";
        var events = new[] { At(b, Now.AddHours(-5)), At(c, Now.AddHours(-4)), At(c, Now.AddHours(-3)) };

        var r = Build(new[] { b, c }, events, ListeningPeriod.Last7Days);

        Assert.Same(c, r.TopAlbums[0].Track);
        Assert.Same(c, r.TopArtists[0].Track);
    }

    [Fact]
    public void Plays_OfSongsThatLeftTheLibrary_StillCountUnderTheLoggedArtist()
    {
        var gone = new PlayHistoryEvent
        {
            TrackId = Guid.NewGuid(), Title = "Old", Artist = "Ghost feat. Friend", PlayedAtUtc = Now.AddHours(-1).ToUniversalTime(),
        };

        var r = Build(Array.Empty<Track>(), new[] { gone }, ListeningPeriod.Last7Days);

        Assert.Equal("Ghost", r.TopArtists.Single().Name);
        Assert.Empty(r.TopAlbums); // no library track, no album
        Assert.Equal("Old", r.TopTracks.Single().Name);
        Assert.Equal(TimeSpan.FromMinutes(3.5).Ticks, r.ListenedTicks);
    }

    [Fact]
    public void TopArtists_MatchTheWrap_ForTheSameYear()
    {
        var swift = Song("Love Story", "Taylor Swift", "Fearless");
        var feat = Song("us.", "Gracie Abrams feat. Taylor Swift", "The Secret of Us");
        var juice = Song("Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance");
        var events = new List<PlayHistoryEvent>();
        for (var i = 0; i < 12; i++) events.Add(At(swift, Now.AddDays(-i).AddHours(-1), skipped: i % 4 == 0));
        for (var i = 0; i < 5; i++) events.Add(At(feat, Now.AddDays(-i).AddHours(-2)));
        for (var i = 0; i < 9; i++) events.Add(At(juice, Now.AddDays(-i).AddHours(-3)));
        // A burst: 40 plays a second apart (holding Next) — none of them counts, in either.
        for (var i = 0; i < 40; i++) events.Add(At(feat, Now.AddDays(-20).AddSeconds(i)));
        events.Sort((x, y) => x.PlayedAtUtc.CompareTo(y.PlayedAtUtc));
        var tracks = new[] { swift, feat, juice };

        var report = Build(tracks, events, ListeningPeriod.ThisYear);
        var wrap = WrapStatsBuilder.Build(events, tracks.ToDictionary(t => t.Id), Now.Year);

        Assert.Equal(26 + 1, report.Plays); // the burst's last play ran on: it counts
        Assert.Equal(wrap.TotalPlays, report.Plays);
        Assert.Equal(wrap.TopArtists.Select(a => (a.Name, a.Plays)),
            report.TopArtists.Take(wrap.TopArtists.Count).Select(a => (a.Name, a.Plays)));
        Assert.Equal(wrap.TotalMinutes, (long)Math.Round(TimeSpan.FromTicks(report.ListenedTicks).TotalMinutes));
    }

    [Fact]
    public void MostSkipped_NeedsThreePlays_AndRanksByRate()
    {
        var often = Song("Often", "X");
        var always = Song("Always", "Y");
        var twice = Song("Twice", "Z");
        var events = new List<PlayHistoryEvent>();
        for (var i = 0; i < 4; i++) events.Add(At(often, Now.AddHours(-10 + i), skipped: i < 2));   // 50%
        for (var i = 0; i < 3; i++) events.Add(At(always, Now.AddHours(-5 + i), skipped: true));   // 100%
        for (var i = 0; i < 2; i++) events.Add(At(twice, Now.AddHours(-1), skipped: true));       // too few

        var r = Build(new[] { often, always, twice }, events, ListeningPeriod.Last7Days);

        Assert.Equal(new[] { "Always", "Often" }, r.MostSkipped.Select(s => s.Title));
        Assert.Equal(1.0, r.MostSkipped[0].Rate);
        Assert.Equal(0.5, r.MostSkipped[1].Rate);
    }

    [Fact]
    public void Recent_IsNewestFirst_EvenWhenTheLogIsOutOfOrder()
    {
        var t = Song("A", "X");
        var early = At(t, Now.AddHours(-5));
        var late = At(t, Now.AddHours(-1));
        var r = Build(new[] { t }, new[] { late, early }, ListeningPeriod.Last7Days);

        Assert.Same(late, r.Recent[0].Event);
        Assert.Same(early, r.Recent[1].Event);
        Assert.Same(t, r.Recent[0].Track);
    }

    [Fact]
    public void Streaks_SpanTheWholeLog_WhateverThePeriod()
    {
        var t = Song("A", "X");
        var events = Enumerable.Range(0, 18).Select(d => At(t, Now.Date.AddDays(-60 + d).AddHours(12)))
            .Append(At(t, Now.Date.AddHours(9))).Append(At(t, Now.Date.AddDays(-1).AddHours(9)))
            .ToArray();

        var r = Build(new[] { t }, events, ListeningPeriod.Last7Days);

        Assert.Equal(2, r.CurrentStreakDays);
        Assert.Equal(18, r.LongestStreakDays);
    }

    [Fact]
    public void LastPlayed_IsTheLatestLoggedPlayPerLibraryTrack()
    {
        var t = Song("A", "X");
        var old = new PlayHistoryEvent { TrackId = Guid.NewGuid(), Title = "A", Artist = "X", PlayedAtUtc = Now.AddDays(-400).ToUniversalTime() };
        var r = Build(new[] { t }, new[] { old, At(t, Now.AddDays(-200)) }, ListeningPeriod.Last7Days);

        Assert.Equal(Now.AddDays(-200).ToUniversalTime(), r.LastPlayedUtc[t.Id]);
    }

    [Fact]
    public void Resolver_PrefersTheId_ThenTitleAndArtist_IgnoringCase()
    {
        var a = Song("Love Story", "Taylor Swift");
        var resolver = new PlayEventResolver(new[] { a });

        Assert.Same(a, resolver.Resolve(new PlayHistoryEvent { TrackId = a.Id, Title = "renamed", Artist = "renamed" }));
        Assert.Same(a, resolver.Resolve(new PlayHistoryEvent { TrackId = Guid.NewGuid(), Title = " love story ", Artist = "TAYLOR SWIFT" }));
        Assert.Null(resolver.Resolve(new PlayHistoryEvent { TrackId = Guid.NewGuid(), Title = "Love Story", Artist = "Someone Else" }));
        Assert.Null(resolver.Resolve(new PlayHistoryEvent { TrackId = Guid.NewGuid(), Title = "", Artist = "" }));
    }

    [Fact]
    public void Resolver_CreditsTheAlbumCut_OverTheSingle_LikeTheWrap()
    {
        var single = Song("Espresso", "Sabrina Carpenter", "Espresso - Single");
        single.TrackCount = 1;
        single.FilePath = "a/single.flac";
        var albumCut = Song("Espresso", "Sabrina Carpenter", "Short n' Sweet");
        albumCut.TrackCount = 12;
        albumCut.FilePath = "b/album.flac";
        var resolver = new PlayEventResolver(new[] { single, albumCut });

        Assert.Same(albumCut, resolver.Resolve(new PlayHistoryEvent { TrackId = Guid.NewGuid(), Title = "Espresso", Artist = "Sabrina Carpenter" }));
    }

    [Fact]
    public void Resolver_MemoizesPerOldId_ButNotAcrossDifferentSongs()
    {
        var a = Song("First", "X");
        var b = Song("Second", "X");
        var resolver = new PlayEventResolver(new[] { a, b });
        var oldId = Guid.NewGuid();

        Assert.Same(a, resolver.Resolve(new PlayHistoryEvent { TrackId = oldId, Title = "First", Artist = "X" }));
        Assert.Same(a, resolver.Resolve(new PlayHistoryEvent { TrackId = oldId, Title = "First", Artist = "X" }));
        Assert.Same(b, resolver.Resolve(new PlayHistoryEvent { TrackId = oldId, Title = "Second", Artist = "X" }));
    }

    [Fact]
    public void Delta_ReadsAsAChangeChip()
    {
        Assert.Equal(("▲ 50%", true, false), StatisticsViewModel.FormatDelta(150, 100));
        Assert.Equal(("▼ 6%", false, true), StatisticsViewModel.FormatDelta(102, 109));
        Assert.Equal(("Same", false, false), StatisticsViewModel.FormatDelta(7, 7));
        Assert.Equal(("New", true, false), StatisticsViewModel.FormatDelta(3, 0));
        Assert.Equal(("▲ <1%", true, false), StatisticsViewModel.FormatDelta(1001, 1000));
    }

    [Fact]
    public void ListeningTime_ReadsInHours()
    {
        Assert.Equal("348h 10m", StatisticsViewModel.FormatListening(TimeSpan.FromDays(14) + TimeSpan.FromHours(12) + TimeSpan.FromMinutes(10)));
        Assert.Equal("42 min", StatisticsViewModel.FormatListening(TimeSpan.FromMinutes(42)));
        Assert.Equal("0 min", StatisticsViewModel.FormatListening(TimeSpan.Zero));
        Assert.Equal("95h 37m", StatisticsViewModel.FormatListening(TimeSpan.FromMinutes(5736.6))); // the Wrap's 5,737 min
    }
}
