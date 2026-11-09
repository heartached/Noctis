using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #210: the Albums grid keeps one gap everywhere. Rows used to share a fixed height
/// (cover + 64), so a row whose titles all fit on one line had a 17px taller gap below it
/// than a row with a wrapped title; the gap between covers was 8px against ~10-27px between
/// rows; and the grid sat 4px closer to the top than to the left.
/// </summary>
public class AlbumGridSpacingTests
{
    private readonly ITestOutputHelper _o;
    public AlbumGridSpacingTests(ITestOutputHelper o) => _o = o;

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

    [AvaloniaFact]
    public async Task AlbumsGrid_GapsMatch_AcrossColumnsRowsAndCaptionHeights()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        // Sorted by title: row 1 all one-line, row 2 with one wrapped title, row 3 one-line.
        for (var i = 0; i < 15; i++)
            albums.Add(new Album
            {
                Id = Guid.NewGuid(),
                Name = $"{(char)('A' + i)}" + (i == 6 ? " a really long album title that has to wrap onto a second line" : " album"),
                Artist = "Artist", Year = 2020, Tracks = new List<Track>(),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 1400, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("album-tile")) < 15)
        { Pump(1); await Task.Delay(5); }
        Pump(6);

        var tiles = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile"))
            .OrderBy(b => b.TranslatePoint(default, view)!.Value.Y).ThenBy(b => b.TranslatePoint(default, view)!.Value.X).ToList();
        Assert.Equal(15, tiles.Count);
        Rect Cover(Button t)
        {
            var c = t.GetVisualDescendants().OfType<Border>().First(b => b.ClipToBounds && b.Width == vm.TileArtworkSize);
            return new Rect(c.TranslatePoint(default, view)!.Value, c.Bounds.Size);
        }
        double CaptionBottom(Button t)
        {
            var text = t.GetVisualDescendants().OfType<StackPanel>().First(s => s.Classes.Contains("tile-text"));
            // The cover is inside tile-text; its last child is the subtitle line.
            var last = (Control)text.Children[^1];
            return last.TranslatePoint(new Point(0, last.Bounds.Height), view)!.Value.Y;
        }
        var cols = vm.GridColumns;
        var hGap = Cover(tiles[1]).X - Cover(tiles[0]).Right;
        var gap1 = Cover(tiles[cols]).Y - tiles.Take(cols).Max(CaptionBottom);
        var gap2 = Cover(tiles[2 * cols]).Y - tiles.Skip(cols).Take(cols).Max(CaptionBottom);
        var left = Cover(tiles[0]).X - view.TranslatePoint(default, view)!.Value.X;
        var top = Cover(tiles[0]).Y;
        var lbi = tiles[0].FindAncestorOfType<ListBoxItem>()!;
        _o.WriteLine($"lbi margin={lbi.Margin} padding={lbi.Padding} h={lbi.Bounds.Height} tile h={tiles[0].Bounds.Height} margin={tiles[0].Margin} pad={tiles[0].Padding}");
        var sv = view.AlbumListBox.FindDescendantOfType<ScrollViewer>()!;
        var ic = tiles[0].FindAncestorOfType<ItemsControl>()!;
        _o.WriteLine($"view w={view.Bounds.Width} lb w={view.AlbumListBox.Bounds.Width} sv w={sv.Bounds.Width} vp={sv.Viewport.Width} ic w={ic.Bounds.Width} art={vm.TileArtworkSize}");
        _o.WriteLine($"cols={cols} hGap={hGap:0.##} rowGap(one-line row)={gap1:0.##} rowGap(wrapped row)={gap2:0.##} left={left:0.##} top={top:0.##}");

        // Layout rounding snaps covers and cells to whole pixels: allow one pixel either way.
        Assert.InRange(hGap, 16.9, 19.1);
        Assert.InRange(gap1, 17.5, 18.5);
        Assert.InRange(gap2, 17.5, 18.5);
        Assert.InRange(top - left, -0.5, 0.5);
        win.Close();
    }
}
