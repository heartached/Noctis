using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The other lists ranked by plays (follow-up to Songs, Discord Andre 10-28): the artist
/// page's Top Songs and the Albums page's artist Songs section kept their old order after a
/// counted play, since a count raises no LibraryUpdated. They re-rank once the song is over.
/// Also: a count on a pre-reload queue copy of a track must reach the library's instance.
/// </summary>
public class PlaysReRankListsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noctis-plays-rerank-{Guid.NewGuid():N}");

    public PlaysReRankListsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Track Trk(string title, int plays, Album album)
    {
        var path = Path.Combine(_dir, $"{title}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track
        {
            Id = Guid.NewGuid(), Title = title, Artist = "Artist", AlbumArtist = "Artist",
            Album = album.Name, AlbumId = album.Id, FilePath = path,
            Duration = TimeSpan.FromMinutes(3), PlayCount = plays,
        };
    }

    private (FakeLibraryService lib, PlayerViewModel player, TestPersistenceService persistence, Track a, Track b, Track c) Setup()
    {
        var lib = new FakeLibraryService();
        var album = new Album { Id = Guid.NewGuid(), Name = "Record", Artist = "Artist", Year = 2020 };
        var a = Trk("a", 10, album);
        var b = Trk("b", 10, album);
        var c = Trk("c", 0, album);
        album.Tracks = new List<Track> { a, b, c };
        lib.TrackList.AddRange(album.Tracks);
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return (lib, player, persistence, a, b, c);
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ArtistPage_TopSongs_ReRankWhenTheSongIsOver()
    {
        var (lib, player, _, _, b, c) = Setup();
        var vm = new ArtistDetailViewModel("Artist", lib, player);
        string Order() => string.Concat(vm.PopularSongs.Select(r => r.Track.Title));
        Assert.Equal("abc", Order());

        player.ReplaceQueueAndPlay(new[] { b, c }, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(11, b.PlayCount);
        Assert.Equal("abc", Order()); // the playing row stays put

        player.NextCommand.Execute(null);
        await PumpUntil(() => Order() == "bac");
        Assert.Equal("bac", Order());
        Assert.Equal(new[] { 1, 2, 3 }, vm.PopularSongs.Select(r => r.Rank));
    }

    [AvaloniaFact]
    public async Task AlbumsPage_ArtistSongsSection_ReRanksWhenTheSongIsOver()
    {
        var (lib, player, persistence, _, b, c) = Setup();
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new FakeHistoryLog()));
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 0);
        vm.SetArtistFilter("Artist");
        string Order() => string.Concat(vm.FilteredAlbumRows.OfType<ArtistSongsRow>()
            .SelectMany(r => r.Songs).Select(s => s.Track.Title));
        Assert.Equal("abc", Order());

        player.ReplaceQueueAndPlay(new[] { b, c }, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("abc", Order());

        player.NextCommand.Execute(null);
        await PumpUntil(() => Order() == "bac");
        Assert.Equal("bac", Order());
    }

    // A rescan of a changed file builds a new Track (LibraryService.CopyMutableTrackState)
    // while the queue keeps the old one: the play was counted on that copy only, and the
    // user-state journal (which writes the library's instance) dropped it.
    [AvaloniaFact]
    public void CountedPlay_OnAPreReloadQueueCopy_ReachesTheLibraryTrack()
    {
        var (lib, player, _, a, _, c) = Setup();
        player.ReplaceQueueAndPlay(new[] { c, a }, 0);

        var reloaded = new Track
        {
            Id = a.Id, Title = a.Title, Artist = a.Artist, FilePath = a.FilePath,
            Duration = a.Duration, PlayCount = a.PlayCount,
        };
        lib.TrackList[lib.TrackList.IndexOf(a)] = reloaded;

        player.NextCommand.Execute(null); // plays the queue's old copy
        Dispatcher.UIThread.RunJobs();

        Assert.Same(a, player.CurrentTrack);
        Assert.Equal(11, reloaded.PlayCount);
        Assert.NotNull(reloaded.LastPlayed);
    }
}
