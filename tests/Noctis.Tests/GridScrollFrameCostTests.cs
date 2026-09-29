using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// "Albums scrolls choppy" (09-24: the user's 60 fps video showed the grid moving once every
/// ~7 frames). A scrolled frame repaints every cover and each new row realizes five tiles;
/// the headless probe found three costs behind it, pinned here: covers sampled through
/// mipmaps on every frame (HighQuality), nine PNG menu icons decoded per album tile, and
/// full-size cover decodes still run for tiles already scrolled past.
/// </summary>
[Collection("ArtworkCache")]
public class GridScrollFrameCostTests : IDisposable
{
    private readonly List<(string Path, int Width)> _decoded = new();

    public GridScrollFrameCostTests()
    {
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            lock (_decoded) _decoded.Add((path, width));
            return new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        };
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
    }

    private static string Path(string name) => System.IO.Path.Combine("C:\\", "art", name + ".jpg");

    [Fact]
    public void MildDownscale_IsTheTileRange_NotThumbnailsOrUpscales()
    {
        static bool Mild(double slot, int bitmap, double scaling) => CachedImage.IsMildDownscale(
            new Size(slot, slot), new Size(bitmap, bitmap), new PixelSize(bitmap, bitmap),
            Stretch.UniformToFill, StretchDirection.Both, scaling);

        Assert.True(Mild(318, 384, 1.0));   // Albums tile at 100%: the 384 bucket drawn at 0.83
        Assert.True(Mild(318, 768, 2.0));   // same tile at 200%: 636 device px from 768
        Assert.True(Mild(384, 384, 1.0));   // exactly 1:1
        Assert.False(Mild(36, 128, 1.0));   // row thumbnail: a 0.28 shrink needs the mipmaps
        Assert.False(Mild(318, 256, 1.0));  // upscaled fallback bitmap keeps its filter
    }

    [AvaloniaFact]
    public async Task FastDownscale_DrawsATileCoverBilinear_AndGivesTheConfiguredModeBack()
    {
        var image = new CachedImage { Width = 318, Height = 318, DecodeWidth = 768, Stretch = Stretch.UniformToFill, FastDownscale = true };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        var window = new Window { Width = 500, Height = 500, Content = image };
        window.Show();
        await Flush();
        image.SourcePath = Path("tile");
        await Flush();

        Assert.Equal(384, Assert.IsAssignableFrom<Bitmap>(image.Source).PixelSize.Width);
        Assert.Equal(BitmapInterpolationMode.LowQuality, RenderOptions.GetBitmapInterpolationMode(image));

        image.FastDownscale = false;
        Assert.Equal(BitmapInterpolationMode.HighQuality, RenderOptions.GetBitmapInterpolationMode(image));
        window.Close();
    }

    [AvaloniaFact]
    public async Task FastDownscale_LeavesARowThumbnailOnItsConfiguredMode()
    {
        var thumb = new CachedImage { Width = 36, Height = 36, DecodeWidth = 256, Stretch = Stretch.UniformToFill, FastDownscale = true };
        RenderOptions.SetBitmapInterpolationMode(thumb, BitmapInterpolationMode.HighQuality);
        var window = new Window { Width = 200, Height = 200, Content = thumb };
        window.Show();
        await Flush();
        thumb.SourcePath = Path("thumb");
        await Flush();

        Assert.Equal(128, Assert.IsAssignableFrom<Bitmap>(thumb.Source).PixelSize.Width);
        Assert.Equal(BitmapInterpolationMode.HighQuality, RenderOptions.GetBitmapInterpolationMode(thumb));
        window.Close();
    }

    [AvaloniaFact]
    public void QueuedDecode_ForATileThatMovedOn_IsSkipped()
    {
        // Off the tree a path change only bumps the load generation: 1, then 2.
        var image = new CachedImage();
        image.SourcePath = Path("scrolled-past");
        image.SourcePath = Path("on-screen");

        Assert.Null(image.DecodeInBackground(Path("scrolled-past"), 384, generation: 1));
        Assert.DoesNotContain(_decoded, d => d.Path == Path("scrolled-past"));

        Assert.NotNull(image.DecodeInBackground(Path("on-screen"), 384, generation: 2));
        Assert.Contains(_decoded, d => d.Path == Path("on-screen"));
    }

    [AvaloniaFact]
    public async Task AlbumsGrid_TilesShareMenuIcons_AndDecodeCoversAtTheirOwnSize()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistoryStub()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var window = new Window { Width = 1728, Height = 1030, Content = view };
        window.Show();
        await Flush();

        var rows = Enumerable.Range(0, 6).Select(r => new AlbumRow
        {
            Albums = Enumerable.Range(0, Math.Max(1, vm.GridColumns)).Select(c => new Album
            {
                Id = Guid.NewGuid(), Name = $"Album {r}-{c}", Artist = "Artist", Year = 2020,
                ArtworkPath = Path($"cover-{r}-{c}"), Tracks = new List<Track>(),
            }).ToList(),
        });
        foreach (var row in rows) vm.FilteredAlbumRows.Add(row);
        await Flush();

        var tiles = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        Assert.True(tiles.Count >= 10, $"only {tiles.Count} tiles realized");

        // Every tile's context menu draws the same few PNGs: one decoded bitmap per icon,
        // not nine fresh decodes per tile each time a row is realized.
        var iconSources = tiles
            .SelectMany(t => t.ContextMenu!.Items.OfType<MenuItem>())
            .Select(m => (m.Icon as Border)?.OpacityMask)
            .OfType<ImageBrush>()
            .Select(b => b.Source)
            .ToList();
        Assert.True(iconSources.Count >= tiles.Count * 7, $"expected the menu icons, found {iconSources.Count}");
        Assert.True(iconSources.Distinct().Count() <= 7,
            $"{iconSources.Distinct().Count()} distinct icon bitmaps for {tiles.Count} tiles: the menu PNGs are decoded per tile");

        // Covers are decoded for the tile they fill (bucketed device width), not the 768 cap.
        var cover = view.GetVisualDescendants().OfType<CachedImage>().First(i => i.Bounds.Width > 100);
        var bucket = ArtworkCache.NormalizeDecodeWidth((int)Math.Ceiling(cover.Bounds.Width * window.RenderScaling));
        Assert.NotEmpty(_decoded);
        Assert.All(_decoded, d => Assert.True(d.Width <= bucket, $"{d.Path} decoded at {d.Width} for a {cover.Bounds.Width:0}px tile"));
        window.Close();
    }

    private sealed class NoOpPlayHistoryStub : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    /// <summary>Jobs, a render tick (which runs layout headless) and a yield for the pool-thread decodes.</summary>
    private static async Task Flush()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
