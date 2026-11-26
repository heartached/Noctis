using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// Discord (Halil): on Favorites "i can select rows … its the whole line everytime". The grid is
/// a ListBox whose items are ROWS of tiles; the row container must never look or be selected,
/// and the tiles select one by one as on the Albums page (Ctrl+click, Ctrl+A, Esc, click beside).
/// </summary>
public class FavoritesRowSelectionTests
{
    private readonly ITestOutputHelper _o;
    public FavoritesRowSelectionTests(ITestOutputHelper o) => _o = o;

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

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Pump(1);
            await Task.Delay(5);
        }
        Pump(4);
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static List<Button> Tiles(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();

    private static List<ListBoxItem> Rows(FavoritesView view) =>
        view.FindControl<ListBox>("FavoritesList")!.GetVisualDescendants().OfType<ListBoxItem>().ToList();

    private FakeAudioPlayer _audio = new();

    private async Task<(FavoritesView view, Window win)> Mount(int count)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < count; i++)
        {
            var t = new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", Album = $"Single {i}",
                AlbumArtist = "Artist", FilePath = $"/music/{i}.mp3", Duration = TimeSpan.FromMinutes(3),
                IsFavorite = true,
            };
            t.AlbumId = Guid.NewGuid();
            lib.TrackList.Add(t);
        }
        var persistence = new TestPersistenceService();
        _audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(_audio, lib, persistence, new FakeAnimatedCoverService());
        var vm = new FavoritesViewModel(player, lib, persistence, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        vm.Refresh();
        var view = new FavoritesView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();
        await PumpUntil(() => Tiles(view).Count > 0);
        return (view, win);
    }

    /// <summary>What the row container shows: selected, its classes, and the brushes it paints.</summary>
    private string Describe(ListBoxItem row)
    {
        var cp = row.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
        static string B(IBrush? b) => b is ISolidColorBrush s ? s.Color.ToString() : b?.ToString() ?? "null";
        return $"selected={row.IsSelected} classes=[{string.Join(",", row.Classes)}] " +
               $"bg={B(row.Background)} cpBg={B(cp?.Background)} cpBorder={B(cp?.BorderBrush)} focusAdorner={row.FocusAdorner != null}";
    }

    /// <summary>The row paints nothing of its own and is not selected.</summary>
    private void AssertRowsLookPlain(FavoritesView view, string after)
    {
        foreach (var row in Rows(view))
        {
            _o.WriteLine($"{after}: {Describe(row)}");
            Assert.False(row.IsSelected, $"{after}: a row of tiles is selected");
            Assert.False(row.Classes.Contains("ctrl-selected"), $"{after}: a row of tiles carries ctrl-selected");
            var cp = row.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
            if (cp?.Background is ISolidColorBrush s)
                Assert.True(s.Color.A == 0, $"{after}: row paints {s.Color}");
        }
    }

    private static void Click(Window win, Point p, RawInputModifiers mods = RawInputModifiers.None)
    {
        win.MouseDown(p, MouseButton.Left, mods);
        win.MouseUp(p, MouseButton.Left, mods);
        Pump(2);
    }

    [AvaloniaFact]
    public async Task PressesBesideTheTiles_NeverSelectTheRow()
    {
        var (view, win) = await Mount(3); // one partial row: empty grid to its right and below
        var tiles = Tiles(view);
        var last = tiles[^1];
        // Right of the last tile, still inside the row (the row stretches the full width).
        var besideInRow = last.TranslatePoint(new Point(last.Bounds.Width + 40, last.Bounds.Height / 2), win)!.Value;
        // The gap between two tiles (their margin, outside both buttons).
        var gap = tiles[0].TranslatePoint(new Point(tiles[0].Bounds.Width + 2, tiles[0].Bounds.Height / 2), win)!.Value;
        foreach (var (where, p) in new[] { ("beside, in the row", besideInRow), ("in the gap", gap) })
            foreach (var mods in new[] { RawInputModifiers.None, RawInputModifiers.Control, RawInputModifiers.Shift })
            {
                Click(win, p, mods);
                AssertRowsLookPlain(view, $"{mods} click {where}");
            }
        win.Close();
    }

    /// <summary>Discord 11-26: holding a press on a tile painted its whole row grey (the row's
    /// :pressed), and the tile shrank to 96% and back. Neither, through a right-click too.</summary>
    [AvaloniaFact]
    public async Task PressOnATile_PaintsNoRow_AndTheTileKeepsItsSize()
    {
        var (view, win) = await Mount(6);
        var tile = Tiles(view)[1];
        var p = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 3), win)!.Value;
        win.MouseMove(p); Pump(2);
        win.MouseDown(p, MouseButton.Right); Pump(2);
        win.MouseUp(p, MouseButton.Right); Pump(2);
        AssertRowsLookPlain(view, "right-click");
        win.MouseDown(p, MouseButton.Left); Pump(2);
        AssertRowsLookPlain(view, "left press");
        Assert.Equal(1, tile.RenderTransform?.Value.M11 ?? 1, 3);
        win.MouseUp(p, MouseButton.Left); Pump(2);
        AssertRowsLookPlain(view, "release");
        win.Close();
    }

    [AvaloniaFact]
    public async Task ArrowKeysAndSpace_NeverSelectTheRow()
    {
        var (view, win) = await Mount(20);
        var list = view.FindControl<ListBox>("FavoritesList")!;
        list.Focus();
        Pump(2);
        foreach (var key in new[] { PhysicalKey.ArrowDown, PhysicalKey.ArrowDown, PhysicalKey.Space, PhysicalKey.Home, PhysicalKey.End })
        {
            win.KeyPressQwerty(key, RawInputModifiers.None);
            Pump(2);
            AssertRowsLookPlain(view, key.ToString());
        }
        win.Close();
    }

    [AvaloniaFact]
    public async Task Tiles_SelectOneByOne_LikeTheAlbumsPage()
    {
        var (view, win) = await Mount(20);
        var tiles = Tiles(view);
        Point Centre(Button t) => t.TranslatePoint(new Point(t.Bounds.Width / 2, t.Bounds.Height / 2), win)!.Value;

        // Ctrl+click one tile: that tile only.
        Click(win, Centre(tiles[1]), RawInputModifiers.Control);
        Assert.True(tiles[1].Classes.Contains("ctrl-selected"));
        Assert.Equal(1, Tiles(view).Count(t => t.Classes.Contains("ctrl-selected")));
        AssertRowsLookPlain(view, "ctrl+click");

        // Ctrl+A: every tile, no row.
        win.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Pump(2);
        Assert.All(Tiles(view), t => Assert.True(t.Classes.Contains("ctrl-selected")));
        AssertRowsLookPlain(view, "ctrl+a");

        // Escape clears.
        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump(2);
        Assert.All(Tiles(view), t => Assert.False(t.Classes.Contains("ctrl-selected")));

        win.Close();
    }

    [AvaloniaFact]
    public async Task ClickBesideTheTiles_ClearsTheSelection_AndOpensNothing()
    {
        var (view, win) = await Mount(3);
        var tiles = Tiles(view);
        Point Centre(Button t) => t.TranslatePoint(new Point(t.Bounds.Width / 2, t.Bounds.Height / 2), win)!.Value;

        // Below the only row (GitHub #209, as on Albums).
        Click(win, Centre(tiles[0]), RawInputModifiers.Control);
        Assert.True(tiles[0].Classes.Contains("ctrl-selected"));
        Click(win, new Point(700, 820));
        Assert.All(Tiles(view), t => Assert.False(t.Classes.Contains("ctrl-selected")));

        // Beside the last tile, inside the row.
        Click(win, Centre(tiles[1]), RawInputModifiers.Control);
        var last = tiles[^1];
        Click(win, last.TranslatePoint(new Point(last.Bounds.Width + 40, last.Bounds.Height / 2), win)!.Value);
        Assert.All(Tiles(view), t => Assert.False(t.Classes.Contains("ctrl-selected")));

        // In a tile's own padding (the gap around its cover): clears, and does not play that song.
        Click(win, Centre(tiles[1]), RawInputModifiers.Control);
        Click(win, tiles[2].TranslatePoint(new Point(2, tiles[2].Bounds.Height / 2), win)!.Value);
        Assert.All(Tiles(view), t => Assert.False(t.Classes.Contains("ctrl-selected")));
        Assert.Empty(_audio.PlayedPaths);
        AssertRowsLookPlain(view, "click beside after a selection");
        win.Close();
    }
}
