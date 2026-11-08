using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Library tab root (tiles, first-launch and reconnect cards) and the list pages the tiles push.</summary>
public class MobileLibraryPagesTests
{
    private static Func<Noctis.Services.PersistenceService, Task> WithFolder(params Playlist[] playlists) => async p =>
    {
        var s = await p.LoadSettingsAsync();
        s.MusicFolders.Add("content://tree/music");
        await p.SaveSettingsAsync(s);
        if (playlists.Length > 0) await p.SavePlaylistsAsync(playlists.ToList());
    };

    /// <summary>Review Focus #1: first launch — no folder, no tracks, nothing to show but the way in.</summary>
    [AvaloniaFact]
    public void EmptyLibrary_ShowsTheConnectCard_AndHidesTheTiles()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.True(MobileFixtures.Named<Border>(view, "ConnectCard").IsVisible);
        Assert.False(MobileFixtures.Named<Border>(view, "ReconnectCard").IsVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(view, "PinnedSection").IsVisible);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        var library = MobileFixtures.Find<LibraryPage>(view);
        Assert.True(MobileFixtures.Named<Border>(library, "ConnectCard").IsVisible);
        Assert.False(MobileFixtures.Named<ItemsControl>(library, "LibraryRowList").IsVisible);
        Assert.False(rig.Shell.IsMiniBarVisible);
        window.Close();
    }

    /// <summary>Signed in to a computer with no folder on the phone: the desktop's songs are the
    /// library. The tabs gated everything on a folder, so Library showed only "Add your music"
    /// (no Songs/Albums/Artists rows, no Edit) and Home no Pinned section, over a full library.</summary>
    [AvaloniaFact]
    public void SongsWithoutAFolder_ShowTheRows_NotTheConnectCard()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("From the computer") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.False(rig.Shell.Library.HasFolders);
        Assert.True(MobileFixtures.Named<StackPanel>(view, "PinnedSection").IsVisible);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        var library = MobileFixtures.Find<LibraryPage>(view);
        Assert.True(MobileFixtures.Named<ItemsControl>(library, "LibraryRowList").IsVisible);
        Assert.False(rig.Shell.Library.ShowConnectCard);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Name == "ConnectCard" && b.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Tiles_ShowCounts_AndOpenTheirLists()
    {
        var a = MobileFixtures.Song("Alpha", favourite: true);
        var b = MobileFixtures.Song("Beta", daysAgo: 60);
        var c = MobileFixtures.Song("Gamma");
        var album = MobileFixtures.MakeAlbum("First", "Band", a, b);
        var mix = new Playlist { Name = "Mix", TrackIds = { c.Id, a.Id } };
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, new[] { album }, WithFolder(mix));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.True(MobileFixtures.Named<Border>(view, "PinnedHint").IsVisible);     // Home: nothing pinned yet
        Assert.False(MobileFixtures.Named<PathIcon>(view, "PinnedChevron").IsVisible); // so no › into an empty page
        Assert.False(MobileFixtures.Named<Button>(view, "PinnedHeader").IsHitTestVisible);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        Assert.True(MobileFixtures.Named<ItemsControl>(view, "LibraryRowList").IsVisible);
        Assert.False(rig.Shell.Library.HasNotice);                                   // no card: the notices collapse
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Name == "ConnectCard" && b.IsEffectivelyVisible);
        Assert.Equal(3, rig.Shell.Library.SongCount);
        Assert.Equal(1, rig.Shell.Library.FavoriteCount);
        Assert.Equal(2, rig.Shell.Library.RecentlyAddedCount);                      // the 30-day window drops Beta

        MobileFixtures.Named<Button>(view, "RecentlyAddedHeader").Command!.Execute(null);
        var recent = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        // The rail's rule: newest album first, its songs in track order, however old (Beta too).
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, recent.Songs.Select(t => t.Title).OrderBy(t => t));
        rig.Shell.NavigateBackCommand.Execute(null);

        rig.Shell.OpenFavouritesCommand.Execute(null);
        var favourites = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new[] { "Alpha" }, favourites.Songs.Select(t => t.Title));

        rig.Shell.NavigateBackCommand.Execute(null);
        rig.Shell.OpenPlaylistsCommand.Execute(null);
        var playlists = Assert.IsType<PlaylistListPageViewModel>(rig.Shell.CurrentPage);
        rig.Shell.OpenPlaylistCommand.Execute(playlists.Playlists.Single());
        var mixPage = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new[] { "Gamma", "Alpha" }, mixPage.Songs.Select(t => t.Title));   // saved order, not title order

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        window.UpdateLayout();
        var grid = MobileFixtures.Find<AlbumGridPage>(view);
        Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "First");
        window.Close();
    }

    /// <summary>Every playlist edit re-reads playlists.json into new objects: an open playlist page
    /// must follow its playlist by id, or songs added after any other edit never showed on it
    /// (nor sorted by their Date Added) until the page was reopened.</summary>
    [Fact]
    public async Task OpenPlaylistPage_ShowsSongsAddedAfterAnotherPlaylistEdit()
    {
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        var mix = new Playlist { Name = "Mix", TrackIds = { a.Id } };
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, null, WithFolder(mix));
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);

        await rig.Shell.Library.CreatePlaylistAsync("Other");                   // re-reads every playlist
        await rig.Shell.Library.AddToPlaylistAsync(rig.Shell.Library.Playlists.Single(p => p.Name == "Mix"), new[] { b });

        Assert.Equal(new[] { "Alpha", "Beta" }, page.Songs.Select(t => t.Title));
    }

    /// <summary>The Favorites list's filter menu offered Favorites (a no-op there); with nothing
    /// else to offer (no computer) its filter button goes. Other lists keep it.</summary>
    [Fact]
    public void FavoritesList_DoesNotOfferTheFavoritesFilter()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Alpha", favourite: true) });

        rig.Shell.FavoritesRoot.ShowFilterMenuCommand.Execute(null);
        Assert.DoesNotContain(rig.Shell.OptionsMenu?.Items ?? Array.Empty<OptionsMenuItem>(), i => i.Label == "Favorites");
        Assert.False(rig.Shell.FavoritesRoot.HasFilters);

        rig.Shell.OpenSongsCommand.Execute(null);
        var songs = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.True(songs.HasFilters);
        songs.ShowFilterMenuCommand.Execute(null);
        Assert.Contains(rig.Shell.OptionsMenu!.Items, i => i.Label == "Favorites");
    }

    [Fact]
    public void SongList_TapPlaysFromThatRow_AndShuffleAllShuffles()
    {
        var songs = Enumerable.Range(0, 5).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.OpenSongsCommand.Execute(null);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;

        page.PlayCommand.Execute(page.Songs[2]);
        Assert.Same(page.Songs[2], rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));

        page.ShuffleAllCommand.Execute(null);
        Assert.True(rig.Shell.Player.IsShuffleEnabled);
        Assert.Equal(songs.Select(t => t.Title).OrderBy(x => x),
            rig.Shell.Player.UpNext.Append(rig.Shell.Player.CurrentTrack!).Select(t => t.Title).OrderBy(x => x));
    }

    /// <summary>Review Focus #2: a big library refreshes as one Reset (not 10,000 Adds, the
    /// desktop's flicker/stall shape) and the list realises about a screenful of rows.</summary>
    [AvaloniaFact]
    public void TenThousandSongs_RefreshIsOneReset_AndThePageRealizesAScreenful()
    {
        var songs = Enumerable.Range(0, 10_000).Select(i => MobileFixtures.Song($"Song {i:D5}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var resets = 0;
        var other = 0;
        rig.Shell.Library.Songs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++; else other++;
        };

        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(1, resets);
        Assert.Equal(0, other);

        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        window.UpdateLayout();
        var list = MobileFixtures.Named<ItemsControl>(view, "SongList");
        Assert.InRange(list.GetRealizedContainers().Count(), 5, 200);
        window.Close();
    }

    [Fact]
    public void LibraryUpdated_RefreshesAnOpenList_AndAClosedPageStopsListening()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Alpha") });
        rig.Shell.OpenSongsCommand.Execute(null);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        Assert.Single(page.Songs);

        rig.Library.TrackList.Add(MobileFixtures.Song("Beta"));
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(2, page.Songs.Count);

        rig.Shell.NavigateBackCommand.Execute(null);
        rig.Library.TrackList.Add(MobileFixtures.Song("Gamma"));
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(2, page.Songs.Count);
    }

    /// <summary>ScrollMemory through the real PageHost + DataTemplate (not an explicit
    /// DataContext): Songs, then an artist's list (the same page type, so the same template)
    /// and Back lands where the Songs list was left.</summary>
    [AvaloniaFact]
    public void SongList_KeepsItsScrollPlace_AcrossAPushAndBack()
    {
        var songs = Enumerable.Range(0, 500).Select(i => MobileFixtures.Song($"Song {i:D3}", artist: i % 2 == 0 ? "Even" : "Odd")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        window.UpdateLayout();

        MobileFixtures.Named<ScrollViewer>(view, "SongScroll").Offset = new Vector(0, 3000);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3000, page.ScrollOffset.Y, 1);

        // 250 rows: long enough to inherit the offset. Pushed directly: an artist link now opens
        // the artist page, which has no SongScroll.
        rig.Shell.Navigate(new SongListPageViewModel(rig.Shell, "Even", () => MobileLibrary.SongsBy(rig.Library, "Even")));
        MobileFixtures.Land(window);
        Dispatcher.UIThread.RunJobs();
        // Scoped to the top page's view: the Songs page stays in the tree, hidden, under it.
        Assert.Equal(0, MobileFixtures.Named<ScrollViewer>(MobileFixtures.PageView(view, rig.Shell.CurrentPage!), "SongScroll").Offset.Y, 1);   // a new page starts at the top

        rig.Shell.NavigateBackCommand.Execute(null);
        MobileFixtures.Land(window);
        for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        Assert.Same(page, rig.Shell.CurrentPage);
        Assert.Equal(3000, MobileFixtures.Named<ScrollViewer>(MobileFixtures.PageView(view, page), "SongScroll").Offset.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ScanAborted_ShowsTheReconnectCard()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Alpha") }, seed: WithFolder());
        rig.Library.AbortScanWithRoots = new[] { "content://tree/music" };

        await rig.Shell.Library.RescanCommand.ExecuteAsync(null);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.True(rig.Shell.Library.NeedsReconnect);
        Assert.True(MobileFixtures.Named<Border>(view, "ReconnectCard").IsVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(view, "StatusLine").IsVisible);   // the card carries the text
        window.Close();
    }

    /// <summary>The Playlists tab: "1 song", not "1 songs"; the header pill searches and sorts
    /// the list, pinned playlists first throughout.</summary>
    [AvaloniaFact]
    public void PlaylistsTab_CountsInTheSingular_AndItsToolsSearchAndSort()
    {
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        var one = new Playlist { Name = "Solo", TrackIds = { a.Id }, ModifiedAt = DateTime.UtcNow.AddDays(-1) };
        var two = new Playlist { Name = "Duo", TrackIds = { a.Id, b.Id }, ModifiedAt = DateTime.UtcNow.AddDays(-3) };
        var pinned = new Playlist { Name = "Zed", IsPinned = true, ModifiedAt = DateTime.UtcNow.AddDays(-9) };
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, seed: WithFolder(one, two, pinned));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Playlists);
        window.UpdateLayout();

        var page = rig.Shell.PlaylistsRoot;
        Assert.Equal(new[] { "Zed", "Duo", "Solo" }, page.Rows.Select(r => r.Name));
        Assert.Equal(new[] { "0 songs", "2 songs", "1 song" }, page.Rows.Select(r => r.Subtitle));
        var root = view.FindControl<Grid>("PlaylistsRoot")!;
        var shown = root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("1 song", shown);
        Assert.DoesNotContain("1 songs", shown);
        // The tab's header carries the tools pill, as Favorites' does.
        var tools = root.GetVisualDescendants().OfType<PlaylistTools>().Single(t => t.IsEffectivelyVisible);
        Assert.Same(page, tools.DataContext);

        page.ChooseSort(page.SortChoices.Single(c => c.Label == "Recently Updated"));
        Assert.True(page.IsSorted);
        Assert.Equal(new[] { "Zed", "Solo", "Duo" }, page.Playlists.Select(p => p.Name));
        page.ChooseSort(page.SortChoices.Single(c => c.Label == "Title"));

        // "Find in Playlists": the list pages' shared field, always there (no toggle), as on Favorites.
        Assert.True(MobileFixtures.Named<FindField>(root, "PlaylistFindField").IsEffectivelyVisible);
        page.FilterText = "so";
        Assert.Equal(new[] { "Solo" }, page.Playlists.Select(p => p.Name));
        page.FilterText = "nothing";
        Assert.True(page.ShowNoMatches);
        page.FilterText = string.Empty;
        Assert.Equal(3, page.Playlists.Count);
        window.Close();
    }
}
