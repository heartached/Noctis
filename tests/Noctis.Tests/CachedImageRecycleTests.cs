using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
/// "Some album covers never load" (09-24): the lead was CachedImage's background decode,
/// which since f3685f7 skips a tile that moved on before its turn — a recycled container
/// coming back to the same cover while its decode was queued or running could have been
/// left blank. These drive exactly those sequences (a detach/re-attach mid-decode, source
/// churn with decodes queued behind each other, the real Albums grid scrolled away and back
/// while decodes are in flight) and require every attached tile to end up showing its OWN
/// cover, alive. They pass on f3685f7 as well: the skip is not what blanked the owner's
/// tiles (those albums have no artwork in their files — see the session log line
/// "artwork: N album(s) have no embedded picture…").
/// </summary>
[Collection("ArtworkCache")]
public class CachedImageRecycleTests : IDisposable
{
    /// <summary>Which path each decoded bitmap was made for, to catch a wrong cover as well as a missing one.</summary>
    private readonly ConcurrentDictionary<Bitmap, string> _madeFor = new(ReferenceEqualityComparer.Instance);

    public CachedImageRecycleTests()
    {
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
        ArtworkCache.MaxCacheBytes = 128L * 1024 * 1024;
    }

    private static string P(string name) => System.IO.Path.Combine("C:\\", "art", name + ".jpg");

    private Bitmap Make(string path, int width)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        _madeFor[bitmap] = path;
        return bitmap;
    }

    private static bool IsDisposed(Bitmap bitmap)
    {
        try { _ = bitmap.PixelSize; return false; }
        catch (ObjectDisposedException) { return true; }
        catch (NullReferenceException) { return true; }
    }

    private void AssertShowsItsOwnCover(CachedImage image)
    {
        var shown = Assert.IsAssignableFrom<Bitmap>(image.Source);
        Assert.False(IsDisposed(shown), $"{image.SourcePath}: shows a disposed bitmap");
        Assert.True(_madeFor.TryGetValue(shown, out var madeFor), $"{image.SourcePath}: shows a bitmap the decoder never made");
        Assert.Equal(image.SourcePath, madeFor);
    }

    [AvaloniaFact]
    public async Task DetachedWhileItsDecodeRuns_ReattachedToTheSameCover_ShowsIt()
    {
        using var gate = new ManualResetEventSlim(false);
        var calls = 0;
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            if (Interlocked.Increment(ref calls) == 1) gate.Wait(5000);
            return Make(path, width);
        };
        var host = new Panel();
        var window = new Window { Width = 400, Height = 400, Content = host };
        window.Show();
        var image = new CachedImage { Width = 200, Height = 200, DecodeWidth = 256 };
        host.Children.Add(image);
        await Flush();

        image.SourcePath = P("a");
        await Flush();
        Assert.Null(image.Source); // the first decode is parked in the decoder

        // A recycled container: the detach moves the load generation on, and the same
        // cover comes straight back while that first decode is still running.
        host.Children.Remove(image);
        host.Children.Add(image);
        await Flush();
        gate.Set();
        await Flush();

        AssertShowsItsOwnCover(image);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SourceChurn_WithDecodesQueuedBehindEachOther_SettlesOnEachTilesOwnCover()
    {
        // Slow decodes on a narrow lane, so work queues and many decodes find their tile
        // already moved on (the skip) — the same pressure a wheel glide puts on the pool.
        using var lane = new SemaphoreSlim(2);
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            lane.Wait();
            try { Thread.Sleep(3); return Make(path, width); }
            finally { lane.Release(); }
        };
        // A budget of a few covers: entries are evicted (and re-decoded) all the time, like a
        // long grid scroll past the byte budget.
        ArtworkCache.MaxCacheBytes = 6L * 128 * 128 * 4;
        var host = new WrapPanel();
        var window = new Window { Width = 900, Height = 900, Content = host };
        window.Show();
        var images = Enumerable.Range(0, 24).Select(_ => new CachedImage { Width = 120, Height = 120, DecodeWidth = 128 }).ToList();
        foreach (var i in images) host.Children.Add(i);
        await Flush();

        var rnd = new Random(24);
        for (var round = 0; round < 40; round++)
        {
            foreach (var image in images)
            {
                switch (rnd.Next(4))
                {
                    case 0: image.SourcePath = P($"c{rnd.Next(10)}"); break;               // rebound to another album (often one it showed before)
                    case 1: host.Children.Remove(image); host.Children.Add(image); break;   // recycled back to the same album
                    case 2: image.SourcePath = null; image.SourcePath = P($"c{rnd.Next(10)}"); break;
                }
            }
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        await Flush(settleMs: 1500);

        foreach (var image in images.Where(i => i.SourcePath != null))
            AssertShowsItsOwnCover(image);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AlbumsGrid_ScrolledAwayAndBack_WhileCoversDecode_EveryTileLoadsItsCover()
    {
        EnsureAppStyles();
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            Thread.Sleep(15); // a real cover decode is 80+ ms; enough to still be running mid-scroll
            return Make(path, width);
        };
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        await Flush();

        var columns = Math.Max(1, vm.GridColumns);
        for (var r = 0; r < 40; r++)
        {
            vm.FilteredAlbumRows.Add(new AlbumRow
            {
                Albums = Enumerable.Range(0, columns).Select(c => new Album
                {
                    Id = Guid.NewGuid(), Name = $"Album {r}-{c}", Artist = "Artist", Year = 2020,
                    // A few albums repeat (same cover file on two albums), like a deluxe edition.
                    ArtworkPath = P($"cover-{(r * columns + c) % 150}"), Tracks = new List<Track>(),
                }).ToList(),
            });
        }
        await Flush();
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Extent.Height > s.Viewport.Height);

        // Glide down and straight back up without letting the decodes finish, so rows are
        // recycled while their covers are queued or mid-decode.
        var max = scroller.Extent.Height - scroller.Viewport.Height;
        for (var y = 0.0; y <= max; y += 90) Step(scroller, window, y);
        for (var y = max; y >= 0; y -= 90) Step(scroller, window, y);
        Step(scroller, window, 0);
        await Flush(settleMs: 2500);

        var tiles = view.GetVisualDescendants().OfType<CachedImage>()
            .Where(i => i.IsEffectivelyVisible && !string.IsNullOrEmpty(i.SourcePath) && i.Bounds.Width > 100)
            .ToList();
        Assert.True(tiles.Count >= columns * 2, $"only {tiles.Count} tiles realized");
        foreach (var tile in tiles)
            AssertShowsItsOwnCover(tile);
        window.Close();
    }

    private static void Step(ScrollViewer scroller, Window window, double y)
    {
        scroller.Offset = new Vector(0, y);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
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

    /// <summary>Jobs, render ticks (layout runs in them headless) and real time for the pool-thread decodes.</summary>
    private static async Task Flush(int settleMs = 120)
    {
        var until = DateTime.UtcNow.AddMilliseconds(settleMs);
        do
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }
}
