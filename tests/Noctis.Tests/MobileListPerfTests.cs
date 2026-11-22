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
}
