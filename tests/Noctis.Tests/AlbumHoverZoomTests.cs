using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #218: Settings › Appearance "Album Hover Zoom" (off by default). With it on, the
/// album under the pointer in the Albums grid grows a little with a glow and the tiles around
/// it step back; render transforms only, so no tile moves in layout. With it off, nothing.
/// </summary>
public class AlbumHoverZoomTests
{
    private readonly ITestOutputHelper _o;
    public AlbumHoverZoomTests(ITestOutputHelper o) => _o = o;

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

    private static double Scale(Visual v) => v.RenderTransform is TransformOperations ops ? ops.Value.M11 : v.RenderTransform?.Value.M11 ?? 1;

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HoveredAlbum_GrowsAndGlows_NeighboursStepBack_OnlyWhenOn(bool on)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 30; i++)
            ((List<Album>)lib.Albums).Add(new Album { Id = Guid.NewGuid(), Name = $"Album {i:00}", Artist = "Artist", Year = 2020, Tracks = new List<Track>() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        Assert.False(settings.AlbumHoverZoom); // off by default
        settings.AlbumHoverZoom = on;
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 1400, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        List<Button> Tiles() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && Tiles().Count < 15) { Pump(1); await Task.Delay(5); }
        Pump(4);

        var tiles = Tiles().OrderBy(t => t.TranslatePoint(default, view)!.Value.Y).ThenBy(t => t.TranslatePoint(default, view)!.Value.X).ToList();
        var cols = vm.GridColumns;
        var hovered = tiles[cols + 2];           // second row, middle
        var near = tiles[cols + 1];              // left neighbour
        var far = tiles[3 * cols + 2 < tiles.Count ? 3 * cols + 2 : tiles.Count - 1]; // two rows down
        var bounds = tiles.Select(t => t.Bounds).ToList();

        var p = hovered.TranslatePoint(new Point(hovered.Bounds.Width / 2, 40), win)!.Value;
        win.MouseMove(p); Pump(2);
        await Task.Delay(350); Pump(3);

        var glow = hovered.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Glow");
        _o.WriteLine($"on={on}: hovered scale={Scale(hovered):0.###} glow={glow.Opacity:0.##} near={Scale(near):0.###} ({near.Classes.Contains("hover-neighbor")}) far={Scale(far):0.###}");
        if (on)
        {
            Assert.Equal(1.04, Scale(hovered), 2);
            Assert.Equal(1, glow.Opacity, 2);
            Assert.True(near.Classes.Contains("hover-neighbor"));
            Assert.Equal(0.97, Scale(near), 2);
            Assert.False(far.Classes.Contains("hover-neighbor"));
            Assert.Equal(1, Scale(far), 2);
        }
        else
        {
            Assert.Equal(1, Scale(hovered), 2);
            Assert.Equal(0, glow.Opacity, 2);
            Assert.False(near.Classes.Contains("hover-neighbor"));
        }
        // Render transforms only: nothing moved in layout.
        Assert.Equal(bounds, tiles.Select(t => t.Bounds).ToList());

        // Leaving the grid lets everything settle back.
        win.MouseMove(new Point(5, 1395)); Pump(2);
        await Task.Delay(350); Pump(3);
        Assert.Equal(1, Scale(hovered), 2);
        Assert.Equal(1, Scale(near), 2);
        Assert.False(near.Classes.Contains("hover-neighbor"));
        win.Close();
    }
}
