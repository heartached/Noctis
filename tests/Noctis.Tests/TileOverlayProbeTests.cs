using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using AvPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Pins for the 09-13 tile overlay: Home albums-row tiles stay at cover width in the WrapPanel (long titles wrap), hover glyphs are centred in their 32px circles (Path Stretch=Uniform), and Favorites shows exactly one of Play/Pause (typed {x:False} fallback).</summary>
public class TileOverlayProbeTests
{
    private readonly ITestOutputHelper _o;
    public TileOverlayProbeTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static Track T(string title, string artist, Guid albumId, string album) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = album,
        AlbumId = albumId, Duration = TimeSpan.FromSeconds(176), PlayCount = 3,
    };

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    /// <summary>
    /// Ink box of every visible glyph inside the two 32px hover circles, relative to the
    /// circle. Stretch=Uniform translates the geometry by -bounds.Position, so the ink is
    /// the Path's own box scaled by the Viewbox; Play carries a 1px optical nudge right.
    /// </summary>
    private void AssertGlyphsCentred(Button tile)
    {
        var failures = new List<string>();
        foreach (var cls in new[] { "tile-play", "tile-more" })
        {
            var btn = tile.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains(cls));
            foreach (var vb in btn.GetVisualDescendants().OfType<Viewbox>().Where(v => v.IsVisible))
            {
                var path = vb.GetVisualDescendants().OfType<AvPath>().First();
                var vp = vb.TranslatePoint(new Point(0, 0), btn)!.Value;
                var pp = path.TranslatePoint(new Point(0, 0), btn)!.Value;
                var pe = path.TranslatePoint(new Point(path.Bounds.Width, path.Bounds.Height), btn)!.Value;
                var cx = (pp.X + pe.X) / 2; var cy = (pp.Y + pe.Y) / 2;
                _o.WriteLine($"  {cls}: stretch={path.Stretch} vb at ({vp.X:0.##},{vp.Y:0.##}) {vb.Bounds.Width:0.##}x{vb.Bounds.Height:0.##} | path bounds {path.Bounds.Width:0.##}x{path.Bounds.Height:0.##} mapped ({pp.X:0.##},{pp.Y:0.##})-({pe.X:0.##},{pe.Y:0.##}) | ink centre=({cx:0.##},{cy:0.##}) nudge={vb.Margin.Left}");
                if (path.Stretch != Avalonia.Media.Stretch.Uniform) failures.Add($"{cls} stretch={path.Stretch}");
                if (Math.Abs(cx - (16 + vb.Margin.Left)) >= 0.6) failures.Add($"{cls} ink centre x={cx:0.##}");
                if (Math.Abs(cy - 16) >= 0.6) failures.Add($"{cls} ink centre y={cy:0.##}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [AvaloniaFact]
    public async Task Home_AlbumsRow_TileWidths_And_Glyphs()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        var a = T("47 (Remix)", "Anuel AA & Ñengo Flow", id1, "47 (Remix) [feat. Farruko, Casper Magico, Darell & Bad Bunny] - Single");
        var b = T("Chambea", "Bad Bunny", id2, "Chambea - Single");
        lib.TrackList.AddRange(new[] { a, b });
        ((List<Album>)lib.Albums).Add(new Album { Id = id1, Name = a.Album, Artist = a.Artist, Year = 2017, Tracks = new List<Track> { a } });
        ((List<Album>)lib.Albums).Add(new Album { Id = id2, Name = b.Album, Artist = b.Artist, Year = 2017, Tracks = new List<Track> { b } });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(a); player.History.Add(b);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();

        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();

        var row = view.FindControl<ItemsControl>("AlbumsRow")!;
        _o.WriteLine($"AlbumsRow width={row.Bounds.Width:0.#} AlbumTileSize={vm.AlbumTileSize:0.#}");
        var tiles = view.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("album-tile")).ToList();
        foreach (var tile in tiles)
        {
            var name = ((Album)tile.DataContext!).Name;
            var tp = tile.TranslatePoint(new Point(0, 0), row)!.Value;
            var cover = tile.GetVisualDescendants().OfType<Border>().First(bd => bd.Width == vm.AlbumTileSize);
            var cp = cover.TranslatePoint(new Point(0, 0), tile)!.Value;
            var title = tile.GetVisualDescendants().OfType<TextBlock>().First(t => t.FontSize == 13);
            _o.WriteLine($"tile '{name}': at x={tp.X:0.#} width={tile.Bounds.Width:0.#} | cover at x={cp.X:0.#} w={cover.Bounds.Width:0.#} | title w={title.Bounds.Width:0.#} h={title.Bounds.Height:0.#}");
            // The WrapPanel measures tiles unbounded: the tile must stay at cover width (+6px
            // padding each side, GitHub #210) with the cover flush left, and a long title wraps
            // (MaxLines=2) instead of stretching the tile.
            Assert.Equal(vm.AlbumTileSize + 12, tile.Bounds.Width, precision: 0);
            Assert.Equal(6, cp.X, precision: 0);
            Assert.True(title.Bounds.Width <= vm.AlbumTileSize, $"title width {title.Bounds.Width}");
            AssertGlyphsCentred(tile);
        }
        var longTitle = tiles.Select(t => t.GetVisualDescendants().OfType<TextBlock>().First(x => x.FontSize == 13)).Max(t => t.Bounds.Height);
        var shortTitle = tiles.Select(t => t.GetVisualDescendants().OfType<TextBlock>().First(x => x.FontSize == 13)).Min(t => t.Bounds.Height);
        Assert.True(longTitle > shortTitle * 1.5, $"long title should wrap: {longTitle} vs {shortTitle}");
    }

    [AvaloniaFact]
    public void Favorites_Hover_PlayPauseState()
    {
        EnsureAppStyles();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new FakeLibraryService();
            var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
            var a = T("COMING OVER", "Juice WRLD", id1, "COMING OVER - Single"); a.IsFavorite = true;
            var b = T("ANGELS", "Chase Atlantic", id2, "PHASES"); b.IsFavorite = true;
            lib.TrackList.AddRange(new[] { a, b });
            ((List<Album>)lib.Albums).Add(new Album { Id = id1, Name = a.Album, Artist = a.Artist, Tracks = new List<Track> { a } });
            ((List<Album>)lib.Albums).Add(new Album { Id = id2, Name = b.Album, Artist = b.Artist, Tracks = new List<Track> { b, T("x", "y", id2, "PHASES") } });
            var persistence = new TestPersistenceService();
            var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
            var settings = new SettingsViewModel(new PersistenceService(root), lib, new NoOpPlayHistory());
            var vm = new FavoritesViewModel(player, lib, persistence, new SidebarViewModel(persistence, lib), settings);
            a.IsNowPlaying = true; a.IsCurrentlyPlaying = true;
            vm.Refresh();

            var view = new FavoritesView { DataContext = vm };
            var win = new Window { Width = 1400, Height = 900, Content = view };
            win.Show();
            Pump();

            var tiles = view.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("album-tile")).ToList();
            _o.WriteLine($"tiles={tiles.Count} TileArtworkSize={vm.TileArtworkSize:0.#} cols={vm.GridColumns}");
            foreach (var tile in tiles)
            {
                var item = (FavoriteItem)tile.DataContext!;
                var tp = tile.TranslatePoint(new Point(0, 0), win)!.Value;
                _o.WriteLine($"tile '{item.Title}' isAlbum={item.IsAlbum} at ({tp.X:0.#},{tp.Y:0.#}) {tile.Bounds.Width:0.#}x{tile.Bounds.Height:0.#}");
                AssertGlyphsCentred(tile);

                // Hover the cover, then the play button itself, then leave.
                var cover = tile.GetVisualDescendants().OfType<Border>().First(bd => bd.Width == vm.TileArtworkSize);
                var cc = cover.TranslatePoint(new Point(cover.Bounds.Width / 2, cover.Bounds.Height / 2), win)!.Value;
                win.MouseMove(cc); Pump(8);
                var play = tile.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("tile-play"));
                var vbs = play.GetVisualDescendants().OfType<Viewbox>().ToList();
                _o.WriteLine($"  hover cover: tile.IsPointerOver={tile.IsPointerOver} play.Opacity={play.Opacity} playVis={vbs[0].IsVisible} pauseVis={vbs[1].IsVisible}");
                // Exactly one glyph: Pause on the audibly playing track, Play elsewhere. (A string
                // FallbackValue="False" used to break both multi-bindings, leaving both visible.)
                var playing = ReferenceEquals(item.Track, a);
                Assert.Equal(!playing, vbs[0].IsVisible);
                Assert.Equal(playing, vbs[1].IsVisible);
                Assert.True(tile.IsPointerOver);
                var pc = play.TranslatePoint(new Point(16, 16), win)!.Value;
                win.MouseMove(pc); Pump(8);
                _o.WriteLine($"  hover play btn: tile.IsPointerOver={tile.IsPointerOver} play.IsPointerOver={play.IsPointerOver} play.Opacity={play.Opacity} playVis={vbs[0].IsVisible} pauseVis={vbs[1].IsVisible}");
                win.MouseMove(new Point(5, 5)); Pump(8);
                _o.WriteLine($"  leave: tile.IsPointerOver={tile.IsPointerOver} play.Opacity={play.Opacity}");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
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

public class TileHeartFadeTests
{
    private readonly ITestOutputHelper _o;
    public TileHeartFadeTests(ITestOutputHelper o) => _o = o;
    private static void Pump(int n = 4) { for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }

    /// <summary>
    /// Album tiles (Home here; Albums/Artist/More-by-artist share the markup): favoriting
    /// pops the heart in and un-favoriting fades it out. The tiles used to also bind the
    /// heart's IsVisible to HasFavoriteTrack, which collapsed it on the same tick.
    /// </summary>
    [AvaloniaFact]
    public async Task HomeTile_Heart_FadesOut_OnUnfavorite()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
        var lib = new FakeLibraryService();
        var id = Guid.NewGuid();
        var t = new Track { Id = Guid.NewGuid(), Title = "Chambea", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny", Album = "Chambea - Single", AlbumId = id, Duration = TimeSpan.FromSeconds(176), PlayCount = 3 };
        lib.TrackList.Add(t);
        var album = new Album { Id = id, Name = t.Album, Artist = t.Artist, Year = 2017, Tracks = new List<Track> { t } };
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(t);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();
        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        await Task.Delay(200); // past the HeartIcon re-bind window

        var tile = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("album-tile"));
        var heart = tile.GetVisualDescendants().OfType<Noctis.Controls.HeartIcon>().First(h => ((Panel)h.Parent!).Classes.Contains("heart-title"));
        Assert.Null(heart.VisibleGlyph);
        Assert.Equal(0, heart.Bounds.Width);

        // Both pops are sampled on the wall clock, so the samples are taken in a short loop
        // and a mid-flight value is only demanded when they came in promptly: a loaded runner
        // can spend the whole 200ms transition inside one pump and see only the end state.
        var clock = Stopwatch.StartNew();
        t.IsFavorite = true; album.NotifyFavoriteStateChanged(); Pump(1);
        var g = heart.VisibleGlyph;
        Assert.NotNull(g);
        var popIn = new List<double>();
        for (var i = 0; i < 16 && g!.Opacity < 1; i++) { popIn.Add(g.Opacity); await Task.Delay(16); Pump(1); }
        popIn.Add(g!.Opacity);
        _o.WriteLine($"favorite pop-in ({clock.ElapsedMilliseconds}ms): {string.Join(" ", popIn.Select(o => o.ToString("0.##")))} heart w={heart.Bounds.Width}");
        Assert.True(g.IsVisible, "pop-in should have started");
        Assert.True(popIn.Any(o => o > 0 && o < 1) || (g.Opacity >= 1 && clock.ElapsedMilliseconds > 150), "pop-in should be mid-transition");

        await Task.Delay(300); Pump(2);
        _o.WriteLine($"favorite settled: opacity={g.Opacity:0.##}");
        Assert.Equal(1, g.Opacity, precision: 2);

        clock.Restart();
        t.IsFavorite = false; album.NotifyFavoriteStateChanged(); Pump(1);
        Assert.True(heart.IsVisible, "the tile must not collapse the heart on the same tick");
        var fadeOut = new List<double>();
        for (var i = 0; i < 16 && g.IsVisible && g.Opacity > 0; i++) { fadeOut.Add(g.Opacity); await Task.Delay(16); Pump(1); }
        fadeOut.Add(g.Opacity);
        _o.WriteLine($"unfavorite fade-out ({clock.ElapsedMilliseconds}ms): {string.Join(" ", fadeOut.Select(o => o.ToString("0.##")))} glyph visible={g.IsVisible} transitions={(g.Transitions == null ? "null" : g.Transitions.Count.ToString())} heartVisible={heart.IsVisible} heart.IsFavorite={heart.IsFavorite}");
        Assert.True(fadeOut.Any(o => o > 0 && o < 1) || (!g.IsVisible && clock.ElapsedMilliseconds > 150), "fade-out should be running");

        await Task.Delay(400); Pump(2);
        _o.WriteLine($"unfavorite settled: glyph visible={g.IsVisible} heart w={heart.Bounds.Width}");
        Assert.False(g.IsVisible);
        Assert.Null(heart.VisibleGlyph);
    }
}
