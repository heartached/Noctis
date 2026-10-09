using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Statistics page's numbers disagreed with each other (owner screenshot 10-09: "60 Total
/// Plays" beside "102 Plays This Week", Top Artists "6ix9ine 18 plays" while the Wrap for the
/// same year said Taylor Swift 2513, "1 plays", "avg length 3:29", "MP3 0%").
///
/// Cause, from a copy of the owner's dev profile: the library had been re-added from another
/// folder on 10-06, so every track had a new id and PlayCount 0, while play_history.json kept
/// 8,470 events under the old ids (60 still matched). Total Plays and Top Artists/Albums summed
/// Track.PlayCount (60); every other tile read the log. Each test here failed on HEAD 3eb84d44
/// with the old API (same data, same assertion) and passes on the revamp.
/// </summary>
public class StatisticsNumbersTests
{
    // HEAD: "Total Plays 0 < Plays This Week 5".
    [AvaloniaFact]
    public void Plays_AllTimeIsNeverBelowTheLast7Days_BothComeFromTheLog()
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Title = "Love Story", Artist = "Taylor Swift", Album = "Fearless", PlayCount = 0 });
        var oldId = Guid.NewGuid(); // the id the song had before the folder was re-added
        var log = Enumerable.Range(0, 5).Select(i => Play(oldId, "Love Story", "Taylor Swift", DateTime.UtcNow.AddHours(-i - 1))).ToArray();
        var vm = new StatisticsViewModel(library, new History(log));

        vm.Period = ListeningPeriod.Last7Days;
        Refresh(vm);
        var week = int.Parse(vm.PlaysText);
        vm.Period = ListeningPeriod.AllTime;
        Refresh(vm);
        var allTime = int.Parse(vm.PlaysText);

        Assert.Equal(5, week);
        Assert.Equal(5, allTime);
    }

    // HEAD: Expected "Taylor Swift", Actual "6ix9ine" (ranked by Track.PlayCount).
    [AvaloniaFact]
    public void TopArtists_FollowThePlayLog_NotTrackPlayCount()
    {
        var library = new FakeLibraryService();
        var swift = new Track { Title = "Love Story", Artist = "Taylor Swift", Album = "Fearless" };
        var tekashi = new Track { Title = "Gummo", Artist = "6ix9ine", Album = "Day69", PlayCount = 3 };
        library.TrackList.AddRange(new[] { swift, tekashi });
        var oldId = Guid.NewGuid();
        var log = Enumerable.Range(0, 10).Select(i => Play(oldId, "Love Story", "Taylor Swift", DateTime.UtcNow.AddHours(-i - 1)))
            .Concat(Enumerable.Range(0, 3).Select(i => Play(tekashi.Id, "Gummo", "6ix9ine", DateTime.UtcNow.AddHours(-i - 1).AddMinutes(-30))))
            .ToArray();
        var vm = new StatisticsViewModel(library, new History(log));

        Refresh(vm);

        Assert.Equal(new[] { "Taylor Swift", "6ix9ine" }, vm.TopArtists.Select(a => a.Name));
        Assert.Equal("10 plays", vm.TopArtists[0].PlaysText);
    }

    // HEAD: Expected "1 play", Actual "1 plays".
    [AvaloniaFact]
    public void OnePlay_IsSingular()
    {
        var library = new FakeLibraryService();
        var t = new Track { Title = "Gummo", Artist = "6ix9ine", Album = "Day69", PlayCount = 1 };
        library.TrackList.Add(t);
        var vm = new StatisticsViewModel(library, new History(new[] { Play(t.Id, "Gummo", "6ix9ine", DateTime.UtcNow.AddHours(-1)) }));

        Refresh(vm);

        Assert.Equal("1 play", vm.TopArtists[0].PlaysText);
        Assert.Equal("1 play", vm.TopTracks[0].PlaysText);
        Assert.Contains(vm.HourBars, b => b.Tooltip.EndsWith("· 1 play", StringComparison.Ordinal));
    }

    // HEAD: Expected 6000000000 (10 min), Actual 2100000000 (the 3.5-minute fallback).
    [Fact]
    public void ListeningTime_ResolvesAReAddedSongByTitleAndArtist()
    {
        var song = new Track { Title = "Love Story", Artist = "Taylor Swift", Duration = TimeSpan.FromMinutes(10) };
        var byId = new Dictionary<Guid, Track> { [song.Id] = song };
        var stats = ListeningStatsCalculator.Compute(
            new[] { Play(Guid.NewGuid(), "Love Story", "Taylor Swift", DateTime.UtcNow.AddHours(-1)) }, byId);

        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, stats.TimeListenedTicks);
    }

    // HEAD: Expected "4:00", Actual "3:30" (the average logged play, all on the fallback).
    [AvaloniaFact]
    public void LibraryAvgLength_IsTheLibraryAverage()
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Title = "A", Artist = "X", Duration = TimeSpan.FromMinutes(2) });
        library.TrackList.Add(new Track { Title = "B", Artist = "X", Duration = TimeSpan.FromMinutes(6) });
        var gone = Play(Guid.NewGuid(), "Removed", "Nobody", DateTime.UtcNow.AddHours(-1));
        var vm = new StatisticsViewModel(library, new History(new[] { gone }));

        Refresh(vm);

        Assert.Equal("4:00", vm.AvgTrackLength);
    }

    // HEAD StatisticsViewModel.cs:323 formatted 1 of 4,353 with "0.#" — "0%" for a format
    // that is there ("MP3 0%" in the owner's screenshot).
    [Fact]
    public void FormatShare_UnderATenthOfAPercent_IsNotShownAsZero()
    {
        Assert.Equal("<0.1%", StatisticsViewModel.Percent(1, 4353));
        Assert.Equal("0%", StatisticsViewModel.Percent(0, 4353));
        Assert.Equal("97.2%", StatisticsViewModel.Percent(4231, 4353));
        Assert.Equal("100%", StatisticsViewModel.Percent(5, 5));
    }

    // The owner's numbers, rebuilt from the same shape: 8,410 plays under ids the library no
    // longer has, 60 under current ids. Every tile now counts the same 8,470.
    [Fact]
    public void ReAddedLibrary_EveryFigureCountsTheWholeLog()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        var current = new Track { Title = "Gummo", Artist = "6ix9ine", Album = "Day69", Duration = TimeSpan.FromMinutes(2.6) };
        var tracks = new[] { current, new Track { Title = "Love Story", Artist = "Taylor Swift", Album = "Fearless", Duration = TimeSpan.FromMinutes(4) } };
        var events = new List<PlayHistoryEvent>();
        for (var i = 0; i < 8410; i++)
            events.Add(Play(Guid.NewGuid(), "Love Story", "Taylor Swift", now.AddMinutes(-10 * (8470 - i)).ToUniversalTime()));
        for (var i = 0; i < 60; i++)
            events.Add(Play(current.Id, "Gummo", "6ix9ine", now.AddMinutes(-10 * (60 - i)).ToUniversalTime()));

        var report = ListeningReportBuilder.Build(events, new PlayEventResolver(tracks), ListeningPeriod.AllTime, now);

        Assert.Equal(8470, report.Plays);
        Assert.Equal("Taylor Swift", report.TopArtists[0].Name);
        Assert.Equal(8410, report.TopArtists[0].Plays);
        Assert.Equal(TimeSpan.FromMinutes(4 * 8410 + 2.6 * 60).Ticks, report.ListenedTicks, TimeSpan.FromSeconds(1).Ticks);
    }

    /// <summary>
    /// The page and the Wrap count the same history the same way: plays whose id is gone are
    /// placed by title + artist, plays cut off within 30 s count nowhere, and time stops where
    /// the next play starts (WrapStatsBuilder's own helpers).
    /// </summary>
    [AvaloniaFact]
    public void StatisticsPage_AndWrap_GiveTheSameTotals_ForTheSameHistory()
    {
        var swift = new Track { Title = "Love Story", Artist = "Taylor Swift", Album = "Fearless", AlbumArtist = "Taylor Swift", Duration = TimeSpan.FromMinutes(3) };
        var juice = new Track { Title = "Lucid Dreams", Artist = "Juice WRLD", Album = "GBGR", AlbumArtist = "Juice WRLD", Duration = TimeSpan.FromMinutes(4) };
        var gracie = new Track { Title = "us.", Artist = "Gracie Abrams feat. Taylor Swift", Album = "The Secret of Us", AlbumArtist = "Gracie Abrams", Duration = TimeSpan.FromMinutes(2) };
        foreach (var t in new[] { swift, juice, gracie }) t.AlbumId = Guid.NewGuid();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(new[] { swift, juice, gracie });
        foreach (var t in library.TrackList)
            ((List<Album>)library.Albums).Add(new Album { Id = t.AlbumId, Name = t.Album, Artist = t.AlbumArtist });

        var now = DateTime.Now;
        var at = now.AddMinutes(-400) < new DateTime(now.Year, 1, 1) ? new DateTime(now.Year, 1, 1) : now.AddMinutes(-400);
        var oldSwiftId = Guid.NewGuid(); // logged before the folder was re-added
        var events = new List<PlayHistoryEvent>();
        void Log(Guid id, Track t, bool skipped = false, int gapSeconds = 180)
        {
            events.Add(Play(id, t.Title, t.Artist, at.ToUniversalTime(), skipped));
            at = at.AddSeconds(gapSeconds);
        }
        for (var i = 0; i < 6; i++) Log(oldSwiftId, swift);
        for (var i = 0; i < 20; i++) Log(juice.Id, juice, gapSeconds: i == 19 ? 180 : 1); // a burst: only the last counts
        for (var i = 0; i < 4; i++) Log(juice.Id, juice, skipped: i == 0);
        Log(Guid.NewGuid(), new Track { Title = "Gone", Artist = "Ghost" }); // left the library
        for (var i = 0; i < 3; i++) Log(gracie.Id, gracie);
        Log(swift.Id, swift);

        var vm = new StatisticsViewModel(library, new History(events));
        vm.Period = ListeningPeriod.ThisYear;
        Refresh(vm);
        var wrap = WrapStatsBuilder.Build(events, library.TrackList.ToDictionary(t => t.Id), now.Year, null, now);

        Assert.Equal(16, wrap.TotalPlays); // 6 + last of the burst + 4 + 1 + 3 + 1
        Assert.Equal(wrap.TotalPlays.ToString("N0"), vm.PlaysText);
        Assert.Equal(StatisticsViewModel.FormatListening(TimeSpan.FromMinutes(wrap.TotalMinutes)), vm.ListeningTimeText);
        Assert.Equal(wrap.UniqueArtists.ToString("N0"), vm.UniqueArtistsText);
        Assert.Equal($"{wrap.UniqueTracks} different songs", vm.UniqueTracksText);
        Assert.Equal(wrap.TopArtists.Select(a => (a.Name, a.PlaysLabel)), vm.TopArtists.Take(wrap.TopArtists.Count).Select(a => (a.Name, a.PlaysText)));
        Assert.Equal(wrap.TopAlbums.Select(a => (a.Name, a.PlaysLabel)), vm.TopAlbums.Take(wrap.TopAlbums.Count).Select(a => (a.Title, a.PlaysText)));
        Assert.Equal(wrap.TopTracks.Select(a => (a.Name, a.PlaysLabel)), vm.TopTracks.Take(wrap.TopTracks.Count).Select(a => (a.Title, a.PlaysText)));
        Assert.Equal(wrap.PlaysByHour, vm.HourBars.Select(b => b.Count));
    }

    internal static void Refresh(StatisticsViewModel vm)
    {
        var task = vm.RefreshAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted, "refresh never finished");
    }

    internal static PlayHistoryEvent Play(Guid id, string title, string artist, DateTime utc, bool skipped = false) => new()
    {
        TrackId = id, Title = title, Artist = artist, PlayedAtUtc = utc, Skipped = skipped,
    };

    internal sealed class History : IPlayHistoryService
    {
        public History(IReadOnlyList<PlayHistoryEvent> events) => Events = events;
        public IReadOnlyList<PlayHistoryEvent> Events { get; }
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
