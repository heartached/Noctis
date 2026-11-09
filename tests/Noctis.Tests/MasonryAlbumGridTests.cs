using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #213: the Albums grid's optional Masonry layout. Cards go to the shortest column, a
/// column's cards keep one gap between them whatever their caption height, only the cards
/// near the viewport exist (a library of thousands keeps a few dozen tiles), scrolling
/// realizes the rest, and the Ctrl-selection follows albums onto recycled tiles.
/// </summary>
public class MasonryAlbumGridTests
{
    private readonly ITestOutputHelper _o;
    public MasonryAlbumGridTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Place_PutsEachCardUnderTheShortestColumn()
    {
        var heights = new double[] { 100, 120, 100, 50, 60, 70 };
        var col = new int[6]; var top = new double[6];
        var total = MasonryLayout.Place(heights, 3, col, top);
        // Row one fills left to right; then the shortest columns take the next cards.
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 1 }, col);
        Assert.Equal(new double[] { 0, 0, 0, 100, 100, 120 }, top);
        Assert.Equal(190, total);
    }

    [Fact]
    public void Visible_FindsOnlyTheCardsInTheBand()
    {
        var heights = Enumerable.Repeat(100.0, 3000).ToArray();
        var col = new int[3000]; var top = new double[3000];
        MasonryLayout.Place(heights, 5, col, top);
        var found = new List<int>();
        MasonryLayout.Visible(heights, col, top, 5, 1000, 1300, found);
        // Rows 10, 11 and 12 (tops 1000, 1100, 1200), five cards each.
        Assert.Equal(Enumerable.Range(50, 15), found);
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static List<Button> RealizedTiles(MasonryAlbumPanel panel) =>
        panel.RealizedElements.OfType<Button>().Where(b => b.IsVisible).ToList();

    [AvaloniaFact]
    public async Task Masonry_StacksByColumn_StaysVirtualized_AndKeepsTheSelection()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var i = 0; i < 3000; i++)
            albums.Add(new Album
            {
                Id = Guid.NewGuid(),
                Name = $"{i:0000} " + (i % 4 == 1 ? "a really long album title that has to wrap onto a second line" : "album"),
                Artist = "Artist", Year = 2020, Tracks = new List<Track>(),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        settings.AlbumGridMasonry = true;
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        var panel = view.FindControl<MasonryAlbumPanel>("MasonryPanel")!;
        var deadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < deadline && RealizedTiles(panel).Count == 0) { Pump(1); await Task.Delay(5); }
        Pump(6);

        Assert.True(vm.IsMasonryView);
        Assert.False(view.AlbumListBox.IsVisible);
        var tiles = RealizedTiles(panel);
        _o.WriteLine($"realized {tiles.Count} of {vm.CarouselAlbums.Count}; panel h={panel.Bounds.Height:0}");
        Assert.InRange(tiles.Count, vm.GridColumns, 120);
        Assert.True(panel.Bounds.Height > 3000 / vm.GridColumns * vm.TileArtworkSize, "the panel claims the whole stack");

        // Within a column, each card starts one fixed gap below the previous one's caption,
        // whatever that caption's height (no row alignment).
        Rect Cover(Button t)
        {
            var c = t.GetVisualDescendants().OfType<Border>().First(b => b.ClipToBounds && b.Width == vm.TileArtworkSize);
            return new Rect(c.TranslatePoint(default, panel)!.Value, c.Bounds.Size);
        }
        double CaptionBottom(Button t)
        {
            var text = t.GetVisualDescendants().OfType<StackPanel>().First(s => s.Classes.Contains("tile-text"));
            var last = (Control)text.Children[^1];
            return last.TranslatePoint(new Point(0, last.Bounds.Height), panel)!.Value.Y;
        }
        var byColumn = tiles.GroupBy(t => Math.Round(Cover(t).X)).ToList();
        Assert.Equal(vm.GridColumns, byColumn.Count);
        var gaps = new List<double>();
        foreach (var column in byColumn)
        {
            var ordered = column.OrderBy(t => Cover(t).Y).ToList();
            for (var k = 1; k < ordered.Count; k++)
                gaps.Add(Cover(ordered[k]).Y - CaptionBottom(ordered[k - 1]));
        }
        _o.WriteLine("column gaps: " + string.Join(" ", gaps.Distinct().Select(g => g.ToString("0.#"))));
        Assert.All(gaps, g => Assert.InRange(g, 17.5, 18.5));
        // Not a row grid: the second cards' tops differ between columns (one-line vs two-line).
        var secondTops = byColumn.Select(c => c.OrderBy(t => Cover(t).Y).Skip(1).Select(t => Cover(t).Y).FirstOrDefault()).Distinct().Count();
        Assert.True(secondTops > 1, "columns should not line up like rows");

        // Ctrl+A, then scroll far down: the tiles that come into view are new albums on
        // recycled tiles and must show the selection, at once.
        win.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); Pump(2);
        var sv = view.FindControl<ScrollViewer>("MasonryScroller")!;
        var sw = Stopwatch.StartNew();
        sv.Offset = new Vector(0, panel.Bounds.Height / 2);
        Pump(4);
        _o.WriteLine($"scroll to middle: {sw.ElapsedMilliseconds} ms");
        var after = RealizedTiles(panel);
        Assert.InRange(after.Count, vm.GridColumns, 120);
        var names = after.Select(t => ((Album)t.DataContext!).Name).ToList();
        Assert.DoesNotContain(names, n => n.StartsWith("0000"));
        Assert.All(after, t => Assert.True(t.Classes.Contains("ctrl-selected")));
        // And the tile count in the visual tree stays bounded (pooled, not one per album).
        Assert.InRange(panel.Children.Count, vm.GridColumns, 200);
        win.Close();
    }
}
