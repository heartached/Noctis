using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone testers (11-22): "the app is still laggy", "in some menus it just feels laggy". What the
/// browsing pages cost on a big library, measured headless (desktop CPU; a mid-range phone core
/// is several times slower). Each test holds one of the fixes to its number.
/// </summary>
public class MobileListPerfTests
{
    private readonly ITestOutputHelper _out;
    public MobileListPerfTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] FindKeys = { "l", "lo", "lov", "love", "lov", "lo", "l", "" };

    /// <summary>
    /// Find in Songs on a sorted list: each filter the field hands on (after its debounce) sorted
    /// the matches again, culture-aware with four tie-breaks, and clearing the field sorted every
    /// song. The order does not depend on the filter, so the list keeps the sorted songs and a
    /// filter only walks them.
    /// </summary>
    [Fact]
    public void FindInSongs_OnASortedList_DoesNotSortAgainPerKeystroke()
    {
        using var rig = MobileSearchPerfTests.BigLibrary(7_200, 600);
        var page = new SongListPageViewModel(rig.Shell, "Nav.Songs", rig.Shell.Library.SourceSongs);
        Assert.Equal(7_200, page.Songs.Count);
        page.ChooseSort(page.SortChoices.First(c => Equals(c.Key, SongSort.Artist)));
        foreach (var k in FindKeys) page.FilterText = k;   // warm-up: the lazily cached search keys

        var times = new List<double>();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 5; round++)
            foreach (var k in FindKeys)
            {
                var start = Stopwatch.GetTimestamp();
                page.FilterText = k;
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        var perKey = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / (5 * FindKeys.Length);
        times.Sort();
        var median = times[times.Count / 2];
        _out.WriteLine($"7.2k songs by Artist: median filter {median:F2} ms, max {times[^1]:F2} ms, {perKey / 1024.0:F0} KB per keystroke");

        // Same rows, same order as a fresh sort of the matches.
        page.FilterText = "love";
        var match = new MobileSearch.Matcher("love");
        var names = StringComparer.CurrentCultureIgnoreCase;
        var expected = rig.Shell.Library.SourceSongs().Where(match.HitTrack)
            .OrderBy(t => t.Artist, names).ThenBy(t => t.Album, names).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Title, names).ToList();
        Assert.Equal(expected, page.Songs);
        page.FilterText = "";
        Assert.Equal(7_200, page.Songs.Count);

        // Before: median 1.55 ms, slowest 5.9 ms (the cleared field sorted every song), 174 KB a
        // keystroke; after: 0.52 ms, 0.54 ms, 20 KB (desktop).
        Assert.True(perKey < 64 * 1024, $"{perKey / 1024} KB per keystroke");
    }

    /// <summary>
    /// Search's Albums chip: a broad query's matches (up to 300) were a plain two-column panel,
    /// so every match built its tile and decoded its cover at once. Rows now virtualise.
    /// </summary>
    [AvaloniaFact]
    public void SearchAlbumsChip_BuildsOnlyTheTilesOnScreen_InResultOrder()
    {
        using var rig = MobileSearchPerfTests.BigLibrary(12_000, 900);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Search);
        MobileFixtures.Land(window);
        var page = MobileFixtures.Find<SearchPage>(view);
        var search = rig.Shell.Search;
        search.Scope = SearchScope.Albums;
        var start = Stopwatch.GetTimestamp();
        search.Query = "e";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var tiles = page.GetVisualDescendants().OfType<AlbumTile>().Where(t => t.IsEffectivelyVisible).ToList();
        _out.WriteLine($"search albums: {search.Albums.Count} results, {tiles.Count} tiles realized, {ms:F0} ms");
        Assert.Equal(SearchPageViewModel.ScopedCap, search.Albums.Count);
        // Before: 300 tiles, 2,683 ms. A screen holds three rows of two; the panel keeps a few spare.
        Assert.InRange(tiles.Count, 4, 24);
        // The grid reads left to right, top to bottom, in result order, and a tap opens its album.
        var grid = MobileFixtures.Named<ItemsControl>(page, "AlbumGridResults");
        var shown = grid.GetRealizedContainers().OrderBy(grid.IndexFromContainer)
            .SelectMany(c => c.GetVisualDescendants().OfType<AlbumTile>().Where(t => t.IsVisible && t.DataContext != null))
            .Select(t => t.DataContext).ToList();
        Assert.Equal(search.Albums.Take(shown.Count), shown);
        var button = grid.GetRealizedContainers().First().GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row"));
        Assert.Same(search.Albums[0], button.CommandParameter);
        Assert.Same(search.OpenAlbumCommand, button.Command);

        // Back under All the grid empties (the row shows instead) and its rows go.
        search.Scope = SearchScope.All;
        Assert.Empty(search.AlbumRows);
        Assert.True(search.ShowAlbumRow);
        window.Close();
    }

    /// <summary>
    /// A section's › page (Downloaded, Pinned, Top Artists, an artist's See All): every tile was
    /// built at once over a plain three-column panel. Rows now virtualise.
    /// </summary>
    [AvaloniaFact]
    public void RailGridPage_BuildsOnlyTheTilesOnScreen_AndScrollsThroughEveryItem()
    {
        using var rig = MobileSearchPerfTests.BigLibrary(4_800, 300);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var items = rig.Library.Albums.Select(RailItem.ForAlbum).ToList();
        var start = Stopwatch.GetTimestamp();
        rig.Shell.Navigate(new RailGridPageViewModel(rig.Shell, "Mobile.Downloaded", () => items));
        MobileFixtures.Land(window);
        Dispatcher.UIThread.RunJobs();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var page = MobileFixtures.Find<RailGridPage>(view);
        var tiles = page.GetVisualDescendants().OfType<RailTile>().ToList();
        _out.WriteLine($"rail grid: {items.Count} items, {tiles.Count} tiles realized, {ms:F0} ms");
        // Before: 400 tiles (every item), 1,946 ms.
        Assert.InRange(tiles.Count, 6, 36);

        // The tiles on screen as the page opens glide in (Appear), as before.
        Assert.All(tiles, t => Assert.NotNull(((Control)t.Content!).Transitions));

        // Scrolled to the end, the tiles on screen are the last items, in order, each opening its own;
        // a row the panel hands the next items to does not replay the arrival (a plain panel's tiles,
        // all built at once, never did).
        var scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Classes.Contains("page-scroll"));
        var grid = MobileFixtures.Named<ItemsControl>(page, "RailGrid");
        var built = new HashSet<RailTile>(ReferenceEqualityComparer.Instance);
        var replays = 0;
        void Watch()
        {
            foreach (var t in page.GetVisualDescendants().OfType<RailTile>())
                if (built.Add(t))
                    ((Control)t.Content!).PropertyChanged += (_, e) =>
                    {
                        if (e.Property == Avalonia.Animation.Animatable.TransitionsProperty && e.NewValue == null) replays++;
                    };
        }
        Watch();
        for (var y = 0.0; y <= scroll.Extent.Height; y += 120)
        {
            scroll.Offset = new Avalonia.Vector(0, y);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Watch();
        }
        Assert.InRange(built.Count, 6, 48);   // reused, not built per row scrolled past
        Assert.Equal(0, replays);             // without RowHost.Arrives: 105 over this scroll
        var last = grid.GetRealizedContainers().OrderBy(grid.IndexFromContainer).Last();
        var lastButtons = last.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("row") && b.IsVisible).ToList();
        Assert.Equal(items.Skip((items.Count - 1) / 3 * 3), lastButtons.Select(b => b.CommandParameter));
        window.Close();
    }

    /// <summary>
    /// A heart toggled (a row's menu, the album heart, Now Playing) refreshes every open list. The
    /// Songs tab sorted all its songs again and the Artists tab rebuilt every circle, though a
    /// heart changes no sort key and nothing a circle shows. Now only what hearts touch re-runs:
    /// the Favorites list, and a list filtered to favourites.
    /// </summary>
    [Fact]
    public void HeartTap_DoesNotResortOrRebuildTheOpenLists_ButFavouriteViewsFollow()
    {
        using var rig = MobileSearchPerfTests.BigLibrary(7_200, 600);
        var songs = new SongListPageViewModel(rig.Shell, "Nav.Songs", rig.Shell.Library.SourceSongs);
        songs.ChooseSort(songs.SortChoices.First(c => Equals(c.Key, SongSort.Artist)));
        var filtered = new SongListPageViewModel(rig.Shell, "Nav.Songs", rig.Shell.Library.SourceSongs) { OnlyFavourites = true };
        var favs = new SongListPageViewModel(rig.Shell, "Nav.Favorites", rig.Shell.Library.Favourites);
        var albums = new AlbumGridPageViewModel(rig.Shell);
        var artists = new ArtistListPageViewModel(rig.Shell);
        var shownSongs = songs.Songs.ToList();
        var albumRows = albums.Rows.ToList();
        var artistRows = artists.Rows.ToList();
        rig.Library.NotifyFavoritesChanged();   // warm-up

        var times = new List<double>();
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            rig.Library.TrackList[i].IsFavorite = !rig.Library.TrackList[i].IsFavorite;
            var start = Stopwatch.GetTimestamp();
            rig.Library.NotifyFavoritesChanged();
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        var perTap = (GC.GetAllocatedBytesForCurrentThread() - alloc) / 10;
        times.Sort();
        _out.WriteLine($"heart tap, Songs (by Artist) + Favorites + filtered + Albums + Artists open: median {times[5]:F1} ms, {perTap / 1024} KB");

        // Ten taps on ten songs left them all favourites (they started not).
        var hearted = rig.Library.TrackList.Take(10).ToHashSet();
        Assert.Equal(shownSongs, songs.Songs);
        Assert.Equal(hearted.OrderBy(t => t.Id), favs.Songs.OrderBy(t => t.Id));
        Assert.Equal(hearted.OrderBy(t => t.Id), filtered.Songs.OrderBy(t => t.Id));
        Assert.Equal(10, filtered.SongCount);
        Assert.True(albumRows.SequenceEqual(albums.Rows, ReferenceEqualityComparer.Instance));
        Assert.True(artistRows.SequenceEqual(artists.Rows, ReferenceEqualityComparer.Instance));

        // Before: 17.3 ms, 735 KB a tap (Songs 11 ms / 423 KB, Artists 7.7 ms / 246 KB of it).
        Assert.True(perTap < 200 * 1024, $"{perTap / 1024} KB per heart tap");

        // A library update still re-sorts: a renamed artist moves.
        var moved = songs.Songs[0];
        moved.Artist = "zzzz";
        rig.Library.RaiseLibraryUpdated();
        Assert.Same(moved, songs.Songs[^1]);
    }

    /// <summary>
    /// A download batch refreshes the open lists each time the number of offline copies changes. A
    /// download changes no song's tags, so a list that still holds the same songs keeps its sorted
    /// order; one whose songs changed (a source, the Favorites-like lists) sorts again.
    /// </summary>
    [Fact]
    public void DownloadsChanging_DoesNotResortASongListThatHoldsTheSameSongs()
    {
        using var rig = MobileSearchPerfTests.BigLibrary(7_200, 600);
        var songs = new SongListPageViewModel(rig.Shell, "Nav.Songs", rig.Shell.Library.SourceSongs);
        songs.ChooseSort(songs.SortChoices.First(c => Equals(c.Key, SongSort.Artist)));
        var shown = songs.Songs.ToList();
        rig.Shell.Library.NotifyDownloadsChanged();   // warm-up

        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++) rig.Shell.Library.NotifyDownloadsChanged();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 10;
        var per = (GC.GetAllocatedBytesForCurrentThread() - alloc) / 10;
        _out.WriteLine($"downloads changed, Songs (by Artist) open: {ms:F1} ms, {per / 1024} KB");
        Assert.Equal(shown, songs.Songs);
        // Before: 7.2 ms, 423 KB per refresh (the whole list sorted again).
        Assert.True(per < 200 * 1024, $"{per / 1024} KB per refresh");

        // A list whose songs the download changed (a source, Downloaded) still sorts them in.
        var held = rig.Library.TrackList.Skip(100).Take(50).ToList();
        var downloaded = new SongListPageViewModel(rig.Shell, "Mobile.Downloaded", () => held);
        downloaded.ChooseSort(downloaded.SortChoices.First(c => Equals(c.Key, SongSort.Title)));
        var extra = MobileFixtures.Song("Aardvark", "aaaa");
        held.Add(extra);
        rig.Shell.Library.NotifyDownloadsChanged();
        Assert.Same(extra, downloaded.Songs[0]);
        Assert.Equal(51, downloaded.SongCount);
    }

    /// <summary>
    /// Search is a tab root, alive the whole session, and re-ran its query over the library on
    /// every library refresh, hearts and downloads included (and threw away its artist → cover
    /// map, which walks every song). Neither changes a result or an artist's cover.
    /// </summary>
    [Fact]
    public void HeartsAndDownloads_DoNotRerunAnOpenSearch()
    {
        using var rig = MobileSearchPerfTests.BigLibrary();
        var search = rig.Shell.Search;
        search.Query = "love";
        rig.Library.NotifyFavoritesChanged();
        rig.Shell.Library.NotifyDownloadsChanged();
        var runs = search.RunCount;
        var songs = search.Songs.ToList();

        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++)
        {
            rig.Library.TrackList[i].IsFavorite = !rig.Library.TrackList[i].IsFavorite;
            rig.Library.NotifyFavoritesChanged();
            rig.Shell.Library.NotifyDownloadsChanged();
        }
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 20;
        var per = (GC.GetAllocatedBytesForCurrentThread() - alloc) / 20;
        _out.WriteLine($"heart/download refresh with \"love\" searched, 12k songs: {ms:F1} ms, {per / 1024} KB, {search.RunCount - runs} searches");
        Assert.Equal(runs, search.RunCount);
        Assert.Equal(songs, search.Songs);
        // Before: a search each (3.1 ms, 209 KB a refresh, desktop).
        Assert.True(per < 100 * 1024, $"{per / 1024} KB per refresh");

        // A library update still searches again.
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(runs + 1, search.RunCount);
    }

    /// <summary>
    /// An artist page rebuilt itself on every library refresh: a heart on one of its songs (its
    /// menu, Now Playing) or a song finishing downloading replaced every rail and card with new
    /// lists, so every tile was built again and blanked its cover until the reload landed. A heart
    /// or a download changes nothing the page shows (Top Songs ranks by plays).
    /// </summary>
    [AvaloniaFact]
    public void ArtistPage_KeepsItsRailsAndTiles_WhenASongIsHeartedOrDownloaded()
    {
        using var rig = MobileSearchPerfTests.BigLibrary(4_800, 300);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var name = rig.Library.Albums[0].Artist;
        rig.Shell.OpenArtistCommand.Execute(name);
        MobileFixtures.Land(window);
        Dispatcher.UIThread.RunJobs();
        var page = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        var (topSongs, albums, cards) = (page.TopSongPages, page.Albums, page.Cards);
        var pageView = MobileFixtures.Find<ArtistPage>(view);
        var tiles = pageView.GetVisualDescendants().OfType<Button>().ToList();

        var song = page.Songs[0];
        song.IsFavorite = !song.IsFavorite;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        rig.Library.NotifyFavoritesChanged();
        rig.Shell.Library.NotifyDownloadsChanged();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        _out.WriteLine($"artist page, heart + download refresh: {ms:F1} ms, {(GC.GetAllocatedBytesForCurrentThread() - alloc) / 1024} KB");

        Assert.Same(topSongs, page.TopSongPages);
        Assert.Same(albums, page.Albums);
        Assert.Same(cards, page.Cards);
        var kept = pageView.GetVisualDescendants().OfType<Button>().ToList();
        Assert.True(tiles.SequenceEqual(kept, ReferenceEqualityComparer.Instance), "the page's buttons and tiles were rebuilt");

        // A library update (a rescan) still rebuilds it.
        rig.Library.RaiseLibraryUpdated();
        Assert.NotSame(albums, page.Albums);
        window.Close();
    }
}
