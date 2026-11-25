using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #264: a multi-disc album's disc header plays or shuffles just that disc. Only the
/// chosen disc's songs are queued (untagged disc 0 counts as disc 1, as the headers group
/// them), Shuffle goes through the album Shuffle button's path (the bar's shuffle lights
/// up), and "Playing from" is still the album.
/// </summary>
public class AlbumDiscPlay264Tests
{
    private static Track Song(int disc, int number) => new()
    {
        Id = Guid.NewGuid(),
        Title = $"D{disc} Song {number}",
        Artist = "A",
        Album = "Album",
        TrackNumber = number,
        DiscNumber = disc,
        FilePath = TestPaths.Primary("Music", "Album", $"{disc}-{number:00}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static (PlayerViewModel Player, AlbumDetailViewModel Page, Album Album) Open(IEnumerable<Track> songs)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", Artist = "A", Tracks = songs.ToList() };
        var page = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib),
            new RebuildKeepsScroll223Tests.NoLastFm());
        Dispatcher.UIThread.RunJobs();
        return (player, page, album);
    }

    private static List<Guid> Queued(PlayerViewModel player)
    {
        var ids = new List<Guid> { player.CurrentTrack!.Id };
        ids.AddRange(player.UpNext.Select(t => t.Id));
        return ids;
    }

    // Disc 2 is listed first and out of order: the page must not rely on the file order.
    private static List<Track> ThreeDiscs() =>
        Enumerable.Range(1, 15).Select(n => Song(2, n))
            .Concat(Enumerable.Range(1, 15).Select(n => Song(1, n)))
            .Concat(Enumerable.Range(1, 15).Select(n => Song(3, n)))
            .Reverse()
            .ToList();

    [AvaloniaFact]
    public void ShuffleDisc_QueuesOnlyThatDisc_Shuffled_FromTheAlbum()
    {
        var songs = ThreeDiscs();
        var (player, page, album) = Open(songs);
        Assert.True(page.HasMultipleDiscs);
        var disc2 = page.DiscGroups.Single(g => g.DiscNumber == 2);

        page.ShuffleDiscCommand.Execute(disc2);

        var expected = songs.Where(t => t.DiscNumber == 2).Select(t => t.Id).ToList();
        var queued = Queued(player);
        Assert.Equal(expected.OrderBy(g => g), queued.OrderBy(g => g));
        Assert.True(player.IsShuffleEnabled);
        Assert.Equal(PlaySource.ForAlbum(album), player.PlaySource);
        // Shuffled, not disc order (1 in 15! by chance).
        var inOrder = songs.Where(t => t.DiscNumber == 2).OrderBy(t => t.TrackNumber).Select(t => t.Id).ToList();
        Assert.NotEqual(inOrder, queued);
    }

    [AvaloniaFact]
    public void PlayDisc_QueuesOnlyThatDisc_InTrackOrder()
    {
        var songs = ThreeDiscs();
        var (player, page, album) = Open(songs);

        page.PlayDiscCommand.Execute(page.DiscGroups.Single(g => g.DiscNumber == 3));

        var expected = songs.Where(t => t.DiscNumber == 3).OrderBy(t => t.TrackNumber).Select(t => t.Id).ToList();
        Assert.Equal(expected, Queued(player));
        Assert.False(player.IsShuffleEnabled);
        Assert.Equal(PlaySource.ForAlbum(album), player.PlaySource);
    }

    [AvaloniaFact]
    public void Disc1_IncludesUntaggedDiscZeroSongs_LikeItsHeader()
    {
        var songs = new List<Track> { Song(0, 3), Song(1, 1), Song(1, 2), Song(2, 1), Song(2, 2) };
        var (player, page, _) = Open(songs);
        var disc1 = page.DiscGroups.Single(g => g.DiscNumber == 1);
        Assert.Equal(3, disc1.Tracks.Count);

        page.PlayDiscCommand.Execute(disc1);

        Assert.Equal(new[] { songs[1].Id, songs[2].Id, songs[0].Id }, Queued(player));
    }

    [AvaloniaFact]
    public void DiscHeaderLabels_NameTheDisc()
    {
        var (_, page, _) = Open(ThreeDiscs());
        var disc2 = page.DiscGroups.Single(g => g.DiscNumber == 2);

        Assert.Equal("Disc 2", disc2.Header);
        Assert.Equal("Play Disc 2", disc2.PlayLabel);
        Assert.Equal("Shuffle Disc 2", disc2.ShuffleLabel);
    }

    [AvaloniaFact]
    public void SingleDiscAlbum_HasNoDiscHeader()
    {
        var (_, page, _) = Open(Enumerable.Range(1, 5).Select(n => Song(1, n)));
        Assert.False(page.HasMultipleDiscs);
    }
}
