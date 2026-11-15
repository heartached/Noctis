using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Artist page grids (09-14): tile pitch, gaps and label overflow in the Singles &amp; EPs tab; song-row art geometry vs Home's chart row.</summary>
public class ArtistGridSpacingProbeTests
{
    private readonly ITestOutputHelper _o;
    public ArtistGridSpacingProbeTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }
    private static void Pump(int n = 4) { for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }

    private static Album Single(string name, int year, int plays)
    {
        var a = new Album { Id = Guid.NewGuid(), Name = name, Artist = "Chase Atlantic", Year = year };
        var t = new Track { Id = Guid.NewGuid(), Title = name.Replace(" - Single", ""), Artist = "Chase Atlantic", AlbumArtist = "Chase Atlantic", Album = name, AlbumId = a.Id, Year = year, PlayCount = plays, Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/" + name + ".mp3" };
        a.Tracks = new List<Track> { t };
        return a;
    }

    [AvaloniaFact]
    public void SinglesTab_TilePitch_Gaps_And_LabelOverflow()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = new List<Album>
        {
            Single("REMIND ME - Single", 2025, 5), Single("FACEDOWN - Single", 2025, 4), Single("DIE FOR ME - Single", 2024, 3),
            Single("MAMACITA - Single", 2023, 2), Single("ESCORT - Single", 2021, 1), Single("OHMAMI (With Maggie Lindemann) - Single", 2021, 1),
            Single("OHMAMI - Single", 2021, 1), Single("MOLLY - Single", 2020, 1), Single("OUT THE ROOF - Single", 2020, 1),
            Single("Hit My Line - Single", 2020, 1), Single("Too Late (Billy Martin Remix) - Single", 2019, 1), Single("DON'T TRY THIS - EP", 2019, 1),
        };
        foreach (var a in albums) { lib.TrackList.AddRange(a.Tracks); ((List<Album>)lib.Albums).Add(a); }
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump(6);
        vm.SelectTabCommand.Execute("singles"); Pump(8);

        var grid = view.GetVisualDescendants().OfType<ItemsControl>().First(i => ReferenceEquals(i.ItemsSource, vm.SingleReleases));
        var tiles = grid.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        _o.WriteLine($"tiles={tiles.Count} TileArtworkSize={vm.TileArtworkSize:0.#} TileHeight={vm.TileHeight:0.#} cols={vm.GridColumns} gridWidth={grid.Bounds.Width:0.#}");
        var rows = tiles.GroupBy(t => Math.Round(t.TranslatePoint(new Point(0, 0), grid)!.Value.Y)).OrderBy(g => g.Key).ToList();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(t => t.Bounds.X).ToList();
            var xs = ordered.Select(t => t.TranslatePoint(new Point(0, 0), grid)!.Value.X).ToList();
            var gaps = xs.Zip(xs.Skip(1), (a, b) => b - a - ordered[0].Bounds.Width).Select(g => g.ToString("0.#"));
            _o.WriteLine($"row y={row.Key}: {ordered.Count} tiles, x0={xs[0]:0.#} w={ordered[0].Bounds.Width:0.#} h={ordered[0].Bounds.Height:0.#} gaps=[{string.Join(",", gaps)}] rightSlack={grid.Bounds.Width - (xs.Last() + ordered.Last().Bounds.Width):0.#}");
        }
        foreach (var tile in tiles.Take(8))
        {
            var album = (Album)tile.DataContext!;
            var label = tile.GetVisualDescendants().OfType<StackPanel>().First(s => s.Classes.Contains("tile-text"));
            var lb = label.TranslatePoint(new Point(0, label.Bounds.Height), tile)!.Value.Y;
            var title = label.GetVisualDescendants().OfType<TextBlock>().First();
            var g = tile.GetVisualDescendants().OfType<Grid>().First();
            var cover = tile.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Count == 0 && b.CornerRadius.TopLeft == 6);
            _o.WriteLine($"  '{album.Name}': tile {tile.Bounds.Width:0.#}x{tile.Bounds.Height:0.#} grid Width={g.Width:0.#} bounds={g.Bounds.Width:0.#}x{g.Bounds.Height:0.#} cover Width={cover.Width:0.#} bounds={cover.Bounds.Width:0.#} labelBottom={lb:0.#} overflow={lb - tile.Bounds.Height:0.#} titleLines={title.TextLayout.TextLines.Count}");
        }
        // Pins (09-14): Home's column count in Auto, tiles never clipped by their cells
        // (the tab grids used a hard-coded 8-column UniformGrid), 8px cover-to-cover gap
        // as on Home and Albums (AlbumGridMetrics.ClassicTileChrome: 4px padding per side, 1.6.9).
        Assert.Equal(AlbumGridMetrics.ClassicColumns, vm.GridColumns);
        Assert.Equal(vm.GridColumns, rows[0].Count());
        foreach (var tile in tiles)
        {
            var g = tile.GetVisualDescendants().OfType<Grid>().First();
            Assert.True(tile.Bounds.Width >= g.Bounds.Width + 4 - 0.5, $"tile {tile.Bounds.Width} clips its {g.Bounds.Width} content");
        }
        var first = rows[0].OrderBy(t => t.Bounds.X).Take(2).ToList();
        var c0 = first[0].GetVisualDescendants().OfType<Border>().First(b => b.Classes.Count == 0 && b.CornerRadius.TopLeft == 6);
        var c1 = first[1].GetVisualDescendants().OfType<Border>().First(b => b.Classes.Count == 0 && b.CornerRadius.TopLeft == 6);
        var coverGap = c1.TranslatePoint(new Point(0, 0), grid)!.Value.X - (c0.TranslatePoint(new Point(0, 0), grid)!.Value.X + c0.Bounds.Width);
        _o.WriteLine($"cover-to-cover gap={coverGap:0.#}");
        Assert.InRange(coverGap, 7.5, 9.5);
        win.Close();
    }

    [AvaloniaFact]
    public void SongRow_ArtGeometry()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var a = Single("PHASES", 2019, 9);
        lib.TrackList.AddRange(a.Tracks); ((List<Album>)lib.Albums).Add(a);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump(6);
        var row = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("song-row"));
        var art = row.GetVisualDescendants().OfType<Border>().First(b => b.Width == b.Height && b.Width >= 30 && b.Width <= 56);
        var ap = art.TranslatePoint(new Point(0, 0), row)!.Value;
        var title = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.FontSize == 13);
        var tp = title.TranslatePoint(new Point(0, 0), row)!.Value;
        _o.WriteLine($"row h={row.Bounds.Height:0.#} padding={row.Padding} | art at x={ap.X:0.#} y={ap.Y:0.#} size={art.Width} radius={art.CornerRadius} | title x={tp.X:0.#}");
        // Home's chart row: 48 tall, rank column 40, 36px art (radius 4) in a 40 column, title 14 in.
        Assert.Equal(48, row.Bounds.Height, 1);
        Assert.Equal(36, art.Width); Assert.Equal(4, art.CornerRadius.TopLeft);
        Assert.Equal(42, ap.X, 1); Assert.Equal(6, ap.Y, 1);
        Assert.Equal(94, tp.X, 1);
        win.Close();
    }
}
