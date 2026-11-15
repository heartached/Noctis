using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #208: a song menu's Search Lyrics on a song that isn't playing loaded that song's
/// lyrics into the lyrics page, which follows the playing song. It now opens the Search Lyrics
/// dialog for the clicked song and leaves the page alone.
/// </summary>
public class SearchLyricsClickedSong1109Tests
{
    private sealed class NoLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class NoNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    [AvaloniaFact]
    public async Task OtherSong_OpensTheSearchDialogForThatSong()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var playing = new Track { Title = "Playing", Artist = "A" };
        var clicked = new Track { Title = "Clicked", Artist = "B" };
        player.CurrentTrack = playing;
        LyricsSearchViewModel? shown = null;
        var vm = new LyricsViewModel(player, new NoLrcLib(), new NoNetEase(), new MetadataService(), new TestPersistenceService(), new FakeLibraryService())
        {
            ShowLyricsSearchDialog = s => { shown = s; return Task.CompletedTask; },
        };

        Assert.True(vm.IsPlayerTrack(playing));
        Assert.False(vm.IsPlayerTrack(clicked));

        await vm.OpenLyricsSearchForTrackAsync(clicked);

        Assert.NotNull(shown);
        Assert.Equal("Clicked", shown!.Title);
        Assert.Equal("B", shown.Artist);
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

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed record MenuRig(PlayerViewModel Player, FakeLibraryService Lib, TestPersistenceService Persistence,
        LyricsViewModel Lyrics, Track Playing, Track Other, List<LyricsSearchViewModel> Dialogs, List<int> PageOpens);

    /// <summary>The player on a song of one album; another artist's album open in the library.</summary>
    private static MenuRig MountMenus()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var playing = new Track { Title = "Playing", Artist = "A", Album = "Queue Album", AlbumArtist = "A", AlbumId = Guid.NewGuid() };
        var other = new Track { Title = "Clicked", Artist = "B", Album = "Random Album", AlbumArtist = "B", AlbumId = Guid.NewGuid() };
        lib.TrackList.AddRange([playing, other]);
        player.CurrentTrack = playing;
        var dialogs = new List<LyricsSearchViewModel>();
        var lyrics = new LyricsViewModel(player, new NoLrcLib(), new NoNetEase(), new MetadataService(), persistence, new FakeLibraryService())
        {
            ShowLyricsSearchDialog = s => { dialogs.Add(s); return Task.CompletedTask; },
        };
        return new MenuRig(player, lib, persistence, lyrics, playing, other, dialogs, []);
    }

    // MainWindowViewModel wires every song menu's Search Lyrics to this call.
    private static Action<Track> Route(MenuRig m) => t => m.Lyrics.SearchLyricsFromSongMenu(t, () => m.PageOpens.Add(1));

    private static Album AlbumOf(Track t) => new()
    {
        Id = t.AlbumId, Name = t.Album, Artist = t.AlbumArtist, TrackCount = 1, Tracks = [t],
    };

    [AvaloniaFact]
    public void AlbumPage_OtherAlbumsSong_OpensItsDialog_NotThePlayingSongsLyricsPage()
    {
        var m = MountMenus();
        var page = new AlbumDetailViewModel(AlbumOf(m.Other), m.Player, m.Persistence, m.Lib,
            new SidebarViewModel(m.Persistence, m.Lib), new FakeLastFm());
        page.SetSearchLyricsAction(Route(m));

        page.SearchLyricsCommand.Execute(m.Other);

        Assert.Empty(m.PageOpens);
        Assert.Equal("Clicked", Assert.Single(m.Dialogs).Title);
    }

    [AvaloniaFact]
    public void ArtistPage_OtherAlbumsSong_OpensItsDialog_NotThePlayingSongsLyricsPage()
    {
        var m = MountMenus();
        var page = new ArtistDetailViewModel("B", m.Lib, m.Player);
        var route = Route(m);
        page.SearchLyricsRequested += (_, t) => route(t);

        page.SearchLyricsCommand.Execute(m.Other);

        Assert.Empty(m.PageOpens);
        Assert.Equal("Clicked", Assert.Single(m.Dialogs).Title);
    }

    [AvaloniaFact]
    public void AlbumsPage_OtherAlbumsSong_OpensItsDialog_NotThePlayingSongsLyricsPage()
    {
        var m = MountMenus();
        var page = new LibraryAlbumsViewModel(m.Lib, m.Player, new SidebarViewModel(m.Persistence, m.Lib),
            new SettingsViewModel(m.Persistence, m.Lib, new NoOpPlayHistory()));
        page.SetSearchLyricsAction(Route(m));

        page.SearchLyricsTrackCommand.Execute(m.Other);

        Assert.Empty(m.PageOpens);
        Assert.Equal("Clicked", Assert.Single(m.Dialogs).Title);
    }

    [AvaloniaFact]
    public void PlayingSong_OpensTheLyricsPage()
    {
        var m = MountMenus();
        var page = new AlbumDetailViewModel(AlbumOf(m.Playing), m.Player, m.Persistence, m.Lib,
            new SidebarViewModel(m.Persistence, m.Lib), new FakeLastFm());
        page.SetSearchLyricsAction(Route(m));

        page.SearchLyricsCommand.Execute(m.Playing);

        Assert.Single(m.PageOpens);
    }
}
