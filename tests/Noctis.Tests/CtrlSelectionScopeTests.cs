using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit S07: a right-click keeps the Ctrl-selection, and the views push it to the
/// ViewModel whether or not the clicked row/tile is part of it. Remove from Library,
/// Remove from Playlist, Favorite, Add to Playlist, Edit Info, Convert and ReplayGain
/// used "selection if any, else the clicked item", so they hit the selected items and
/// left the clicked one alone. They now follow Rate/Lyrics/Send to Folder: the whole
/// selection only when the clicked item is in it, otherwise just the clicked item.
/// Favorite stands in for the dialog-backed commands, which share the same rule.
/// </summary>
public class CtrlSelectionScopeTests
{
    private sealed class NoOpPlayHistoryService : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track Trk(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Artist",
        FilePath = TestPaths.Primary("sel", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static Album Alb(string name) => new() { Id = Guid.NewGuid(), Name = name, Artist = "Artist", Tracks = new List<Track> { Trk(name + " 1"), Trk(name + " 2") } };

    private static bool Fav(Album album) => album.Tracks.All(t => t.IsFavorite);

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (PlayerViewModel player, SidebarViewModel sidebar, TestPersistenceService persistence) Shell(FakeLibraryService lib)
    {
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return (player, new SidebarViewModel(persistence, lib), persistence);
    }

    // ── Songs ──

    [AvaloniaFact]
    public async Task Songs_ActionOnRowOutsideSelection_HitsOnlyThatRow()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, persistence) = Shell(lib);
        var vm = new LibrarySongsViewModel(lib, player, sidebar, persistence);
        var (a, b, c) = (Trk("A"), Trk("B"), Trk("C"));
        vm.CtrlSelectedTracks = new List<Track> { a, b };

        await vm.ToggleFavoriteCommand.ExecuteAsync(c);

        Assert.True(c.IsFavorite);
        Assert.False(a.IsFavorite);
        Assert.False(b.IsFavorite);
    }

    [AvaloniaFact]
    public async Task Songs_ActionOnRowInsideSelection_HitsWholeSelection()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, persistence) = Shell(lib);
        var vm = new LibrarySongsViewModel(lib, player, sidebar, persistence);
        var (a, b, c) = (Trk("A"), Trk("B"), Trk("C"));
        vm.CtrlSelectedTracks = new List<Track> { a, b };

        await vm.ToggleFavoriteCommand.ExecuteAsync(a);

        Assert.True(a.IsFavorite);
        Assert.True(b.IsFavorite);
        Assert.False(c.IsFavorite);
    }

    // ── Playlist ──

    private static async Task<(PlaylistViewModel vm, Playlist playlist, List<Track> tracks)> PlaylistOf(params string[] titles)
    {
        var tracks = titles.Select(Trk).ToList();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var (player, sidebar, persistence) = Shell(lib);
        var playlist = new Playlist { Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        await PumpUntil(() => vm.Tracks.Count == tracks.Count);
        Assert.Equal(tracks.Count, vm.Tracks.Count);
        return (vm, playlist, tracks);
    }

    [AvaloniaFact]
    public async Task Playlist_RemoveOnRowOutsideSelection_RemovesOnlyThatRow()
    {
        var (vm, playlist, t) = await PlaylistOf("A", "B", "C");
        vm.CtrlSelectedTracks = new List<Track> { t[0], t[1] };

        await vm.RemoveTrackCommand.ExecuteAsync(t[2]);

        Assert.Equal(new[] { t[0].Id, t[1].Id }, playlist.TrackIds);
    }

    [AvaloniaFact]
    public async Task Playlist_RemoveOnRowInsideSelection_RemovesWholeSelection()
    {
        // The floating selection bar passes the first selected row, so this is its path too.
        var (vm, playlist, t) = await PlaylistOf("A", "B", "C");
        vm.CtrlSelectedTracks = new List<Track> { t[0], t[1] };

        await vm.RemoveTrackCommand.ExecuteAsync(t[0]);

        Assert.Equal(new[] { t[2].Id }, playlist.TrackIds);
    }

    // ── Albums grid, Home, Favorites ──

    [AvaloniaFact]
    public async Task Albums_ActionOnTileOutsideSelection_HitsOnlyThatAlbum()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, persistence) = Shell(lib);
        var vm = new LibraryAlbumsViewModel(lib, player, sidebar, new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
        var (x, y, z) = (Alb("X"), Alb("Y"), Alb("Z"));
        vm.CtrlSelectedAlbums = new List<Album> { x, y };

        await vm.ToggleAlbumFavoritesCommand.ExecuteAsync(z);

        Assert.True(Fav(z));
        Assert.DoesNotContain(x.Tracks, t => t.IsFavorite);
        Assert.DoesNotContain(y.Tracks, t => t.IsFavorite);

        vm.CtrlSelectedAlbums = new List<Album> { x, y };
        await vm.ToggleAlbumFavoritesCommand.ExecuteAsync(x);

        Assert.True(Fav(x));
        Assert.True(Fav(y));
    }

    [AvaloniaFact]
    public async Task Home_ActionOnTileOutsideSelection_HitsOnlyThatAlbum()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, _) = Shell(lib);
        var vm = new HomeViewModel(player, lib, sidebar);
        var (x, y, z) = (Alb("X"), Alb("Y"), Alb("Z"));
        vm.CtrlSelectedAlbums = new List<Album> { x, y };

        await vm.ToggleAlbumFavoritesCommand.ExecuteAsync(z);

        Assert.True(Fav(z));
        Assert.DoesNotContain(x.Tracks, t => t.IsFavorite);
        Assert.DoesNotContain(y.Tracks, t => t.IsFavorite);
    }

    [AvaloniaFact]
    public async Task Favorites_ActionOnTileOutsideSelection_HitsOnlyThatItem()
    {
        var lib = new FakeLibraryService();
        var (a, b, c) = (Trk("A"), Trk("B"), Trk("C"));
        foreach (var t in new[] { a, b, c }) t.IsFavorite = true;
        lib.TrackList.AddRange(new[] { a, b, c });
        var (player, sidebar, persistence) = Shell(lib);
        var vm = new FavoritesViewModel(player, lib, persistence, sidebar, new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
        var (ia, ib, ic) = (new FavoriteItem { Track = a }, new FavoriteItem { Track = b }, new FavoriteItem { Track = c });
        vm.CtrlSelectedItems = new List<FavoriteItem> { ia, ib };

        await vm.RemoveItemFavoriteCommand.ExecuteAsync(ic);

        Assert.False(c.IsFavorite);
        Assert.True(a.IsFavorite);
        Assert.True(b.IsFavorite);
    }
}
