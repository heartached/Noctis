using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Home › Most Played (beta 4, Discord #mobile-beta: "Most played section is missing"): the songs
/// of all time by Track.PlayCount, the number Sort by Most Played uses, which the phone's own
/// counted plays now raise (NowPlayingViewModel.CountPlay). Before, only the computer's sync did,
/// so a phone without a computer had no counts at all.
/// </summary>
public class MobileMostPlayedTests
{
    private static Track Played(string title, int plays, int hoursAgo = 1)
    {
        var t = MobileFixtures.Song(title);
        t.PlayCount = plays;
        t.LastPlayed = DateTime.UtcNow.AddHours(-hoursAgo);
        return t;
    }

    private static void Listen(MobileFixtures.Rig rig, int seconds)
    {
        for (var s = 1; s <= seconds; s++) rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(s));
    }

    /// <summary>A shell signed in to a computer, over the rig's library and player.</summary>
    private static (ShellViewModel Shell, MobileAccountTests.FakeAccount Account) SignedIn(MobileFixtures.Rig rig)
    {
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };
        return (shell, account);
    }

    [Fact]
    public void Rail_ShowsOnceThreeSongsHaveAPlay_MostPlayedFirst()
    {
        var a = Played("A", 5);
        var b = Played("B", 2);
        var c = Played("C", 0);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        var library = rig.Shell.Library;
        Assert.False(library.HasMostPlayed);   // two songs played: not a chart yet
        Assert.Empty(library.MostPlayedRail);

        c.PlayCount = 1;
        rig.Library.RaiseLibraryUpdated();

        Assert.True(library.HasMostPlayed);
        Assert.Equal(new[] { "A", "B", "C" }, library.MostPlayedRail.Select(r => r.Title));
        Assert.All(library.MostPlayedRail, r => Assert.Equal(RailItemKind.Track, r.Kind));
    }

    [Fact]
    public void Rail_TiesGoToTheMoreRecentPlay_AndItHoldsTwelve()
    {
        var songs = Enumerable.Range(1, 14).Select(i => Played("S" + i, 3, hoursAgo: i)).ToArray();
        songs[13].PlayCount = 9;
        using var rig = MobileFixtures.MakeRig(songs);

        var titles = rig.Shell.Library.MostPlayedRail.Select(r => r.Title).ToList();
        Assert.Equal(12, titles.Count);
        Assert.Equal(new[] { "S14", "S1", "S2" }, titles.Take(3));
    }

    [AvaloniaFact]
    public async Task ACountedPlay_RaisesThePlayCount_AndTheRailFollows_NoAccount()
    {
        var a = Played("A", 2);
        var b = Played("B", 1);
        var c = MobileFixtures.Song("C");   // 90 s, never played
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        await rig.Shell.LoadHistoryPrefsAsync();   // Count a Play After: half the song
        Assert.False(rig.Shell.Library.HasMostPlayed);

        rig.Shell.Player.PlayTracks(new[] { c }, 0);
        Listen(rig, 44);
        Assert.Equal(0, c.PlayCount);              // not counted yet: neither is the log
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(45));

        Assert.Equal(1, c.PlayCount);
        Assert.NotNull(c.LastPlayed);
        Assert.True(rig.Shell.Library.HasMostPlayed);
        Assert.Equal(new[] { "A", "C", "B" }, rig.Shell.Library.MostPlayedRail.Select(r => r.Title));   // C ties B, played later

        // Again: the same number the rail and Sort by Most Played read.
        rig.Shell.Player.PlayTracks(new[] { c }, 0);
        Listen(rig, 45);
        rig.Shell.Player.PlayTracks(new[] { c }, 0);
        Listen(rig, 45);
        Assert.Equal(3, c.PlayCount);
        Assert.Equal("C", rig.Shell.Library.MostPlayedRail[0].Title);
    }

    /// <summary>The queue can hold an older copy of a song (a rescan re-read its file): the play
    /// is counted on the library's song, which the rail and the journal read.</summary>
    [Fact]
    public void ACountedPlay_LandsOnTheLibrarysSong_WhenTheQueueHoldsAnOlderCopy()
    {
        var song = MobileFixtures.Song("Song");
        using var rig = MobileFixtures.MakeRig(new[] { song });
        var queued = new Track { Id = song.Id, Title = song.Title, Artist = song.Artist, Album = song.Album, FilePath = song.FilePath, Duration = song.Duration };

        rig.Shell.Player.PlayTracks(new[] { queued }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));

        Assert.Equal(1, song.PlayCount);
        Assert.Equal(1, queued.PlayCount);
    }

    /// <summary>Signed in, Share with Computer on: the desktop song's play is counted here at once
    /// and queued for the computer (which counts it there; the sync keeps the larger count, see
    /// NoctisAccountServiceTests.APhoneCountedPlay_IsNotCountedTwice_ByTheSync). Off: counted here
    /// only.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SignedIn_TheDesktopSongsPlay_IsCountedOnce_AndSharedOnlyWhenOn(bool share)
    {
        var song = MobileFixtures.Song("Desk");
        song.FilePath = "noctis-remote://studio/desk";
        using var rig = MobileFixtures.MakeRig(new[] { song });
        var (shell, account) = SignedIn(rig);
        shell.SetShareHistoryWithComputer(share);

        shell.Player.PlayTracks(new[] { song }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));

        Assert.Equal(1, song.PlayCount);
        Assert.Equal(share ? 1 : 0, account.Plays.Count);
    }

    /// <summary>A sync that brings the computer's higher counts re-reads the rail.</summary>
    [Fact]
    public async Task SyncDone_RereadsTheCounts()
    {
        var a = Played("A", 1);
        var b = Played("B", 2);
        var c = Played("C", 3);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c });
        var (shell, account) = SignedIn(rig);
        Assert.Equal("C", shell.Library.MostPlayedRail[0].Title);

        a.PlayCount = 10;   // the sync's ledger raised it (counts only grow)
        await account.SyncNowAsync();

        Assert.Equal("A", shell.Library.MostPlayedRail[0].Title);
    }

    [AvaloniaFact]
    public void Header_OpensEveryPlayedSong_SortedByMostPlayed()
    {
        var a = Played("A", 2);
        var b = Played("B", 7);
        var c = Played("C", 4);
        var d = MobileFixtures.Song("D");
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c, d });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(MobileFixtures.Named<StackPanel>(view, "MostPlayedSection").IsVisible);

        MobileFixtures.Named<Button>(view, "MostPlayedHeader").Command!.Execute(null);
        window.UpdateLayout();

        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Most Played", page.Title);
        Assert.Equal(SongSort.MostPlayed, page.Sort);
        Assert.True(page.IsDescending);
        Assert.Equal(new[] { "B", "C", "A" }, page.Songs.Select(t => t.Title));
        window.Close();
    }

    /// <summary>A tile plays the Most Played rail from it, though the song is on On Repeat too.</summary>
    [Fact]
    public void Tile_PlaysTheMostPlayedRail_FromIt()
    {
        var a = Played("A", 9);
        var c = Played("C", 5);
        var b = Played("B", 1);
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, log: h =>
        {
            foreach (var d in new[] { 1, 2, 3 }) h.Seed(c, DateTime.UtcNow.AddDays(-d));
        });
        Assert.Contains(rig.Shell.Library.OnRepeatRail, r => ReferenceEquals(r.Payload, c));

        var tile = rig.Shell.Library.MostPlayedRail.Single(r => ReferenceEquals(r.Payload, c));
        rig.Shell.OpenRailItemCommand.Execute(tile);

        Assert.Same(c, rig.Shell.Player.CurrentTrack);
        // The rail (A, C, B) from C: B next. On Repeat's rail (C alone) has nothing after it.
        Assert.Equal(new[] { "B" }, rig.Shell.Player.UpNext.Select(t => t.Title));
    }
}
