using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>What the Queue sheet says about the queue: where it plays from (recorded by every
/// play surface, saved with the queue) and the current track's place in it.</summary>
public class MobileQueueSourceTests
{
    private static Track[] Songs(int n, string prefix = "S") =>
        Enumerable.Range(0, n).Select(i => MobileFixtures.Song($"{prefix}{i}")).ToArray();

    // ── Playing from ───────────────────────────────────────────────

    [Fact]
    public async Task SourceLabel_IsRecordedByPlayAndShuffle_SurvivesARestart_AndIsClearedByAnUnlabelledPlay()
    {
        var songs = Songs(3);
        using var rig = MobileFixtures.MakeRig(songs);
        var player = rig.Shell.Player;

        player.PlayTracks(songs, 0, "Abbey Road");
        Assert.Equal("Abbey Road", player.SourceLabel);
        Assert.Equal("Playing from Abbey Road", player.PlayingFromText);

        player.PlayShuffled(songs, new Random(1), "Road Trip");
        Assert.Equal("Road Trip", player.SourceLabel);
        await player.SaveStateAsync();

        var restored = new NowPlayingViewModel(new FakeAudioPlayer(), rig.Library, rig.Persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        Assert.Equal("Road Trip", restored.SourceLabel);
        Assert.True(restored.HasSourceLabel);

        player.PlayTracks(songs, 1);
        Assert.Null(player.SourceLabel);
        Assert.False(player.HasSourceLabel);
        Assert.Equal(string.Empty, player.PlayingFromText);
    }

    [AvaloniaFact]
    public void EveryPlaySurface_NamesWhatItPlaysFrom()
    {
        var a1 = MobileFixtures.Song("A1", "Band");
        var a2 = MobileFixtures.Song("A2", "Band");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", a1, a2);
        using var rig = MobileFixtures.MakeRig(new[] { a1, a2 }, new[] { album }, log: h =>
        {
            foreach (var d in new[] { 1, 2, 3 }) h.Seed(a1, DateTime.UtcNow.AddDays(-d));
        });
        var shell = rig.Shell;
        string? Source() => shell.Player.SourceLabel;

        shell.PlaySongCommand.Execute(a1);
        Assert.Equal(Loc.T("Nav.Songs"), Source());

        var albumPage = new AlbumPageViewModel(shell, album);
        albumPage.PlayCommand.Execute(null);
        Assert.Equal("Alpha", Source());
        shell.Player.PlayTracks(new[] { a1 }, 0);
        albumPage.ShuffleCommand.Execute(null);
        Assert.Equal("Alpha", Source());
        shell.Player.PlayTracks(new[] { a1 }, 0);
        albumPage.PlayTrackCommand.Execute(albumPage.Tracks[1]);
        Assert.Equal("Alpha", Source());

        var artist = new ArtistPageViewModel(shell, "Band");
        artist.PlayCommand.Execute(null);
        Assert.Equal("Band", Source());
        shell.Player.PlayTracks(new[] { a1 }, 0);
        artist.ShuffleCommand.Execute(null);
        Assert.Equal("Band", Source());
        shell.Player.PlayTracks(new[] { a1 }, 0);
        artist.PlayTopSongCommand.Execute(artist.TopSongs[0]);
        Assert.Equal("Band", Source());

        var list = new SongListPageViewModel(shell, "Road Trip", () => new[] { a1, a2 });
        list.PlayCommand.Execute(a2);
        Assert.Equal("Road Trip", Source());
        shell.Player.PlayTracks(new[] { a1 }, 0);
        list.PlayAllCommand.Execute(null);
        Assert.Equal("Road Trip", Source());
        shell.Player.PlayTracks(new[] { a1 }, 0);
        list.ShuffleAllCommand.Execute(null);
        Assert.Equal("Road Trip", Source());

        shell.Search.Query = "A";
        shell.Search.PlaySongCommand.Execute(shell.Search.Songs[0]);
        Assert.Equal("Search", Source());

        shell.OpenRailItemCommand.Execute(shell.Library.OnRepeatRail[0]);
        Assert.Equal("On Repeat", Source());
    }

    [Fact]
    public void ContextSheet_PlayNextWithNothingLoaded_PlaysFromTheAlbum()
    {
        var a1 = MobileFixtures.Song("A1");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", a1);
        using var rig = MobileFixtures.MakeRig(new[] { a1 }, new[] { album });

        ContextSheetViewModel.ForAlbum(rig.Shell, album).PlayNextCommand.Execute(null);

        Assert.Same(a1, rig.Shell.Player.CurrentTrack);
        Assert.Equal("Alpha", rig.Shell.Player.SourceLabel);
    }

    // ── Track N of M ───────────────────────────────────────────────

    [Fact]
    public void QueuePosition_CountsThisQueueOnly_AndStaysExactPastTheHistoryCap()
    {
        var old = MobileFixtures.Song("Old");
        var songs = Songs(60);
        using var rig = MobileFixtures.MakeRig(songs.Append(old).ToArray());
        var player = rig.Shell.Player;
        player.PlayTracks(new[] { old }, 0);

        player.PlayTracks(songs, 0);                // Old is in History, but not in this queue
        Assert.Equal("Track 1 of 60", player.QueuePositionText);

        for (var i = 0; i < 55; i++) player.NextCommand.Execute(null);
        Assert.Equal("Track 56 of 60", player.QueuePositionText);   // History stopped at 50

        player.PreviousCommand.Execute(null);
        Assert.Equal("Track 55 of 60", player.QueuePositionText);

        player.AddToQueue(old);                     // a track added later is part of the count
        Assert.Equal("Track 55 of 61", player.QueuePositionText);
        Assert.True(player.HasQueuePosition);
    }

    [Fact]
    public void QueuePosition_IsHidden_WhenNothingPlays_AtTheEnd_AndOncePreviousLeavesTheQueue()
    {
        var a = Songs(1, "A");
        var b = Songs(2, "B");
        using var rig = MobileFixtures.MakeRig(a.Concat(b).ToArray());
        var player = rig.Shell.Player;
        Assert.False(player.HasQueuePosition);

        player.PlayTracks(a, 0);
        player.PlayTracks(b, 0);
        Assert.Equal("Track 1 of 2", player.QueuePositionText);
        player.PreviousCommand.Execute(null);       // back into A's queue: B's count no longer applies
        Assert.Same(a[0], player.CurrentTrack);
        Assert.False(player.HasQueuePosition);

        player.PlayTracks(b, 1);
        Assert.Equal("Track 1 of 1", player.QueuePositionText);
        player.NextCommand.Execute(null);           // ran out: the last track stays loaded, unnumbered
        Assert.Same(b[1], player.CurrentTrack);
        Assert.False(player.HasQueuePosition);
    }

    [Fact]
    public async Task QueuePosition_SurvivesARestart_AndAQueueSavedBeforeItIsUnnumbered()
    {
        var songs = Songs(5);
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.Player.NextCommand.Execute(null);
        rig.Shell.Player.NextCommand.Execute(null);
        await rig.Shell.Player.SaveStateAsync();

        var restored = new NowPlayingViewModel(new FakeAudioPlayer(), rig.Library, rig.Persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        Assert.Equal("Track 3 of 5", restored.QueuePositionText);

        await rig.Persistence.SaveQueueStateAsync(new QueueState
        {
            CurrentTrackId = songs[2].Id,
            UpNextIds = new() { songs[3].Id, songs[4].Id },
            HistoryIds = new() { songs[1].Id, songs[0].Id },
        });
        var legacy = new NowPlayingViewModel(new FakeAudioPlayer(), rig.Library, rig.Persistence, marshal: a => a());
        await legacy.RestoreStateAsync();
        Assert.Same(songs[2], legacy.CurrentTrack);
        Assert.False(legacy.HasQueuePosition);
        Assert.Null(legacy.SourceLabel);
    }
}
