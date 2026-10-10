using System.Windows.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Snooze for a Month" with a Ctrl multi-selection (owner 10-10): it snoozed only the clicked
/// album or row. It now follows the neighbouring menu actions (SelectionOr): the whole selection
/// when the clicked item is part of it, otherwise just the clicked item, written in ONE
/// SetTracksSnoozedAsync call with each track once.
/// </summary>
public class SnoozeMultiSelectTests
{
    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static Track Trk(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Artist",
        FilePath = TestPaths.Primary("snooze-sel", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static Album Alb(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, Artist = "Artist", Tracks = new List<Track> { Trk(name + " 1"), Trk(name + " 2") } };

    private static (PlayerViewModel Player, SidebarViewModel Sidebar, TestPersistenceService Persistence) Shell(FakeLibraryService lib)
    {
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return (player, new SidebarViewModel(persistence, lib), persistence);
    }

    private static List<Guid> Ids(IEnumerable<Track> tracks) => tracks.Select(t => t.Id).OrderBy(id => id).ToList();

    /// <summary>Runs the command and returns the single snooze write it made (asserting there was exactly one).</summary>
    private static List<Track> SnoozedBy(FakeLibraryService lib, ICommand command, object parameter)
    {
        lib.SnoozeCalls.Clear();
        command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
        var call = Assert.Single(lib.SnoozeCalls);
        Assert.NotNull(call.Until);
        Assert.Equal(call.Tracks.Count, call.Tracks.Select(t => t.Id).Distinct().Count());
        return call.Tracks;
    }

    // ── Album tiles ──

    [AvaloniaFact]
    public void Albums_Snooze_HonorsSelection()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, persistence) = Shell(lib);
        var vm = new LibraryAlbumsViewModel(lib, player, sidebar, new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
        var (x, y, z) = (Alb("X"), Alb("Y"), Alb("Z"));

        vm.CtrlSelectedAlbums = new List<Album> { x, y };
        Assert.Equal(Ids(x.Tracks.Concat(y.Tracks)), Ids(SnoozedBy(lib, vm.SnoozeAlbumForMonthCommand, x)));

        vm.CtrlSelectedAlbums = new List<Album> { x, y };
        Assert.Equal(Ids(z.Tracks), Ids(SnoozedBy(lib, vm.SnoozeAlbumForMonthCommand, z)));
    }

    [AvaloniaFact]
    public void Home_AlbumSnooze_HonorsSelection()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, _) = Shell(lib);
        var vm = new HomeViewModel(player, lib, sidebar);
        var (x, y, z) = (Alb("X"), Alb("Y"), Alb("Z"));

        vm.CtrlSelectedAlbums = new List<Album> { x, y };
        Assert.Equal(Ids(x.Tracks.Concat(y.Tracks)), Ids(SnoozedBy(lib, vm.SnoozeAlbumForMonthCommand, y)));

        vm.CtrlSelectedAlbums = new List<Album> { x, y };
        Assert.Equal(Ids(z.Tracks), Ids(SnoozedBy(lib, vm.SnoozeAlbumForMonthCommand, z)));
    }

    [AvaloniaFact]
    public void Favorites_AlbumSnooze_HonorsSelection_AndDropsDuplicateTracks()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, persistence) = Shell(lib);
        var vm = new FavoritesViewModel(player, lib, persistence, sidebar, new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
        var (x, z) = (Alb("X"), Alb("Z"));
        var loose = Trk("Loose");
        // A favourite song from album X selected next to X itself: its track is snoozed once.
        var (ix, iSong, iLoose, iz) = (new FavoriteItem { Album = x }, new FavoriteItem { Track = x.Tracks[0] },
            new FavoriteItem { Track = loose }, new FavoriteItem { Album = z });

        vm.CtrlSelectedItems = new List<FavoriteItem> { ix, iSong, iLoose };
        Assert.Equal(Ids(x.Tracks.Append(loose)), Ids(SnoozedBy(lib, vm.SnoozeAlbumForMonthCommand, x)));

        vm.CtrlSelectedItems = new List<FavoriteItem> { ix, iLoose };
        Assert.Equal(Ids(z.Tracks), Ids(SnoozedBy(lib, vm.SnoozeAlbumForMonthCommand, iz.Album!)));
    }

    // ── Track rows ──

    [AvaloniaFact]
    public void Songs_TrackSnooze_HonorsSelection()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, persistence) = Shell(lib);
        var vm = new LibrarySongsViewModel(lib, player, sidebar, persistence);
        var (a, b, c) = (Trk("A"), Trk("B"), Trk("C"));

        vm.CtrlSelectedTracks = new List<Track> { a, b, a };
        Assert.Equal(Ids(new[] { a, b }), Ids(SnoozedBy(lib, vm.SnoozeForMonthCommand, a)));

        vm.CtrlSelectedTracks = new List<Track> { a, b };
        Assert.Equal(Ids(new[] { c }), Ids(SnoozedBy(lib, vm.SnoozeForMonthCommand, c)));
    }

    [AvaloniaFact]
    public void AlbumPage_TrackSnooze_HonorsSelection()
    {
        var lib = new FakeLibraryService();
        var (player, sidebar, persistence) = Shell(lib);
        var (a, b, c) = (Trk("A"), Trk("B"), Trk("C"));
        var album = new Album { Id = Guid.NewGuid(), Name = "Al", Artist = "Artist", Tracks = new List<Track> { a, b, c } };
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, sidebar, new FakeLastFm());
        Dispatcher.UIThread.RunJobs();

        vm.CtrlSelectedTracks = new List<Track> { b, c };
        Assert.Equal(Ids(new[] { b, c }), Ids(SnoozedBy(lib, vm.SnoozeForMonthCommand, c)));

        vm.CtrlSelectedTracks = new List<Track> { b, c };
        Assert.Equal(Ids(new[] { a }), Ids(SnoozedBy(lib, vm.SnoozeForMonthCommand, a)));
    }

    [AvaloniaFact]
    public async Task Playlist_TrackSnooze_HonorsSelection()
    {
        var lib = new FakeLibraryService();
        var tracks = new[] { Trk("A"), Trk("B"), Trk("C") };
        lib.TrackList.AddRange(tracks);
        var (player, sidebar, persistence) = Shell(lib);
        var playlist = new Playlist { Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        var deadline = Environment.TickCount64 + 5000;
        while (vm.Tracks.Count < tracks.Length && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.Equal(tracks.Length, vm.Tracks.Count);

        vm.CtrlSelectedTracks = new List<Track> { tracks[0], tracks[2] };
        Assert.Equal(Ids(new[] { tracks[0], tracks[2] }), Ids(SnoozedBy(lib, vm.SnoozeForMonthCommand, tracks[2])));

        vm.CtrlSelectedTracks = new List<Track> { tracks[0], tracks[2] };
        Assert.Equal(Ids(new[] { tracks[1] }), Ids(SnoozedBy(lib, vm.SnoozeForMonthCommand, tracks[1])));
    }
}
