using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta 2 feedback: "When I sort my songs in any way, if I go out of the Songs page and come
/// back, it's reset to the default order." Each list now keeps the sort it was left in, across
/// leaving and coming back and across a restart (settings.json, MobileListSorts).
/// </summary>
public class MobileListSortMemoryTests
{
    private static Track[] Songs() => new[]
    {
        MobileFixtures.Song("Bravo", plays: 1),
        MobileFixtures.Song("Alpha", plays: 9),
        MobileFixtures.Song("Charlie", plays: 5),
    };

    private static SongListPageViewModel OpenSongs(MobileFixtures.Rig rig)
    {
        rig.Shell.OpenSongsCommand.Execute(null);
        return Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
    }

    private static void Choose(SongListPageViewModel page, SongSort sort) =>
        page.ChooseSort(page.SortChoices.Single(c => (SongSort)c.Key == sort));

    [Fact]
    public async Task Songs_KeepTheirSort_AfterLeavingAndComingBack_AndAfterARestart()
    {
        var songs = Songs();
        using var rig = MobileFixtures.MakeRig(songs);
        await rig.Shell.InitializeAsync();
        var page = OpenSongs(rig);
        Choose(page, SongSort.Title);
        page.ChooseDirection(true);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, page.Songs.Select(t => t.Title));

        rig.Shell.GoBack();
        var again = OpenSongs(rig);
        Assert.NotSame(page, again);
        Assert.Equal(SongSort.Title, again.Sort);
        Assert.True(again.IsDescending);
        Assert.True(again.SortChoices.Single(c => (SongSort)c.Key == SongSort.Title).IsSelected);
        Assert.False(again.SortChoices.Single(c => (SongSort)c.Key == SongSort.Default).IsSelected);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, again.Songs.Select(t => t.Title));

        await rig.Shell.PendingListSortSave;
        var saved = (await rig.Persistence.LoadSettingsAsync()).MobileListSorts;
        Assert.Equal("Title desc", saved["Nav.Songs"]);

        // A restart: a new shell over the same settings.
        using var restarted = MobileFixtures.MakeRig(songs, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileListSorts = new Dictionary<string, string>(saved);
            await p.SaveSettingsAsync(s);
        });
        await restarted.Shell.InitializeAsync();
        var cold = OpenSongs(restarted);
        Assert.Equal(SongSort.Title, cold.Sort);
        Assert.True(cold.IsDescending);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, cold.Songs.Select(t => t.Title));
    }

    [Fact]
    public async Task BackToTheOwnOrder_ClearsTheSavedSort()
    {
        using var rig = MobileFixtures.MakeRig(Songs());
        await rig.Shell.InitializeAsync();
        var page = OpenSongs(rig);
        Choose(page, SongSort.MostPlayed);
        Choose(page, SongSort.Default);
        await rig.Shell.PendingListSortSave;
        Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileListSorts.ContainsKey("Nav.Songs"));
        rig.Shell.GoBack();
        Assert.False(OpenSongs(rig).IsSorted);
    }

    [Fact]
    public async Task ListsKeepTheirOwnSorts_AndAnArtistsTopSongsAreNotRemembered()
    {
        using var rig = MobileFixtures.MakeRig(Songs());
        await rig.Shell.InitializeAsync();
        Choose(OpenSongs(rig), SongSort.Title);
        rig.Shell.GoBack();
        rig.Shell.OpenRecentlyAddedCommand.Execute(null);
        var recent = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.False(recent.IsSorted);   // Songs' sort is Songs' only
        var unkeyed = new SongListPageViewModel(rig.Shell, "Top", () => rig.Library.TrackList);
        Choose(unkeyed, SongSort.Title);
        await rig.Shell.PendingListSortSave;
        Assert.Equal(new[] { "Nav.Songs" }, (await rig.Persistence.LoadSettingsAsync()).MobileListSorts.Keys);
    }

    [Fact]
    public async Task TheFavoritesTab_MadeBeforeTheSettingsAreRead_OpensInTheSavedSort_AndFollowsAFavoritesPage()
    {
        var songs = Songs();
        foreach (var t in songs) t.IsFavorite = true;
        using var rig = MobileFixtures.MakeRig(songs, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileListSorts["Nav.Favorites"] = "Title";
            await p.SaveSettingsAsync(s);
        });
        var root = rig.Shell.FavoritesRoot;   // the shell view binds it before InitializeAsync runs
        Assert.False(root.IsSorted);
        await rig.Shell.InitializeAsync();
        Assert.Equal(SongSort.Title, root.Sort);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, root.Songs.Select(t => t.Title));

        rig.Shell.OpenFavouritesCommand.Execute(null);
        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(SongSort.Title, page.Sort);
        Choose(page, SongSort.MostPlayed);
        Assert.Equal(SongSort.MostPlayed, root.Sort);
        Assert.Equal(new[] { "Alpha", "Charlie", "Bravo" }, root.Songs.Select(t => t.Title));
    }

    [Fact]
    public async Task APlaylist_KeepsItsOwnSort_AndThePlaylistsListKeepsItsToo()
    {
        var songs = Songs();
        var one = new Playlist { Name = "One", TrackIds = songs.Select(t => t.Id).ToList() };
        var two = new Playlist { Name = "Two", TrackIds = songs.Select(t => t.Id).ToList(), ModifiedAt = DateTime.UtcNow.AddDays(1) };
        using var rig = MobileFixtures.MakeRig(songs, seed: p => p.SavePlaylistsAsync(new List<Playlist> { one, two }));
        await rig.Shell.InitializeAsync();

        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single(p => p.Name == "One"));
        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Choose(page, SongSort.Title);
        rig.Shell.GoBack();
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single(p => p.Name == "One"));
        Assert.Equal(SongSort.Title, Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage).Sort);
        rig.Shell.GoBack();
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single(p => p.Name == "Two"));
        Assert.Equal(SongSort.Default, Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage).Sort);
        rig.Shell.GoBack();

        rig.Shell.OpenPlaylistsCommand.Execute(null);
        var list = Assert.IsType<PlaylistListPageViewModel>(rig.Shell.CurrentPage);
        list.ChooseSort(list.SortChoices.Single(c => (PlaylistSort)c.Key == PlaylistSort.RecentlyUpdated));
        rig.Shell.GoBack();
        rig.Shell.OpenPlaylistsCommand.Execute(null);
        var back = Assert.IsType<PlaylistListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(PlaylistSort.RecentlyUpdated, back.Sort);
        Assert.Equal(new[] { "Two", "One" }, back.Playlists.Select(p => p.Name));
        Assert.Equal(PlaylistSort.RecentlyUpdated, rig.Shell.PlaylistsRoot.Sort);   // the tab's list agrees
        await rig.Shell.PendingListSortSave;
        Assert.Equal("RecentlyUpdated", (await rig.Persistence.LoadSettingsAsync()).MobileListSorts[PlaylistListPageViewModel.SortKey]);
    }
}
