using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-10: snoozed songs still showed on Home. Snooze means "hide the song from
/// suggestions for a month" (Strings.resx Menu.Snooze), so Home's suggestion rows (time of
/// day, Heavy rotation, Rediscovered) skip a snoozed song and take the next candidate,
/// while the history rows (Most Played, Last Played) keep reporting it.
/// </summary>
public class HomeSnoozedSuggestionsTests
{
    private sealed class FakePlayHistory : IPlayHistoryService
    {
        public List<PlayHistoryEvent> Log { get; } = new();
        public IReadOnlyList<PlayHistoryEvent> Events => Log;
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
        public void Add(Track t, DateTime local) =>
            Log.Add(new PlayHistoryEvent { TrackId = t.Id, Title = t.Title, PlayedAtUtc = local.ToUniversalTime() });
    }

    private static Track T(string title, int playCount = 0) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "A", AlbumArtist = "A", Album = "X",
        AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(180), FilePath = "C:/m/" + title + ".mp3",
        PlayCount = playCount,
    };

    private sealed record Setup(HomeViewModel Vm, FakeLibraryService Lib, Track Heavy, Track Time, Track Rediscovered, Track Top,
        Track HeavySpare, Track TimeSpare);

    /// <summary>
    /// Seven heavy-rotation candidates (row takes six), seven time-of-day candidates, five
    /// rediscovered ones; the first of each ranks highest. <c>top</c> is a Most Played song
    /// and the heavy leader is the newest play (so it heads Last Played).
    /// </summary>
    private static Setup Build(DateTime? snoozedUntil)
    {
        var lib = new FakeLibraryService();
        var log = new FakePlayHistory();
        var now = DateTime.Now;

        var heavy = Enumerable.Range(0, 7).Select(i => T("h" + i)).ToList();
        var time = Enumerable.Range(0, 7).Select(i => T("d" + i)).ToList();
        var redisc = Enumerable.Range(0, 5).Select(i => T("r" + i)).ToList();
        var top = T("top", playCount: 99);
        lib.TrackList.AddRange(heavy.Concat(time).Concat(redisc).Append(top));

        // Time of day: 20 days ago at this hour (outside Heavy rotation's two weeks).
        for (var i = 0; i < time.Count; i++)
            for (var n = 0; n < 2 + (time.Count - i); n++) log.Add(time[i], now.AddDays(-20).AddMinutes(-n));
        // Rediscovered: a play 100 days back, then one 3 days ago (gap ordering by index).
        for (var i = 0; i < redisc.Count; i++)
        {
            log.Add(redisc[i], now.AddDays(-100 - i * 5));
            log.Add(redisc[i], now.AddDays(-3));
        }
        // Heavy rotation: 3+ plays in the last two weeks; h0 has the most and plays last.
        for (var i = heavy.Count - 1; i >= 0; i--)
            for (var n = 0; n < 3 + (heavy.Count - i); n++) log.Add(heavy[i], now.AddDays(-1).AddMinutes(n));

        heavy[0].SnoozedUntil = snoozedUntil;
        time[0].SnoozedUntil = snoozedUntil;
        redisc[0].SnoozedUntil = snoozedUntil;
        top.SnoozedUntil = snoozedUntil;

        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), playHistory: log);
        return new Setup(vm, lib, heavy[0], time[0], redisc[0], top, heavy[6], time[6]);
    }

    [AvaloniaFact]
    public async Task SnoozedSongs_LeaveTheSuggestionRows_ButStayInTheHistoryRows()
    {
        var s = Build(DateTime.UtcNow.AddDays(30));
        await s.Vm.RefreshAsync();

        Assert.DoesNotContain(s.Heavy, s.Vm.HeavyRotationTracks);
        Assert.DoesNotContain(s.Time, s.Vm.TimeRotationTracks);
        Assert.DoesNotContain(s.Rediscovered, s.Vm.RediscoveredTracks);
        Assert.DoesNotContain(s.Vm.HeavyRotationTracks.Concat(s.Vm.TimeRotationTracks).Concat(s.Vm.RediscoveredTracks),
            t => t.IsSnoozed);
        // No hole: the next candidate fills the slot.
        Assert.Equal(6, s.Vm.HeavyRotationTracks.Count);
        Assert.Contains(s.HeavySpare, s.Vm.HeavyRotationTracks);
        Assert.Equal(6, s.Vm.TimeRotationTracks.Count);
        Assert.Contains(s.TimeSpare, s.Vm.TimeRotationTracks);
        Assert.Equal(4, s.Vm.RediscoveredTracks.Count);

        // History rows report what happened, snoozed or not.
        Assert.Contains(s.Top, s.Vm.TopSongs);
        Assert.Same(s.Heavy, s.Vm.LastPlayed.First());
    }

    [AvaloniaFact]
    public async Task ExpiredSnooze_ShowsAgain()
    {
        var s = Build(DateTime.UtcNow.AddDays(-1));
        await s.Vm.RefreshAsync();

        Assert.Same(s.Heavy, s.Vm.HeavyRotationTracks.First());
        Assert.Same(s.Time, s.Vm.TimeRotationTracks.First());
        Assert.Contains(s.Rediscovered, s.Vm.RediscoveredTracks);
        Assert.DoesNotContain(s.HeavySpare, s.Vm.HeavyRotationTracks);
    }

    [AvaloniaFact]
    public async Task SnoozeFromHomeMenu_DropsTheSongFromTheLoadedRows()
    {
        var s = Build(null);
        await s.Vm.RefreshAsync();
        Assert.Contains(s.Heavy, s.Vm.HeavyRotationTracks);

        await s.Vm.SnoozeForMonthCommand.ExecuteAsync(s.Heavy);

        Assert.True(s.Heavy.IsSnoozed);
        Assert.DoesNotContain(s.Heavy, s.Vm.HeavyRotationTracks);
        Assert.Contains(s.HeavySpare, s.Vm.HeavyRotationTracks);
        Assert.Same(s.Heavy, s.Vm.LastPlayed.First());
    }

    /// <summary>Snoozing an album from Home's album menu drops its songs from the loaded rows too
    /// (album snooze, 10-10, used to skip the refresh the song snooze does).</summary>
    [AvaloniaFact]
    public async Task SnoozeAlbumFromHomeMenu_DropsItsSongsFromTheLoadedRows()
    {
        var s = Build(null);
        await s.Vm.RefreshAsync();
        Assert.Contains(s.Heavy, s.Vm.HeavyRotationTracks);
        var album = new Album { Id = s.Heavy.AlbumId, Name = "X", Artist = "A", Tracks = new() { s.Heavy } };

        await s.Vm.SnoozeAlbumForMonthCommand.ExecuteAsync(album);

        Assert.True(s.Heavy.IsSnoozed);
        Assert.DoesNotContain(s.Heavy, s.Vm.HeavyRotationTracks);
        Assert.Contains(s.HeavySpare, s.Vm.HeavyRotationTracks);
    }

    [AvaloniaFact]
    public async Task PlaylistSuggestions_SkipSnoozedSongs()
    {
        var lib = new FakeLibraryService();
        var inList = T("in list");
        var snoozed = T("snoozed");
        snoozed.SnoozedUntil = DateTime.UtcNow.AddDays(30);
        var free = T("free");
        lib.TrackList.AddRange(new[] { inList, snoozed, free });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "p", TrackIds = new() { inList.Id } };
        sidebar.Playlists.Add(playlist);

        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        for (var i = 0; i < 100 && vm.SuggestedTracks.Count == 0; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(new[] { free }, vm.SuggestedTracks);
    }
}
