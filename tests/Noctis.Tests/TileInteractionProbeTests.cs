using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Pins for the tile interactions fixed 09-13: Home album tiles top-align in their WrapPanel
/// row, a Home context menu re-opened right after using an option survives the 500 ms
/// refresh (the row is no longer reset when unchanged), and the Favorites hover Play plays
/// without the tile's own Click handler also opening the album.
/// </summary>
public class TileInteractionProbeTests
{
    private readonly ITestOutputHelper _o;
    public TileInteractionProbeTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }
    private static Track T(string title, string artist, Guid albumId, string album) => new()
    { Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = album, AlbumId = albumId, Duration = TimeSpan.FromSeconds(176), PlayCount = 3, FilePath = "C:\\m\\" + title + ".mp3" };
    private static void Pump(int n = 4) { for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }

    [AvaloniaFact]
    public async Task Home_TileCoverY_And_ContextMenuReopen()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        var a = T("47 (Remix)", "Anuel AA", id1, "47 (Remix) [feat. Farruko, Casper Magico, Darell & Bad Bunny] - Single");
        var b = T("Chambea", "Bad Bunny", id2, "Chambea - Single");
        lib.TrackList.AddRange(new[] { a, b });
        ((List<Album>)lib.Albums).Add(new Album { Id = id1, Name = a.Album, Artist = a.Artist, Year = 2017, Tracks = new List<Track> { a } });
        ((List<Album>)lib.Albums).Add(new Album { Id = id2, Name = b.Album, Artist = b.Artist, Year = 2017, Tracks = new List<Track> { b } });
        var persistence = new TestPersistenceService();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(a); player.History.Add(b);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();
        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();

        var row = view.FindControl<ItemsControl>("AlbumsRow")!;
        // The Albums row's own tiles (New in Library, GitHub #159, shows the same albums below).
        var tiles = row.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("album-tile")).ToList();
        foreach (var tile in tiles)
        {
            var cover = tile.GetVisualDescendants().OfType<Border>().First(bd => bd.Width == vm.AlbumTileSize);
            var cp = cover.TranslatePoint(new Point(0, 0), row)!.Value;
            _o.WriteLine($"tile '{((Album)tile.DataContext!).Name.Substring(0, 8)}' h={tile.Bounds.Height:0.#} valign={tile.VerticalAlignment} vcontent={tile.VerticalContentAlignment} cover y={cp.Y:0.##}");
        }
        // Covers share one top edge even when one title wraps and the other does not.
        var coverTops = tiles.Select(t => t.GetVisualDescendants().OfType<Border>().First(bd => bd.Width == vm.AlbumTileSize).TranslatePoint(new Point(0, 0), row)!.Value.Y).ToList();
        Assert.True(coverTops.Max() - coverTops.Min() < 0.5, $"cover tops differ: {string.Join(",", coverTops)}");

        await vm.RefreshAsync(); Pump();
        tiles = row.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("album-tile")).ToList();

        // Right-click tile 2, "use" the Play option, right-click again; log the menu's open state.
        var t2 = tiles[1];
        var pt = t2.TranslatePoint(new Point(40, 40), win)!.Value;
        var log = new List<string>();
        Button? Under() { var v = win.GetVisualAt(pt) as Visual; while (v != null && !(v is Button b && b.Classes.Contains("album-tile"))) v = v.GetVisualParent(); return v as Button; }
        string St() { var u = Under(); return u == null ? "no-tile" : $"tile#{u.GetHashCode() % 1000} ctx={(u.ContextMenu == null ? "null" : u.ContextMenu.IsOpen ? "OPEN" : "closed")}"; }
        void RightClick(string tag)
        {
            win.MouseDown(pt, MouseButton.Right); Pump(1);
            log.Add($"{tag} pressed: {St()}");
            win.MouseUp(pt, MouseButton.Right);
            for (var i = 0; i < 6; i++) { Pump(1); log.Add($"{tag} released +{i}: {St()}"); }
        }
        RightClick("rc1");
        var menu = t2.ContextMenu;
        Assert.NotNull(menu);
        menu!.Opened += (_, _) => log.Add("  event Opened");
        menu.Closed += (_, _) => log.Add("  event Closed");
        // Use the "Favorites" option the way a click would, then let the 500 ms Home refresh run.
        var fav = menu.Items.OfType<MenuItem>().First(m => (m.Header as string) == "Favorites");
        fav.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 4; i++) { Pump(1); log.Add($"after option +{i}: {(menu.IsOpen ? "OPEN" : "closed")} {St()} fav={((Album)t2.DataContext!).HasFavoriteTrack}"); }
        RightClick("rc2");
        log.Add($"rc2 menu.IsOpen={menu.IsOpen} tilesAlive={tiles.Count(t => t.IsAttachedToVisualTree())}");
        // The 500 ms Home refresh debounce fires now (the option's FavoritesChanged).
        await Task.Delay(700); Pump(3);
        var newTiles = row.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("album-tile")).ToList();
        log.Add($"after 700ms refresh: menu.IsOpen={menu.IsOpen} oldTilesAlive={tiles.Count(t => t.IsAttachedToVisualTree())} sameTiles={newTiles.SequenceEqual(tiles)}");
        RightClick("rc3");
        foreach (var l in log) _o.WriteLine(l);
        // The refresh must leave the (unchanged) row's tiles alone so the re-opened menu survives.
        Assert.True(newTiles.SequenceEqual(tiles), "album tiles were rebuilt by the refresh");
        Assert.True(log.Any(l => l.StartsWith("after 700ms refresh: menu.IsOpen=True")), "menu closed by the refresh");
    }

    [AvaloniaFact]
    public void Favorites_HoverPlayClick_PlaysInsteadOfOpening()
    {
        EnsureAppStyles();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new FakeLibraryService();
            var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
            var a = T("COMING OVER", "Juice WRLD", id1, "COMING OVER - Single"); a.IsFavorite = true;
            var b1 = T("ANGELS", "Chase Atlantic", id2, "PHASES"); b1.IsFavorite = true;
            var b2 = T("INTRO", "Chase Atlantic", id2, "PHASES"); b2.IsFavorite = true;
            lib.TrackList.AddRange(new[] { a, b1, b2 });
            ((List<Album>)lib.Albums).Add(new Album { Id = id1, Name = a.Album, Artist = a.Artist, Tracks = new List<Track> { a } });
            ((List<Album>)lib.Albums).Add(new Album { Id = id2, Name = "PHASES", Artist = "Chase Atlantic", Tracks = new List<Track> { b1, b2 } });
            var persistence = new TestPersistenceService();
            var audio = new FakeAudioPlayer();
            var player = new PlayerViewModel(audio, lib, persistence, new FakeAnimatedCoverService());
            var settings = new SettingsViewModel(new PersistenceService(root), lib, new NoOpPlayHistory());
            var vm = new FavoritesViewModel(player, lib, persistence, new SidebarViewModel(persistence, lib), settings);
            vm.Refresh();
            var opened = 0; vm.AlbumOpened += (_, _) => opened++;
            var view = new FavoritesView { DataContext = vm };
            var win = new Window { Width = 1400, Height = 900, Content = view };
            win.Show(); Pump();

            var tiles = view.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("album-tile")).ToList();
            foreach (var tile in tiles)
            {
                var item = (FavoriteItem)tile.DataContext!;
                var outerClicks = 0; tile.Click += (_, _) => outerClicks++;
                var play = tile.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("tile-play"));
                var innerClicks = 0; play.Click += (_, _) => innerClicks++;
                var pc = play.TranslatePoint(new Point(16, 16), win)!.Value;
                var hit = win.GetVisualAt(pc);
                var inside = hit is Visual v && (ReferenceEquals(play, v) || play.IsVisualAncestorOf(v));
                _o.WriteLine($"[{item.Title}] isAlbum={item.IsAlbum}: play enabled={play.IsEffectivelyEnabled} cmd={(play.Command == null ? "null" : play.Command.GetType().Name)} hit={hit?.GetType().Name} hitInsidePlay={inside}");
                var before = audio.PlayedPaths.Count; var openedBefore = opened;
                win.MouseMove(pc); Pump(2);
                win.MouseDown(pc, MouseButton.Left); Pump(1);
                _o.WriteLine($"   pressed: inner.IsPressed={play.IsPressed} outer.IsPressed={tile.IsPressed}");
                win.MouseUp(pc, MouseButton.Left); Pump(4);
                _o.WriteLine($"   after click: innerClicks={innerClicks} outerClicks={outerClicks} played={audio.PlayedPaths.Count - before} albumOpened={opened - openedBefore} current={player.CurrentTrack?.Title}");
                // The inner Click bubbles to the tile (outerClicks counts it) but must not act:
                // Play plays the item from the tile and never opens the album.
                Assert.Equal(1, innerClicks);
                Assert.Equal(1, audio.PlayedPaths.Count - before);
                Assert.Equal(0, opened - openedBefore);
            }
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
