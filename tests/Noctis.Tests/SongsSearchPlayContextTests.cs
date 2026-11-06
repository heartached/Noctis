using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (HypnoTized, 10-24): Songs page, search "through the", 1 result, play it, and the
/// Queue said "Nothing up next": the row's queue was built from the search results only. A
/// song played out of a search now continues with the whole list (current sort, the
/// favorites/quality narrowing kept) after it, as Apple Music / Spotify do.
/// </summary>
public class SongsSearchPlayContextTests
{
    private static (LibrarySongsViewModel vm, PlayerViewModel player, FakeLibraryService lib) CreateVm()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var vm = new LibrarySongsViewModel(lib, player, sidebar, persistence);
        return (vm, player, lib);
    }

    private static Track Trk(string title, bool favorite = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Artist",
        FilePath = TestPaths.Primary("t", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
        IsFavorite = favorite,
    };

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

    private static async Task<LibrarySongsViewModel> Loaded(
        (LibrarySongsViewModel vm, PlayerViewModel player, FakeLibraryService lib) s, params Track[] tracks)
    {
        s.lib.TrackList.AddRange(tracks);
        s.vm.SortCommand.Execute("Title"); // Title ascending
        s.vm.IsActive = true;
        await PumpUntil(() => s.vm.FilteredTracks.Count == tracks.Length);
        return s.vm;
    }

    [AvaloniaFact]
    public async Task PlayingASearchResult_ContinuesWithTheFullListAfterIt()
    {
        var s = CreateVm();
        var vm = await Loaded(s, Trk("Delta"), Trk("Alpha"), Trk("Through The Fire"), Trk("Zulu"), Trk("Charlie"));

        vm.ApplyFilter("through the");
        await PumpUntil(() => vm.FilteredTracks.Count == 1);
        var hit = Assert.Single(vm.FilteredTracks);

        vm.PlayFromHereCommand.Execute(hit);

        Assert.Same(hit, s.player.CurrentTrack);
        Assert.Equal(new[] { "Zulu" }, s.player.UpNext.Select(t => t.Title));
    }

    [AvaloniaFact]
    public async Task NoSearch_KeepsPlayingTheShownList()
    {
        var s = CreateVm();
        var vm = await Loaded(s, Trk("Charlie"), Trk("Alpha"), Trk("Bravo"));

        vm.PlayFromHereCommand.Execute(vm.FilteredTracks[1]); // Bravo

        Assert.Equal("Bravo", s.player.CurrentTrack?.Title);
        Assert.Equal(new[] { "Charlie" }, s.player.UpNext.Select(t => t.Title));
    }

    [AvaloniaFact]
    public async Task SearchContext_KeepsTheFavoritesNarrowing()
    {
        var s = CreateVm();
        var vm = await Loaded(s, Trk("Alpha", favorite: true), Trk("Bravo"), Trk("Charlie", favorite: true), Trk("Delta", favorite: true));
        vm.SetShowOnlyFavoritesCommand.Execute(null);
        vm.ApplyFilter("alpha");
        await PumpUntil(() => vm.FilteredTracks.Count == 1);

        vm.PlayFromHereCommand.Execute(vm.FilteredTracks[0]);

        Assert.Equal("Alpha", s.player.CurrentTrack?.Title);
        Assert.Equal(new[] { "Charlie", "Delta" }, s.player.UpNext.Select(t => t.Title));
    }

    [AvaloniaFact]
    public async Task SearchPlay_WithShuffleOn_ShufflesTheFullList()
    {
        var s = CreateVm();
        var vm = await Loaded(s, Trk("Alpha"), Trk("Bravo"), Trk("Charlie"), Trk("Delta"), Trk("Through"));
        s.player.PlayShuffled(vm.FilteredTracks.ToList()); // Shuffle on
        Assert.True(s.player.IsShuffleEnabled);

        vm.ApplyFilter("through");
        await PumpUntil(() => vm.FilteredTracks.Count == 1);
        vm.PlayFromHereCommand.Execute(vm.FilteredTracks[0]);

        Assert.True(s.player.IsShuffleEnabled);
        Assert.Equal("Through", s.player.CurrentTrack?.Title);
        Assert.Equal(4, s.player.UpNext.Count);
        Assert.DoesNotContain(s.player.UpNext, t => t.Title == "Through");
    }

    [AvaloniaFact]
    public async Task Playlist_PlayingASearchResult_ContinuesWithThePlaylistAfterIt()
    {
        var s = CreateVm();
        var tracks = new[] { Trk("One"), Trk("Through The Fire"), Trk("Three"), Trk("Four") };
        s.lib.TrackList.AddRange(tracks);
        var sidebar = new SidebarViewModel(new TestPersistenceService(), s.lib);
        var playlist = new Playlist { Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, s.player, s.lib, new TestPersistenceService(), sidebar);
        await PumpUntil(() => vm.Tracks.Count == 4);

        vm.ApplyFilter("through the");
        await PumpUntil(() => vm.Tracks.Count == 1);
        vm.PlayFromCommand.Execute(vm.Tracks[0]);

        Assert.Equal("Through The Fire", s.player.CurrentTrack?.Title);
        Assert.Equal(new[] { "Three", "Four" }, s.player.UpNext.Select(t => t.Title));
    }

    /// <summary>Discord (DendyAly 11-04, seen live): searching "Cole" in a 50-song playlist
    /// turned the header into "9 tracks · 30 min"; "Lucid" (no match) into "0 tracks · 0 KB".</summary>
    [AvaloniaFact]
    public async Task Playlist_Search_KeepsTheHeaderOnTheWholePlaylist()
    {
        var s = CreateVm();
        var tracks = new[] { Trk("Fire One"), Trk("Fire Two"), Trk("Three"), Trk("Four") };
        foreach (var t in tracks) t.FileSize = 1024 * 1024;
        s.lib.TrackList.AddRange(tracks);
        var sidebar = new SidebarViewModel(new TestPersistenceService(), s.lib);
        var playlist = new Playlist { Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, s.player, s.lib, new TestPersistenceService(), sidebar);
        await PumpUntil(() => vm.Tracks.Count == 4);

        vm.ApplyFilter("fire");
        await PumpUntil(() => vm.Tracks.Count == 2);
        Assert.Equal(("4 tracks", "12 min", "4.0 MB"), (vm.TrackCountText, vm.TotalDuration, vm.TotalSize));

        vm.ApplyFilter("nothing like this");
        await PumpUntil(() => vm.Tracks.Count == 0);
        Assert.Equal(("4 tracks", "12 min"), (vm.TrackCountText, vm.TotalDuration));

        // Removing a row during a search still counts down from the whole playlist.
        vm.ApplyFilter("fire");
        await PumpUntil(() => vm.Tracks.Count == 2);
        vm.RemoveTrackCommand.Execute(vm.Tracks[0]);
        await PumpUntil(() => vm.TrackCountText == "3 tracks");
        Assert.Equal("3 tracks", vm.TrackCountText);
    }

    /// <summary>The rail's Featured Artists describe the whole playlist, like the header: removing
    /// a row during a search rebuilt them from the search results alone.</summary>
    [AvaloniaFact]
    public async Task Playlist_RemoveDuringASearch_KeepsTheFeaturedArtistsOnTheWholePlaylist()
    {
        var s = CreateVm();
        var tracks = new[] { Trk("Fire One"), Trk("Fire Two"), Trk("Three"), Trk("Four") };
        tracks[2].Artist = "Other";
        tracks[3].Artist = "Other";
        s.lib.TrackList.AddRange(tracks);
        var sidebar = new SidebarViewModel(new TestPersistenceService(), s.lib);
        var playlist = new Playlist { Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, s.player, s.lib, new TestPersistenceService(), sidebar);
        await PumpUntil(() => vm.Tracks.Count == 4);
        Assert.Equal(new[] { "Artist", "Other" }, vm.FeaturedArtists.Select(a => a.Name).OrderBy(n => n));

        vm.ApplyFilter("fire");
        await PumpUntil(() => vm.Tracks.Count == 2);
        await vm.RemoveTrackCommand.ExecuteAsync(vm.Tracks.Single(t => t.Title == "Fire Two"));

        Assert.Equal(new[] { "Artist", "Other" }, vm.FeaturedArtists.Select(a => a.Name).OrderBy(n => n));
        Assert.Equal(2, vm.FeaturedArtists.Single(a => a.Name == "Other").TrackCount);
    }

    [AvaloniaFact]
    public async Task Playlist_SongRemovedDuringASearch_IsNotQueuedByTheNextSearchPlay()
    {
        var s = CreateVm();
        var tracks = new[] { Trk("Fire One"), Trk("Fire Two"), Trk("Three") };
        s.lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var sidebar = new SidebarViewModel(persistence, s.lib);
        var playlist = new Playlist { Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, s.player, s.lib, persistence, sidebar);
        await PumpUntil(() => vm.Tracks.Count == 3);

        vm.ApplyFilter("fire");
        await PumpUntil(() => vm.Tracks.Count == 2);
        await vm.RemoveTrackCommand.ExecuteAsync(vm.Tracks.Single(t => t.Title == "Fire Two"));
        vm.PlayFromCommand.Execute(vm.Tracks.Single(t => t.Title == "Fire One"));

        Assert.Equal("Fire One", s.player.CurrentTrack?.Title);
        Assert.Equal(new[] { "Three" }, s.player.UpNext.Select(t => t.Title));
    }
}
