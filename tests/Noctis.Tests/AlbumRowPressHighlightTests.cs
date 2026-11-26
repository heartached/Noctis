using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-26: "when i right click to open the menu options and then i click to open a
/// track/album, it highlights that whole row" and "when i click inside of tracks/albums the
/// artwork flickers for a split second". A left press on a tile set :pressed on its grid row
/// (ListBoxItem, PressedMixin) and Fluent painted the whole row grey; the tile itself snapped
/// to 96% (Button:pressed) and back on the release. Real pointer events on the Albums grid:
/// no row paints or is selected, the tile keeps its size and its cover (same bitmap, never
/// blank) through a right-click, the menu, and the click that opens the album.
/// </summary>
[Collection("ArtworkCache")]
public class AlbumRowPressHighlightTests : IDisposable
{
    private readonly ITestOutputHelper _o;

    public AlbumRowPressHighlightTests(ITestOutputHelper o)
    {
        _o = o;
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
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

    private static string B(IBrush? b) => b is ISolidColorBrush s ? s.Color.ToString() : b?.ToString() ?? "null";

    private static ContentPresenter? RowPresenter(ListBoxItem row) =>
        row.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter" && ReferenceEquals(p.TemplatedParent, row));

    /// <summary>No grid row is selected or paints anything of its own.</summary>
    private void AssertRowsPlain(ListBox grid, string after)
    {
        foreach (var row in grid.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var cp = RowPresenter(row);
            _o.WriteLine($"{after}: selected={row.IsSelected} classes=[{string.Join(",", row.Classes)}] cpBg={B(cp?.Background)}");
            Assert.False(row.IsSelected, $"{after}: a row of albums is selected");
            Assert.Null(grid.SelectedItem);
            if (cp?.Background is ISolidColorBrush s)
                Assert.True(s.Color.A == 0, $"{after}: the row of albums paints {s.Color} ({string.Join(",", row.Classes)})");
        }
    }

    private static double Scale(Visual v) => v.RenderTransform?.Value.M11 ?? 1;

    [AvaloniaFact]
    public async Task RightClickThenClick_NoRowHighlight_CoverStaysPut()
    {
        EnsureAppStyles();
        ArtworkCache.DecoderOverride = (path, width) =>
            new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var lib = new FakeLibraryService();
        for (var i = 0; i < 20; i++)
            ((List<Album>)lib.Albums).Add(new Album
            {
                Id = Guid.NewGuid(), Name = $"Album {i:00}", Artist = "Artist", Year = 2020,
                ArtworkPath = System.IO.Path.Combine("C:\\", "art", $"press-{i}.jpg"), Tracks = new List<Track>(),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        vm.AlbumSortMode = "title";
        Album? opened = null;
        vm.AlbumOpened += (_, a) => opened = a;
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;

        List<Button> Tiles() => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        CachedImage Cover(Button t) => t.GetVisualDescendants().OfType<CachedImage>().First();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && (Tiles().Count < 10 || Tiles().Any(t => Cover(t).Source is null)))
        {
            Pump(1);
            await Task.Delay(5);
        }
        Pump(4);
        var grid = view.FindControl<ListBox>("AlbumListBox")!;
        var tile = Tiles()[2];
        var cover = Cover(tile);
        var bitmap = cover.Source;
        Assert.NotNull(bitmap);
        var swaps = new List<string>();
        cover.PropertyChanged += (_, e) =>
        {
            if (e.Property == Image.SourceProperty) swaps.Add($"{e.OldValue?.GetHashCode()} -> {e.NewValue?.GetHashCode().ToString() ?? "null"}");
        };
        AssertRowsPlain(grid, "start");

        var p = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, 40), win)!.Value;
        win.MouseMove(p); Pump(2);

        // Right-click: the album menu opens; no row is marked.
        win.MouseDown(p, MouseButton.Right); Pump(2);
        AssertRowsPlain(grid, "right press");
        win.MouseUp(p, MouseButton.Right); Pump(2);
        Assert.True(tile.ContextMenu?.IsOpen, "the album menu did not open");
        AssertRowsPlain(grid, "menu open");

        // Click the album with the menu still open (it light-dismisses and passes the press on).
        win.MouseDown(p, MouseButton.Left); Pump(2);
        AssertRowsPlain(grid, "left press");
        Assert.Equal(1, Scale(tile), 3);
        await Task.Delay(120); Pump(3);
        Assert.Equal(1, Scale(tile), 3);
        AssertRowsPlain(grid, "left press held");
        win.MouseUp(p, MouseButton.Left); Pump(2);
        AssertRowsPlain(grid, "release");

        // A second plain click (menu closed by now): the same.
        win.MouseDown(p, MouseButton.Left); Pump(2);
        AssertRowsPlain(grid, "second press");
        Assert.Equal(1, Scale(tile), 3);
        win.MouseUp(p, MouseButton.Left); Pump(2);
        AssertRowsPlain(grid, "second release");
        Assert.Same(tile.DataContext, opened);

        // The cover never blanked or changed bitmap on the way.
        foreach (var s in swaps) _o.WriteLine("Source " + s);
        Assert.Empty(swaps);
        Assert.Same(bitmap, cover.Source);
        win.Close();
    }
}
