using System;
using System.Collections.Generic;
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

/// <summary>
/// Asked since beta 1: "The option to choose how you sort the albums in the albums page." The
/// Albums page has the Songs list's sort button and glass menu (Title, Artist, Release Year,
/// Recently Added, Most Played, then Ascending / Descending), and keeps the sort it was left in
/// across leaving, the Albums tab and a restart (settings.json, MobileListSorts).
/// </summary>
public class MobileAlbumSortTests
{
    // Bravo: 2001, added 30 days ago, 3 plays. Alpha: 1999, 1 day ago, 10 plays. Charlie: 2010, 10 days ago, 12 plays (two songs).
    private static (Track[] Songs, Album[] Albums) Library()
    {
        var b = MobileFixtures.Song("b1", daysAgo: 30, plays: 3);
        var a = MobileFixtures.Song("a1", daysAgo: 1, plays: 10);
        var c1 = MobileFixtures.Song("c1", daysAgo: 10, plays: 5);
        var c2 = MobileFixtures.Song("c2", daysAgo: 12, plays: 7);
        var bravo = MobileFixtures.MakeAlbum("Bravo", "Zed", b);
        var alpha = MobileFixtures.MakeAlbum("Alpha", "Moe", a);
        var charlie = MobileFixtures.MakeAlbum("Charlie", "Abe", c1, c2);
        (bravo.Year, alpha.Year, charlie.Year) = (2001, 1999, 2010);
        return (new[] { b, a, c1, c2 }, new[] { bravo, alpha, charlie });
    }

    private static AlbumGridPageViewModel OpenAlbums(MobileFixtures.Rig rig)
    {
        rig.Shell.OpenAlbumsCommand.Execute(null);
        return Assert.IsType<AlbumGridPageViewModel>(rig.Shell.CurrentPage);
    }

    private static void Choose(AlbumGridPageViewModel page, AlbumSort sort) =>
        page.ChooseSort(page.SortChoices.Single(c => (AlbumSort)c.Key == sort));

    private static string[] Names(AlbumGridPageViewModel page) => page.Albums.Select(a => a.Name).ToArray();

    [Fact]
    public void EachSort_OrdersTheAlbums_TheWayItIsUsuallyRead_AndTheDirectionFlipsIt()
    {
        var (songs, albums) = Library();
        using var rig = MobileFixtures.MakeRig(songs, albums);
        var page = OpenAlbums(rig);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, Names(page));   // A→Z, the page's own order
        Assert.False(page.IsSorted);

        Choose(page, AlbumSort.Artist);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(page));   // Abe, Moe, Zed
        Assert.False(page.IsDescending);

        Choose(page, AlbumSort.Year);
        Assert.True(page.IsDescending);   // newest first
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, Names(page));

        Choose(page, AlbumSort.RecentlyAdded);
        Assert.Equal(new[] { "Alpha", "Charlie", "Bravo" }, Names(page));   // an album's newest song

        Choose(page, AlbumSort.MostPlayed);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(page));   // 12 (5 + 7), 10, 3
        page.ChooseDirection(false);
        Assert.Equal(new[] { "Bravo", "Alpha", "Charlie" }, Names(page));

        Choose(page, AlbumSort.Title);
        page.ChooseDirection(true);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, Names(page));
        Assert.True(page.IsSorted);   // reversed A→Z still lights the button
        Assert.Equal(new[] { AlbumSort.Title }, page.SortChoices.Where(c => c.IsSelected).Select(c => (AlbumSort)c.Key));
    }

    [Fact]
    public void Year_BreaksTiesByTitle_AndTheSearchFiltersTheSortedList()
    {
        var (songs, albums) = Library();
        albums[1].Year = 2001;   // Alpha now shares Bravo's year
        using var rig = MobileFixtures.MakeRig(songs, albums);
        var page = OpenAlbums(rig);
        Choose(page, AlbumSort.Year);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(page));

        page.Query = "a";   // every name has an "a"; Zed's Bravo too
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(page));
        page.Query = "br";
        Assert.Equal(new[] { "Bravo" }, Names(page));
        page.Query = string.Empty;
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(page));
    }

    [Fact]
    public async Task TheSort_IsKept_AfterLeavingAndComingBack_AndAfterARestart_AndTheOwnOrderClearsIt()
    {
        var (songs, albums) = Library();
        using var rig = MobileFixtures.MakeRig(songs, albums);
        await rig.Shell.InitializeAsync();
        var page = OpenAlbums(rig);
        Choose(page, AlbumSort.MostPlayed);

        rig.Shell.GoBack();
        var again = OpenAlbums(rig);
        Assert.NotSame(page, again);
        Assert.Equal(AlbumSort.MostPlayed, again.Sort);
        Assert.True(again.IsDescending);
        Assert.True(again.SortChoices.Single(c => (AlbumSort)c.Key == AlbumSort.MostPlayed).IsSelected);
        Assert.False(again.SortChoices.Single(c => (AlbumSort)c.Key == AlbumSort.Title).IsSelected);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(again));

        await rig.Shell.PendingListSortSave;
        var saved = (await rig.Persistence.LoadSettingsAsync()).MobileListSorts;
        Assert.Equal("MostPlayed desc", saved[AlbumGridPageViewModel.SortKey]);

        // A restart: a new shell over the same settings.
        var (songs2, albums2) = Library();
        using var restarted = MobileFixtures.MakeRig(songs2, albums2, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileListSorts = new Dictionary<string, string>(saved);
            await p.SaveSettingsAsync(s);
        });
        await restarted.Shell.InitializeAsync();
        var cold = OpenAlbums(restarted);
        Assert.Equal(AlbumSort.MostPlayed, cold.Sort);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(cold));

        Choose(cold, AlbumSort.Title);
        await restarted.Shell.PendingListSortSave;
        Assert.False((await restarted.Persistence.LoadSettingsAsync()).MobileListSorts.ContainsKey(AlbumGridPageViewModel.SortKey));
        Assert.False(cold.IsSorted);
    }

    [Fact]
    public async Task AnOpenAlbumsPage_FollowsASortChosenOnAnother()
    {
        var (songs, albums) = Library();
        using var rig = MobileFixtures.MakeRig(songs, albums);
        await rig.Shell.InitializeAsync();
        var under = new AlbumGridPageViewModel(rig.Shell);   // stands in for the Albums tab's page
        var page = OpenAlbums(rig);
        Choose(page, AlbumSort.Artist);
        Assert.Equal(AlbumSort.Artist, under.Sort);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(under));
        under.OnClosed();
    }

    /// <summary>The header's sort button pops the shared sort menu (the one in use and its direction
    /// ticked), and a new sort starts the grid at its top.</summary>
    [AvaloniaFact]
    public void TheHeaderSortButton_OpensTheSortMenu_AndANewSortScrollsToTheTop()
    {
        var songs = new List<Track>();
        var albums = new List<Album>();
        for (var i = 0; i < 200; i++)
        {
            var song = MobileFixtures.Song($"s{i:D3}", plays: i);
            songs.Add(song);
            albums.Add(MobileFixtures.MakeAlbum($"Album {i:D3}", "Band", song));
        }
        using var rig = MobileFixtures.MakeRig(songs.ToArray(), albums.ToArray());
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var page = OpenAlbums(rig);
        MobileFixtures.Land(window);
        var pageView = MobileFixtures.PageView(view, page);

        var button = MobileFixtures.Named<Button>(pageView, "AlbumSortButton");
        Assert.True(button.IsEffectivelyVisible);
        button.Command!.Execute(null);
        var menu = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        Assert.Equal(new[] { "Title", "Artist", "Release Year", "Recently Added", "Most Played", "Ascending", "Descending" },
            menu.Items.Select(i => i.Label));
        Assert.Equal(new[] { "Title", "Ascending" }, menu.Items.Where(i => i.IsChecked).Select(i => i.Label));
        rig.Shell.OptionsMenu = null;

        var scroll = MobileFixtures.Named<ScrollViewer>(pageView, "AlbumScroll");
        scroll.Offset = new Vector(0, 2000);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroll.Offset.Y > 1000);
        Choose(page, AlbumSort.MostPlayed);
        window.UpdateLayout();
        Assert.Equal(0, scroll.Offset.Y, 1);
        Assert.Equal("Album 199", page.Albums[0].Name);
        window.Close();
    }

    /// <summary>The Albums tab: the tab's own header carries the same sort pill, bound to the tab's page.</summary>
    [AvaloniaFact]
    public void TheAlbumsTab_HasTheSortPillInItsHeader()
    {
        var (songs, albums) = Library();
        using var rig = MobileFixtures.MakeRig(songs, albums);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.SetBarTabs(new[] { MobileTab.Albums, MobileTab.Library });
        for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(MobileTab.Albums, rig.Shell.SelectedTab);

        var root = view.FindControl<Grid>("AlbumsRoot")!;
        var tools = root.GetVisualDescendants().OfType<AlbumTools>().Single(t => t.IsEffectivelyVisible);
        Assert.Same(rig.Shell.AlbumsRoot, tools.DataContext);
        var button = MobileFixtures.Named<Button>(tools, "AlbumSortButton");
        button.Command!.Execute(null);
        Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        window.Close();
    }
}
