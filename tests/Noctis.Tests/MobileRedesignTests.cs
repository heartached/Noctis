using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone redesign batch: the song rows (no heart since 10-19), the sheet's quick-action tiles, Search's
/// scopes / featured artist / sort / playlists, the song lists' sort, filters and in-list search,
/// and the mini player's elapsed fill.
/// </summary>
public class MobileRedesignTests
{
    // ── Song rows ──

    /// <summary>Owner 10-19: no heart on a song row (Songs, Favorites, Recently Added, playlists,
    /// Search), only ⋯, whose menu favourites the song without playing it and says so after.</summary>
    [AvaloniaFact]
    public void SongRow_HasNoHeart_AndItsMenuFavouritesTheSong_WithoutPlayingIt()
    {
        var a = MobileFixtures.Song("Alpha");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        window.UpdateLayout();

        var row = MobileFixtures.Find<SongRow>(view);
        Assert.Equal(new[] { "MoreButton" }, row.GetVisualDescendants().OfType<Button>().Select(b => b.Name));
        var more = MobileFixtures.Named<Button>(row, "MoreButton");
        more.Command!.Execute(more.CommandParameter);
        Assert.Equal("Favorite", rig.Shell.Sheet!.FavouriteTileLabel);
        rig.Shell.Sheet.ToggleFavouriteCommand.Execute(null);
        window.UpdateLayout();

        Assert.True(a.IsFavorite);
        Assert.Equal(1, rig.Shell.Library.FavoriteCount);
        Assert.Null(rig.Shell.Player.CurrentTrack);
        more = MobileFixtures.Named<Button>(MobileFixtures.Find<SongRow>(view), "MoreButton");
        more.Command!.Execute(more.CommandParameter);
        Assert.Equal("Favorited", rig.Shell.Sheet!.FavouriteTileLabel);
        window.Close();
    }

    // ── The long-press sheet ──

    [AvaloniaFact]
    public void TrackSheet_ShowsTheQuickActionTiles_AndNoDownloadTileWithoutAnAccount()
    {
        var a = MobileFixtures.Song("Alpha");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenTrackSheetCommand.Execute(a);
        window.UpdateLayout();

        // The ⋯ menu's tiles (Apple's): Favorite, Add to Playlist, Play Next; Pin is a row.
        var tiles = MobileFixtures.Named<UniformGrid>(view, "Tiles");
        var shown = tiles.Children.Where(c => c.IsVisible).Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "MenuFavourite", "MenuAddToPlaylist", "MenuPlayNext" }, shown);
        Assert.False(MobileFixtures.Named<Control>(view, "MenuDownload").IsVisible);
        // Three tiles share the row evenly.
        var widths = tiles.Children.Where(c => c.IsVisible).Select(c => Math.Round(c.Bounds.Width)).Distinct();
        Assert.Single(widths);
        Assert.Equal("Favorite", rig.Shell.Sheet!.FavouriteTileLabel);
        Assert.Equal("Pin", rig.Shell.Sheet.PinTileLabel);
        window.Close();
    }

    [Fact]
    public async Task TrackSheet_TileLabels_FollowTheState()
    {
        var a = MobileFixtures.Song("Alpha", favourite: true);
        using var rig = MobileFixtures.MakeRig(new[] { a });
        await rig.Shell.Library.SetTrackPinnedAsync(a.Id, true);

        rig.Shell.OpenTrackSheetCommand.Execute(a);

        Assert.Equal("Favorited", rig.Shell.Sheet!.FavouriteTileLabel);
        Assert.Equal("Pinned", rig.Shell.Sheet.PinTileLabel);
        Assert.False(rig.Shell.Sheet.ShowDownloadTile);
    }

    // ── Search ──

    private static MobileFixtures.Rig SearchLibrary()
    {
        var songs = Enumerable.Range(0, 40).Select(i => MobileFixtures.Song($"Love {i:D2}", artist: "Band", plays: i)).ToArray();
        var album = MobileFixtures.MakeAlbum("Love Album", "Band", songs[0]);
        var rig = MobileFixtures.MakeRig(songs, new[] { album });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Band", TrackCount = 40 });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Lovers", TrackCount = 0 });
        return rig;
    }

    [Fact]
    public void Search_AScopeChip_ShowsOneKindInFull_AndTappingItAgainGoesBackToAll()
    {
        using var rig = SearchLibrary();
        var search = rig.Shell.Search;
        search.Query = "love";
        Assert.Equal(SearchPageViewModel.SongCap, search.Songs.Count);
        Assert.True(search.HasAlbums);

        search.SetScopeCommand.Execute(SearchScope.Songs);
        Assert.Equal(40, search.Songs.Count);
        Assert.False(search.HasAlbums);
        Assert.False(search.HasArtists);
        Assert.True(search.IsSongsScope);

        search.SetScopeCommand.Execute(SearchScope.Songs);
        Assert.True(search.IsAllScope);
        Assert.True(search.HasAlbums);
    }

    [Fact]
    public void Search_TheArtistTheQueryNames_IsFeatured_WithTheirMostPlayedSongs()
    {
        using var rig = SearchLibrary();
        var search = rig.Shell.Search;

        search.Query = "ba";                               // two letters: too short to feature anyone
        Assert.Null(search.Featured);
        Assert.Equal(new[] { "Band" }, search.Artists.Select(a => a.Name));

        search.Query = "band";
        Assert.Equal("Band", search.Featured!.Name);
        Assert.Empty(search.Artists);
        Assert.Equal(new[] { "Love 39", "Love 38", "Love 37", "Love 36", "Love 35" }, search.FeaturedSongs.Select(t => t.Title));

        search.PlayFeaturedSongCommand.Execute(search.FeaturedSongs[1]);
        Assert.Equal("Love 38", rig.Shell.Player.CurrentTrack!.Title);
        Assert.Equal("Band", rig.Shell.Player.SourceLabel);

        // Under the Artists chip the featured artist is just the first artist.
        search.SetScopeCommand.Execute(SearchScope.Artists);
        Assert.Null(search.Featured);
        Assert.Equal(new[] { "Band" }, search.Artists.Select(a => a.Name));
    }

    [Fact]
    public void Search_Sort_ReordersTheSongs_AndLightsTheButton()
    {
        using var rig = SearchLibrary();
        var search = rig.Shell.Search;
        search.SetScopeCommand.Execute(SearchScope.Songs);
        search.Query = "love";
        Assert.Equal("Love 39", search.Songs[0].Title);    // relevance: most played first
        Assert.False(search.IsSorted);

        search.ChooseSort(search.SortChoices.Single(c => (SearchSort)c.Key == SearchSort.Title));

        Assert.Equal("Love 00", search.Songs[0].Title);
        Assert.True(search.IsSorted);
        Assert.Single(search.SortChoices, c => c.IsSelected);
    }

    [Fact]
    public async Task Search_FindsPlaylists_AndAPlaylistResultOpensIt()
    {
        var a = MobileFixtures.Song("Alpha");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var playlist = await rig.Shell.Library.CreatePlaylistAsync("Road Trip");
        await rig.Shell.Library.AddToPlaylistAsync(playlist, new[] { a });

        rig.Shell.Search.Query = "road";

        var result = Assert.Single(rig.Shell.Search.Playlists);
        Assert.Equal("Playlist · 1 song", result.Subtitle);
        rig.Shell.OpenPlaylistCommand.Execute(result.Playlist);
        Assert.Equal("Road Trip", rig.Shell.CurrentPage!.Title);
    }

    // ── Song lists ──

    private static (MobileFixtures.Rig Rig, SongListPageViewModel Page) SongList()
    {
        var songs = new[]
        {
            MobileFixtures.Song("Charlie", artist: "Zed", daysAgo: 3, plays: 1),
            MobileFixtures.Song("Alpha", artist: "Yan", daysAgo: 1, favourite: true, plays: 9),
            MobileFixtures.Song("Bravo", artist: "Xia", daysAgo: 2, plays: 5),
        };
        var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.OpenSongsCommand.Execute(null);
        return (rig, (SongListPageViewModel)rig.Shell.CurrentPage!);
    }

    [Fact]
    public void SongList_Sort_ByTitleThenDescending_AndBackToTheListsOwnOrder()
    {
        var (rig, page) = SongList();
        using var _ = rig;
        var own = page.Songs.Select(t => t.Title).ToArray();

        page.ChooseSort(page.SortChoices.Single(c => (SongSort)c.Key == SongSort.Title));
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, page.Songs.Select(t => t.Title));
        Assert.False(page.IsDescending);

        page.ChooseDirection(descending: true);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, page.Songs.Select(t => t.Title));

        page.ChooseSort(page.SortChoices.Single(c => (SongSort)c.Key == SongSort.MostPlayed));
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, page.Songs.Select(t => t.Title));   // counts start most first

        page.ChooseSort(page.SortChoices[0]);
        Assert.Equal(own, page.Songs.Select(t => t.Title));
        Assert.False(page.IsSorted);
    }

    [Fact]
    public void SongList_OwnOrderDescending_ThenOwnOrderAgain_UnlightsTheSortButton()
    {
        var (rig, page) = SongList();
        using var _ = rig;
        page.ChooseDirection(descending: true);
        Assert.True(page.IsSorted);
        var raised = new List<string?>();
        page.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        page.ChooseSort(page.SortChoices[0]);                  // the list's own order again: ascending, Sort unchanged

        Assert.False(page.IsSorted);
        Assert.Contains(nameof(SongListPageViewModel.IsSorted), raised);   // the button's Classes.on binding hears it
    }

    [Fact]
    public void SongList_FavouritesFilter_AndInListSearch_NarrowWhatPlays()
    {
        var (rig, page) = SongList();
        using var _ = rig;

        page.OnlyFavourites = true;
        Assert.Equal(new[] { "Alpha" }, page.Songs.Select(t => t.Title));
        Assert.Equal("1 of 3", page.CountText);
        Assert.True(page.IsFiltered);
        Assert.False(page.CanFilterDownloaded);                // no account: no Downloaded filter

        page.OnlyFavourites = false;
        page.FilterText = "xia";                               // matches the artist
        Assert.Equal(new[] { "Bravo" }, page.Songs.Select(t => t.Title));
        page.PlayAllCommand.Execute(null);
        Assert.Equal("Bravo", rig.Shell.Player.CurrentTrack!.Title);

        page.FilterText = "nothing";
        Assert.True(page.ShowEmpty);
        Assert.Equal("No matching songs", page.EmptyText);
        Assert.True(page.HasSongs);                            // the toolbar stays to undo it

        page.FilterText = string.Empty;                        // the field's clear button
        Assert.Equal(3, page.SongCount);
    }

    [Fact]
    public async Task PlaylistPage_DateAdded_SortsByWhenEachSongJoinedThePlaylist()
    {
        var old = MobileFixtures.Song("Old", daysAgo: 30);
        var fresh = MobileFixtures.Song("Fresh", daysAgo: 1);
        using var rig = MobileFixtures.MakeRig(new[] { old, fresh });
        var playlist = await rig.Shell.Library.CreatePlaylistAsync("Mix");
        await rig.Shell.Library.AddToPlaylistAsync(playlist, new[] { fresh });
        await rig.Shell.Library.AddToPlaylistAsync(playlist, new[] { old });     // added to the playlist last

        rig.Shell.OpenPlaylistCommand.Execute(playlist);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal("Playlist Order", page.SortChoices[0].Label);

        page.ChooseSort(page.SortChoices.Single(c => (SongSort)c.Key == SongSort.DateAdded));

        Assert.Equal(new[] { "Old", "Fresh" }, page.Songs.Select(t => t.Title));  // newest to the playlist first
    }

    // ── Headers ──

    [AvaloniaFact]
    public void Headers_TheListToolsSitInTheTitleRow_AndEveryTabHasTheProfile()
    {
        var a = MobileFixtures.Song("Alpha", favourite: true);
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Favorites);
        window.UpdateLayout();
        var favourites = view.FindControl<Grid>("FavoritesRoot")!;
        // One pill shows: the header's (the embedded list hides its own title row).
        var tools = favourites.GetVisualDescendants().OfType<ListTools>().Single(t => t.IsEffectivelyVisible);
        Assert.Same(rig.Shell.FavoritesRoot, tools.DataContext);
        // Apple's tab headers each end in the profile circle (Settings), not Library's alone.
        Assert.Contains(favourites.GetVisualDescendants().OfType<Button>(), b => b.Command == rig.Shell.OpenSettingsCommand);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Playlists);
        window.UpdateLayout();
        Assert.Contains(view.FindControl<Grid>("PlaylistsRoot")!.GetVisualDescendants().OfType<Button>(),
            b => b.Command == rig.Shell.OpenSettingsCommand);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Search);
        rig.Shell.Search.Query = "alp";
        window.UpdateLayout();
        Assert.Contains(MobileFixtures.Find<SearchPage>(view).GetVisualDescendants().OfType<Button>(),
            b => b.Command == rig.Shell.OpenSettingsCommand);
        var sort = MobileFixtures.Named<Button>(view, "SortButton");
        var chips = MobileFixtures.Named<ScrollViewer>(view, "ScopeBar");
        // The sort button sits above the chip row, so the chips never scroll under it.
        var sortBottom = sort.TranslatePoint(new Point(0, sort.Bounds.Height), view)!.Value.Y;
        var chipsTop = chips.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        Assert.True(sortBottom <= chipsTop, $"sort bottom {sortBottom} vs chips top {chipsTop}");
        window.Close();
    }

    // ── Mini player ──

    [AvaloniaFact]
    public void FractionPanel_LaysItsChildOverTheElapsedPart()
    {
        var fill = new Border();
        var panel = new FractionPanel { Fraction = 0.25, Children = { fill } };
        var window = new Window { Width = 400, Height = 60, Content = panel };
        window.Show();
        window.UpdateLayout();
        Assert.Equal(100, fill.Bounds.Width, 1);

        panel.Fraction = 0.5;
        window.UpdateLayout();
        Assert.Equal(200, fill.Bounds.Width, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void MiniBar_HasTheElapsedFill_BoundToTheSongsProgress()
    {
        var songs = Enumerable.Range(0, 2).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        window.UpdateLayout();

        var fill = view.GetVisualDescendants().OfType<FractionPanel>().Single();
        Assert.Equal(rig.Shell.Player.ProgressFraction, fill.Fraction);
        Assert.NotNull(rig.Shell.MiniProgressFill);
        window.Close();
    }
}
