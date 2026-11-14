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
/// Albums grid spacing. Default = the 1.6.9 look the owner asked back for (11-14): covers 8px
/// apart and one row height (cover + 64) for every row, so a wrapped title never makes its
/// row taller. Settings › "Wider spacing between albums" keeps GitHub #210: one 18px gap
/// between covers and between a caption and the next cover, rows sized to their own captions.
/// With NOCTIS_TEST_SKIA=1 the render probe saves PNGs (NOCTIS_RENDER_OUT or
/// D:\NoctisWT\art-1114\albumgrid).
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

    private sealed record Rig(Window Win, LibraryAlbumsView View, LibraryAlbumsViewModel Vm, SettingsViewModel Settings, List<Button> Tiles);

    /// <summary>15 albums sorted by title: row 1 all one-line, row 2 with one wrapped title,
    /// row 3 one-line (at five columns); every third album is explicit (E badge).</summary>
    private static async Task<Rig> Mount(double width, bool wide, bool masonry = false, double height = 1400, string? artDir = null)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        var names = new[]
        {
            "Take the Blame - Single", "Me Llamas - Single", "Me Acostumbre - Single", "Sigues Con El (Remix) - Single", "Tu No Vive Asi - Single",
            "Up Next", "Up Next (feat. Desiigner and Ski Mask the Slump God) - Single", "Volvi - Single", "Waves - EP", "Xtasis - Single",
            "Yesterday - Single", "Yonaguni - Single", "Zero - Single", "Zona - EP", "Zzz - Single",
        };
        for (var i = 0; i < names.Length; i++)
            albums.Add(new Album
            {
                Id = Guid.NewGuid(),
                Name = $"{(char)('A' + i)} {names[i]}",
                Artist = "Artist", Year = 2020,
                Tracks = i % 3 == 1 ? new List<Track> { new() { Id = Guid.NewGuid(), Title = "T", IsExplicit = true } } : new List<Track>(),
                ArtworkPath = artDir == null ? null : Path.Combine(artDir, $"cover{i % 5}.png"),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumGridWideSpacing = wide;
        settings.AlbumGridMasonry = masonry;
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window
        {
            Width = width, Height = height, Content = view,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
        };
        win.Show(); Pump();
        vm.IsActive = true;
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && VisibleTiles(view).Count < 15)
        { Pump(1); await Task.Delay(5); }
        Pump(6);
        var tiles = VisibleTiles(view)
            .OrderBy(b => Math.Round(b.TranslatePoint(default, view)!.Value.Y)).ThenBy(b => b.TranslatePoint(default, view)!.Value.X).ToList();
        return new Rig(win, view, vm, settings, tiles);
    }

    private static List<Button> VisibleTiles(Control view)
        => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible).ToList();

    private static Rect Cover(Rig g, Button t)
    {
        var c = t.GetVisualDescendants().OfType<Border>().First(b => b.ClipToBounds && b.Width == g.Vm.TileArtworkSize);
        return new Rect(c.TranslatePoint(default, g.View)!.Value, c.Bounds.Size);
    }

    private static double CaptionBottom(Rig g, Button t)
    {
        var text = t.GetVisualDescendants().OfType<StackPanel>().First(s => s.Classes.Contains("tile-text"));
        // The cover is inside tile-text; its last child is the subtitle line.
        var last = (Control)text.Children[^1];
        return last.TranslatePoint(new Point(0, last.Bounds.Height), g.View)!.Value.Y;
    }

    private static int TitleLines(Button t)
        => t.GetVisualDescendants().OfType<TextBlock>().First(b => Noctis.Helpers.TitleWrap.GetWatch(b)).TextLayout.TextLines.Count;

    [AvaloniaTheory]
    [InlineData(1366)]
    [InlineData(1920)]
    public async Task Default_Is169_CloseCovers_AndOneRowHeight_EvenWithAWrappedTitle(double width)
    {
        var g = await Mount(width, wide: false);
        try
        {
            Assert.False(g.Vm.IsWideAlbumSpacing);
            Assert.Equal(15, g.Tiles.Count);
            var cols = g.Vm.GridColumns;
            Assert.Equal(5, cols);
            var hGap = Cover(g, g.Tiles[1]).X - Cover(g, g.Tiles[0]).Right;
            var rows = g.Tiles.Select(t => t.FindAncestorOfType<ListBoxItem>()!).Distinct().ToList();
            var heights = rows.Select(r => r.Bounds.Height).ToList();
            var pitch1 = Cover(g, g.Tiles[cols]).Y - Cover(g, g.Tiles[0]).Y;
            var pitch2 = Cover(g, g.Tiles[2 * cols]).Y - Cover(g, g.Tiles[cols]).Y;
            var wrapped = g.Tiles.Skip(cols).Take(cols).Max(TitleLines);
            var left = Cover(g, g.Tiles[0]).X;
            var top = Cover(g, g.Tiles[0]).Y;
            _o.WriteLine($"w={width} art={g.Vm.TileArtworkSize:0.#} rowH={g.Vm.TileRowHeight:0.#} hGap={hGap:0.##} rows=[{string.Join(",", heights.Select(h => h.ToString("0.#")))}] pitch={pitch1:0.#}/{pitch2:0.#} wrappedLines={wrapped} left={left:0.#} top={top:0.#}");

            // 1.6.9: 2px margin + 2px padding a side = 8px between covers (one pixel of rounding).
            Assert.InRange(hGap, 7, 9.1);
            // Row 2 really has a two-line title, yet every row is cover + 64 high.
            Assert.Equal(2, wrapped);
            Assert.Equal(g.Vm.TileArtworkSize + LibraryAlbumsViewModel.TileTextHeight, g.Vm.TileRowHeight, 3);
            Assert.Equal(3, heights.Count);
            Assert.All(heights, h => Assert.Equal(heights[0], h, 0));
            Assert.Equal(pitch1, pitch2, 0);
            // The wrapped caption fits inside its row: the next cover starts below it.
            Assert.True(Cover(g, g.Tiles[2 * cols]).Y - g.Tiles.Skip(cols).Take(cols).Max(t => CaptionBottom(g, t)) >= 4);
            // 1.6.9's margins: 8px DockPanel top, 12px side, + 1px row + 4px tile padding.
            Assert.Equal(17, left, 0);
            Assert.Equal(13, top, 0);
        }
        finally { g.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task WideSpacing_GapsMatch_AcrossColumnsRowsAndCaptionHeights()
    {
        var g = await Mount(1400, wide: true);
        try
        {
            Assert.True(g.Vm.IsWideAlbumSpacing);
            Assert.True(double.IsNaN(g.Vm.TileRowHeight));
            Assert.Equal(15, g.Tiles.Count);
            var tiles = g.Tiles;
            var cols = g.Vm.GridColumns;
            var hGap = Cover(g, tiles[1]).X - Cover(g, tiles[0]).Right;
            var gap1 = Cover(g, tiles[cols]).Y - tiles.Take(cols).Max(t => CaptionBottom(g, t));
            var gap2 = Cover(g, tiles[2 * cols]).Y - tiles.Skip(cols).Take(cols).Max(t => CaptionBottom(g, t));
            var left = Cover(g, tiles[0]).X;
            var top = Cover(g, tiles[0]).Y;
            _o.WriteLine($"cols={cols} hGap={hGap:0.##} rowGap(one-line row)={gap1:0.##} rowGap(wrapped row)={gap2:0.##} left={left:0.##} top={top:0.##}");

            // GitHub #210, unchanged: one 18px gap everywhere (one pixel of layout rounding).
            Assert.InRange(hGap, 16.9, 19.1);
            Assert.InRange(gap1, 17.5, 18.5);
            Assert.InRange(gap2, 17.5, 18.5);
            Assert.InRange(top - left, -0.5, 0.5);
        }
        finally { g.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task TogglingTheSetting_SwitchesTheLiveGrid()
    {
        var g = await Mount(1400, wide: false);
        try
        {
            var closeArt = g.Vm.TileArtworkSize;
            g.Settings.AlbumGridWideSpacing = true;
            Pump(8);
            Assert.True(g.View.Classes.Contains("wide-album-gap"));
            Assert.True(g.Vm.TileArtworkSize < closeArt - 9, $"{g.Vm.TileArtworkSize} vs {closeArt}");
            var tile = VisibleTiles(g.View)[0];
            Assert.Equal(new Thickness(6), tile.Padding);
            Assert.Equal(new Thickness(3, 2), tile.Margin);
            g.Settings.AlbumGridWideSpacing = false;
            Pump(8);
            Assert.False(g.View.Classes.Contains("wide-album-gap"));
            Assert.Equal(closeArt, g.Vm.TileArtworkSize, 1);
            Assert.Equal(new Thickness(4), tile.Padding);
            Assert.Equal(new Thickness(0), tile.Margin);
        }
        finally { g.Win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, 8)]
    [InlineData(true, 18)]
    public async Task Masonry_UsesTheSpacingModesGapBetweenColumns(bool wide, double expected)
    {
        var g = await Mount(1400, wide, masonry: true, height: 900);
        try
        {
            Assert.True(g.Vm.IsMasonryView);
            var firstRow = g.Tiles.OrderBy(t => Cover(g, t).Y).Take(2).OrderBy(t => Cover(g, t).X).ToList();
            var hGap = Cover(g, firstRow[1]).X - Cover(g, firstRow[0]).Right;
            _o.WriteLine($"wide={wide} masonry hGap={hGap:0.##}");
            Assert.InRange(hGap, expected - 1, expected + 1.1);
        }
        finally { g.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task Setting_DefaultsOff_AndSurvivesSaveAndReload()
    {
        Assert.False(new AppSettings().AlbumGridWideSpacing);
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            Assert.False(vm.AlbumGridWideSpacing);
            vm.AlbumGridWideSpacing = true;
            await vm.SaveAsync();
            var reloaded = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await reloaded.LoadAsync();
            Assert.True(reloaded.AlbumGridWideSpacing);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>Real-Skia render of the grid at 1366 and 1920 in both modes, for eyeballing.</summary>
    [AvaloniaFact]
    public async Task Probe_RenderTheGridToPng()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering
        var env = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        var outDir = string.IsNullOrWhiteSpace(env) ? @"D:\NoctisWT\art-1114\albumgrid" : env;
        var artDir = Path.Combine(outDir, "covers");
        Directory.CreateDirectory(artDir);
        var colors = new[] { Colors.IndianRed, Colors.SteelBlue, Colors.SeaGreen, Colors.Goldenrod, Colors.MediumPurple };
        for (var i = 0; i < colors.Length; i++)
        {
            var file = Path.Combine(artDir, $"cover{i}.png");
            if (File.Exists(file)) continue;
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(400, 400));
            using (var ctx = bmp.CreateDrawingContext())
                ctx.FillRectangle(new SolidColorBrush(colors[i]), new Rect(0, 0, 400, 400));
            bmp.Save(file);
        }
        foreach (var width in new[] { 1366.0, 1920.0 })
            foreach (var (wide, masonry) in new[] { (false, false), (true, false), (false, true) })
            {
                var g = await Mount(width, wide, masonry, height: width > 1500 ? 1080 : 768, artDir: artDir);
                try
                {
                    var end = Environment.TickCount64 + 800;
                    while (Environment.TickCount64 < end) { Pump(1); await Task.Delay(8); }
                    var tag = $"{width:0}-{(wide ? "wide" : "default")}{(masonry ? "-masonry" : "")}";
                    g.Win.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{tag}.png"));
                }
                finally { g.Win.Close(); }
            }
    }
}
