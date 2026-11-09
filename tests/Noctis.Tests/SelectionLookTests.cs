using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #209: one Ctrl-selection look (an accent plate that fades in and out on a template
/// overlay) for album tiles and track rows; selecting never changes a tile's layout (the old
/// 2px border widened Home tiles and wrapped the last column); a click beside the items
/// clears the selection; Ctrl+A does nothing on Home; and containers realized on scroll take
/// their state without a fade.
/// </summary>
public class SelectionLookTests
{
    private readonly ITestOutputHelper _o;
    public SelectionLookTests(ITestOutputHelper o) => _o = o;

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

    private static Border Ring(Control owner) =>
        owner.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_SelectRing");

    private static List<Button> Tiles(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();

    private static async Task<(LibraryAlbumsViewModel vm, LibraryAlbumsView view, Window win)> MountAlbums(int count = 40)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var i = 0; i < count; i++)
            albums.Add(new Album
            {
                Id = Guid.NewGuid(),
                // Every third title wraps to a second line.
                Name = i % 3 == 0 ? $"A rather long album title number {i} that wraps" : $"Album {i}",
                Artist = "Artist", Year = 2020, Tracks = new List<Track>(),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();
        vm.IsActive = true;
        await PumpUntil(() => Tiles(view).Count > 0);
        return (vm, view, win);
    }

    private static void CtrlA(Window win)
    {
        win.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Pump(2);
    }

    [AvaloniaFact]
    public async Task AlbumTiles_CtrlA_FadesThePlateIn_WithoutMovingAnything()
    {
        var (_, view, win) = await MountAlbums();
        var tile = Tiles(view)[1];
        var cover = tile.GetVisualDescendants().OfType<Border>().First(b => b.CornerRadius.TopLeft == 6 && b.ClipToBounds);
        var before = (tile.Bounds, cover.TranslatePoint(default, view)!.Value);
        var ring = Ring(tile);
        Assert.Equal(0, ring.Opacity);

        CtrlA(win);
        Assert.True(tile.Classes.Contains("ctrl-selected"));
        var samples = new List<double>();
        for (var i = 0; i < 20 && ring.Opacity < 1; i++) { samples.Add(ring.Opacity); await Task.Delay(16); Pump(1); }
        await Task.Delay(250); Pump(2);
        _o.WriteLine($"fade-in: {string.Join(" ", samples.Select(o => o.ToString("0.##")))} -> {ring.Opacity}");
        Assert.Equal(1, ring.Opacity, 2);
        Assert.Contains(samples, o => o < 1); // it eased in rather than snapping on the same frame

        // Selection is an overlay: the tile and its cover are exactly where they were.
        Assert.Equal(before.Bounds, tile.Bounds);
        Assert.Equal(before.Item2, cover.TranslatePoint(default, view)!.Value);

        // And out again (Ctrl+A toggles).
        CtrlA(win);
        Assert.False(tile.Classes.Contains("ctrl-selected"));
        Assert.True(ring.Opacity > 0, "deselect should fade, not snap");
        await Task.Delay(300); Pump(2);
        Assert.Equal(0, ring.Opacity, 2);
        win.Close();
    }

    [AvaloniaFact]
    public async Task AlbumTiles_SelectedPlatesInOneRow_ShareOneHeight()
    {
        var (vm, view, win) = await MountAlbums();
        CtrlA(win);
        var firstRow = Tiles(view).Take(vm.GridColumns).ToList();
        var heights = firstRow.Select(t => Ring(t).Bounds.Height).ToList();
        _o.WriteLine("plate heights: " + string.Join(", ", heights));
        Assert.True(heights.Max() - heights.Min() < 0.5, string.Join(",", heights));
        win.Close();
    }

    [AvaloniaFact]
    public async Task Albums_ClickBesideTheTiles_ClearsTheSelection()
    {
        var (_, view, win) = await MountAlbums(6); // two rows, lots of empty grid below
        CtrlA(win);
        Assert.All(Tiles(view), t => Assert.True(t.Classes.Contains("ctrl-selected")));

        // A Ctrl press on empty space leaves it alone.
        var empty = new Point(700, 820);
        win.MouseDown(empty, MouseButton.Left, RawInputModifiers.Control); win.MouseUp(empty, MouseButton.Left, RawInputModifiers.Control); Pump(2);
        Assert.All(Tiles(view), t => Assert.True(t.Classes.Contains("ctrl-selected")));

        win.MouseDown(empty, MouseButton.Left); win.MouseUp(empty, MouseButton.Left); Pump(2);
        Assert.All(Tiles(view), t => Assert.False(t.Classes.Contains("ctrl-selected")));

        // Escape clears too.
        CtrlA(win);
        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(2);
        Assert.All(Tiles(view), t => Assert.False(t.Classes.Contains("ctrl-selected")));
        win.Close();
    }

    [AvaloniaFact]
    public async Task RealizedContainer_TakesItsSelection_WithoutAFade()
    {
        var (_, view, win) = await MountAlbums();
        var tile = Tiles(view)[0];
        var ring = Ring(tile);
        MultiSelectHelper.SetSelectedVisual(tile, true, animate: false);
        Pump(1);
        Assert.Equal(1, ring.Opacity, 3);
        MultiSelectHelper.SetSelectedVisual(tile, false, animate: false);
        Pump(1);
        Assert.Equal(0, ring.Opacity, 3);
        // The transitions are back for the next user action.
        Assert.NotEmpty(ring.Transitions!);
        win.Close();
    }

    private static async Task<(HomeViewModel vm, HomeView view, Window win)> MountHome()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            var t = new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", AlbumArtist = "Artist", Album = $"Album {i}", AlbumId = id, Duration = TimeSpan.FromSeconds(176), PlayCount = 3, FilePath = $"C:\\m\\{i}.mp3" };
            lib.TrackList.Add(t);
            ((List<Album>)lib.Albums).Add(new Album { Id = id, Name = t.Album, Artist = t.Artist, Year = 2017, Tracks = new List<Track> { t } });
            player.History.Add(t);
        }
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();
        var view = new HomeView { DataContext = vm };
        // Tall enough that the album rows sit on screen for the pointer.
        var win = new Window { Width = 1400, Height = 3200, Content = view };
        win.Show(); Pump();
        return (vm, view, win);
    }

    [AvaloniaFact]
    public async Task Home_SelectingTiles_KeepsEveryTileInItsRow_AndCtrlADoesNothing()
    {
        var (_, view, win) = await MountHome();
        var row = view.FindControl<ItemsControl>("AlbumsRow")!;
        var tiles = Tiles(row);
        Assert.True(tiles.Count >= 5, $"tiles={tiles.Count}");
        var tops = tiles.Select(t => t.Bounds.Y).ToList();

        CtrlA(win);
        Assert.DoesNotContain(Tiles(view), t => t.Classes.Contains("ctrl-selected"));

        foreach (var t in tiles)
        {
            var p = t.TranslatePoint(new Point(20, 20), win)!.Value;
            win.MouseDown(p, MouseButton.Left, RawInputModifiers.Control); win.MouseUp(p, MouseButton.Left, RawInputModifiers.Control); Pump(1);
        }
        Pump(2);
        Assert.All(tiles, t => Assert.True(t.Classes.Contains("ctrl-selected")));
        // The old 2px selection border widened each tile by 4px and wrapped the last column.
        Assert.Equal(tops, tiles.Select(t => t.Bounds.Y).ToList());
        win.Close();
    }

    [AvaloniaFact]
    public async Task Home_ClickBesideTheTiles_ClearsTheSelection()
    {
        var (_, view, win) = await MountHome();
        var row = view.FindControl<ItemsControl>("AlbumsRow")!;
        var tile = Tiles(row)[0];
        var p = tile.TranslatePoint(new Point(20, 20), win)!.Value;
        win.MouseDown(p, MouseButton.Left, RawInputModifiers.Control); win.MouseUp(p, MouseButton.Left, RawInputModifiers.Control); Pump(2);
        Assert.True(tile.Classes.Contains("ctrl-selected"));

        // Just right of the album row's last tile is page background.
        var last = Tiles(row).Last();
        var beside = last.TranslatePoint(new Point(last.Bounds.Width + 6, 20), win)!.Value;
        _o.WriteLine($"beside hit: {win.GetVisualAt(beside)?.GetType().Name}");
        win.MouseDown(beside, MouseButton.Left); win.MouseUp(beside, MouseButton.Left); Pump(2);
        Assert.False(tile.Classes.Contains("ctrl-selected"));
        win.Close();
    }

    [AvaloniaFact]
    public async Task Songs_RowsGetThePlate_AndAClickBelowTheRowsClears()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 5; i++)
            lib.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", Album = "Album", FilePath = TestPaths.Primary("t", $"{Guid.NewGuid():N}.mp3"), Duration = TimeSpan.FromMinutes(3) });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;
        await PumpUntil(() => view.TrackList.GetVisualDescendants().OfType<ListBoxItem>().Count() == 5);

        CtrlA(win);
        var rows = view.TrackList.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.All(rows, r => Assert.True(r.Classes.Contains("ctrl-selected")));
        await Task.Delay(250); Pump(2);
        Assert.All(rows, r => Assert.Equal(1, Ring(r).Opacity, 2));

        var below = new Point(700, 800);
        win.MouseDown(below, MouseButton.Left); win.MouseUp(below, MouseButton.Left); Pump(2);
        Assert.All(rows, r => Assert.False(r.Classes.Contains("ctrl-selected")));
        win.Close();
    }
}
