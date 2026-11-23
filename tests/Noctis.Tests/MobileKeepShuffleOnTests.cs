using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-23 (testers: MAJOR), "shuffle turns itself off when I play something else": the phone's
/// Settings › Listening › Keep Shuffle On, the desktop's KeepShuffleOn. With it on and Shuffle on, a
/// tapped song plays first and the rest of its list follows shuffled, and a list's Play button plays it
/// shuffled; Shuffle stays on. Off (the default), a new list plays in order and turns Shuffle off.
/// </summary>
public class MobileKeepShuffleOnTests
{
    private static Track[] Songs(int n, string prefix = "S") =>
        Enumerable.Range(0, n).Select(i => MobileFixtures.Song($"{prefix}{i}", prefix == "O" ? "Other" : "Band")).ToArray();

    // The current song and Up Next, the whole queue as played.
    private static List<Track> Queue(NowPlayingViewModel player) =>
        new[] { player.CurrentTrack! }.Concat(player.UpNext).ToList();

    private static void ShuffleOn(NowPlayingViewModel player, IReadOnlyList<Track> other)
    {
        player.PlayShuffled(other, new Random(3));
        Assert.True(player.IsShuffleEnabled);
    }

    private static void AssertSameSongs(IEnumerable<Track> expected, IEnumerable<Track> actual) =>
        Assert.Equal(expected.Select(t => t.Id).OrderBy(i => i), actual.Select(t => t.Id).OrderBy(i => i));

    [AvaloniaTheory]
    [InlineData("album")]
    [InlineData("songs")]
    [InlineData("list")]
    [InlineData("search")]
    [InlineData("artist")]
    public void RowTap_WithKeepShuffleOn_PlaysTheTappedSongFirst_AndShufflesTheWholeRest(string surface)
    {
        var songs = Songs(8);
        var other = Songs(2, "O");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", songs);
        using var rig = MobileFixtures.MakeRig(songs.Concat(other).ToArray(), new[] { album });
        var shell = rig.Shell;
        shell.KeepShuffleOn = true;
        ShuffleOn(shell.Player, other);

        var (list, tapped) = Tap(shell, album, surface, 3);

        Assert.Same(tapped, shell.Player.CurrentTrack);
        Assert.True(shell.Player.IsShuffleEnabled);
        // The songs before the tapped one are queued too, not only the ones after it.
        AssertSameSongs(list.Where(t => !ReferenceEquals(t, tapped)), shell.Player.UpNext);
    }

    [AvaloniaFact]
    public void RowTap_WithKeepShuffleOff_PlaysInOrder_AndTurnsShuffleOff()
    {
        var songs = Songs(6);
        var other = Songs(2, "O");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", songs);
        using var rig = MobileFixtures.MakeRig(songs.Concat(other).ToArray(), new[] { album });
        var shell = rig.Shell;
        Assert.False(shell.KeepShuffleOn);   // the default
        ShuffleOn(shell.Player, other);

        var page = new AlbumPageViewModel(shell, album);
        page.PlayTrackCommand.Execute(page.Tracks[2]);

        Assert.Same(songs[2], shell.Player.CurrentTrack);
        Assert.False(shell.Player.IsShuffleEnabled);
        Assert.Equal(songs.Skip(3), shell.Player.UpNext);
    }

    [AvaloniaFact]
    public void RowTap_WithKeepShuffleOn_ButShuffleOff_PlaysInOrder()
    {
        var songs = Songs(6);
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", songs);
        using var rig = MobileFixtures.MakeRig(songs, new[] { album });
        var shell = rig.Shell;
        shell.KeepShuffleOn = true;

        var page = new AlbumPageViewModel(shell, album);
        page.PlayTrackCommand.Execute(page.Tracks[2]);

        Assert.Same(songs[2], shell.Player.CurrentTrack);
        Assert.False(shell.Player.IsShuffleEnabled);
        Assert.Equal(songs.Skip(3), shell.Player.UpNext);
    }

    [AvaloniaTheory]
    [InlineData("album")]
    [InlineData("artist")]
    [InlineData("artistCard")]
    [InlineData("list")]
    [InlineData("featured")]
    public void PlayButton_WithKeepShuffleOn_PlaysTheListShuffled_AndShuffleStaysOn(string surface)
    {
        var songs = Songs(8);
        var other = Songs(2, "O");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", songs);
        using var rig = MobileFixtures.MakeRig(songs.Concat(other).ToArray(), new[] { album });
        var shell = rig.Shell;
        shell.KeepShuffleOn = true;
        ShuffleOn(shell.Player, other);

        PressPlay(rig, album, songs, surface);

        Assert.True(shell.Player.IsShuffleEnabled);
        AssertSameSongs(songs, Queue(shell.Player));
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PlayButton_OtherwiseStartsAtTheFirstSongInOrder(bool keepShuffleOn, bool shuffleOn)
    {
        var songs = Songs(5);
        var other = Songs(2, "O");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", songs);
        using var rig = MobileFixtures.MakeRig(songs.Concat(other).ToArray(), new[] { album });
        var shell = rig.Shell;
        shell.KeepShuffleOn = keepShuffleOn;
        if (shuffleOn) ShuffleOn(shell.Player, other);

        new AlbumPageViewModel(shell, album).PlayCommand.Execute(null);

        Assert.Same(songs[0], shell.Player.CurrentTrack);
        Assert.False(shell.Player.IsShuffleEnabled);
        Assert.Equal(songs.Skip(1), shell.Player.UpNext);
    }

    [Fact]
    public async Task TheSetting_IsSaved_AndReadBack_UnderTheDesktopsKey()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1), seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            Assert.False(s.KeepShuffleOn);   // the default
            s.KeepShuffleOn = true;
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.True(rig.Shell.KeepShuffleOn);
        Assert.True(rig.Shell.Player.KeepShuffleOn);

        rig.Shell.KeepShuffleOn = false;
        Assert.False(rig.Shell.Player.KeepShuffleOn);
        await rig.Shell.PendingPlayerBarSave;
        Assert.False((await rig.Persistence.LoadSettingsAsync()).KeepShuffleOn);
    }

    [AvaloniaFact]
    public async Task TheSettingsSwitch_IsUnderListening_AndTurnsItOn()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1));
        var shell = rig.Shell;
        var window = MobileFixtures.Mount(shell, out _);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
            await vm.Loaded;
            MobileFixtures.Land(window);
            var page = MobileFixtures.PageView((ShellView)window.Content!, vm);
            var toggle = MobileFixtures.Named<ToggleButton>(page, "KeepShuffleSwitch");
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Equal("Keep Shuffle On", Avalonia.Automation.AutomationProperties.GetName(toggle));
            Assert.False(toggle.IsChecked);

            toggle.IsChecked = true;
            Assert.True(shell.KeepShuffleOn);
            Assert.True(shell.Player.KeepShuffleOn);
        }
        finally
        {
            window.Close();
        }
    }

    // A song row tapped on <paramref name="surface"/>: the list it plays and the song.
    private static (List<Track> List, Track Tapped) Tap(ShellViewModel shell, Album album, string surface, int index)
    {
        switch (surface)
        {
            case "album":
                var page = new AlbumPageViewModel(shell, album);
                page.PlayTrackCommand.Execute(page.Tracks[index]);
                return (album.Tracks.ToList(), album.Tracks[index]);
            case "songs":
                var all = shell.Library.Songs.ToList();
                shell.PlaySongCommand.Execute(all[index]);
                return (all, all[index]);
            case "list":
                var songs = album.Tracks.ToList();
                new SongListPageViewModel(shell, "Road Trip", () => songs).PlayCommand.Execute(songs[index]);
                return (songs, songs[index]);
            case "search":
                shell.Search.Query = "S";
                var found = shell.Search.Songs.ToList();
                shell.Search.PlaySongCommand.Execute(found[index]);
                return (found, found[index]);
            default:
                var artist = new ArtistPageViewModel(shell, "Band");
                var top = artist.TopSongs.Select(s => s.Track).ToList();
                artist.PlayTopSongCommand.Execute(artist.TopSongs[Math.Min(index, top.Count - 1)]);
                return (top, top[Math.Min(index, top.Count - 1)]);
        }
    }

    // A list's Play button on <paramref name="surface"/>, playing <paramref name="songs"/>.
    private static void PressPlay(MobileFixtures.Rig rig, Album album, Track[] songs, string surface)
    {
        var shell = rig.Shell;
        switch (surface)
        {
            case "album":
                new AlbumPageViewModel(shell, album).PlayCommand.Execute(null);
                break;
            case "artist":
                new ArtistPageViewModel(shell, "Band").PlayCommand.Execute(null);
                break;
            case "artistCard":
                var artist = new ArtistPageViewModel(shell, "Band");
                artist.PlayCardCommand.Execute(artist.Cards.First(c => c.Album == album));
                break;
            case "list":
                new SongListPageViewModel(shell, "Road Trip", () => songs).PlayAllCommand.Execute(null);
                break;
            default:
                rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Band", TrackCount = songs.Length });
                shell.Search.Query = "Band";
                Assert.NotNull(shell.Search.Featured);
                shell.Search.PlayFeaturedCommand.Execute(null);
                break;
        }
    }
}
