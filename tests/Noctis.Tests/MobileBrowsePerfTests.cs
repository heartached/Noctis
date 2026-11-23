using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone testers (11-22), "the app is still laggy": what Home, the album and artist pages and the
/// covers cost on a big library, measured headless (desktop CPU; a mid-range phone core is several
/// times slower). Each test holds one of the fixes to its number.
/// </summary>
public class MobileBrowsePerfTests
{
    private readonly ITestOutputHelper _out;
    public MobileBrowsePerfTests(ITestOutputHelper output) => _out = output;

    /// <summary>Every Home rail full (twelve tiles each), as on a phone with a long play log.</summary>
    private static void FillHomeRails(MobileFixtures.Rig rig)
    {
        var lib = rig.Shell.Library;
        var albums = rig.Library.Albums.Select(RailItem.ForAlbum).ToList();
        var songs = rig.Library.TrackList.Select(RailItem.ForTrack).ToList();
        var artists = rig.Library.ArtistList.Select(a => RailItem.ForArtist(a, null)).ToList();
        lib.PinnedRail.ReplaceAll(albums.Skip(0).Take(12));
        lib.RecentlyPlayedRail.ReplaceAll(albums.Skip(12).Take(12));
        lib.OnRepeatRail.ReplaceAll(songs.Skip(0).Take(12));
        lib.MostPlayedRail.ReplaceAll(songs.Skip(12).Take(12));
        lib.TopArtistsRail.ReplaceAll(artists.Take(12));
        lib.DownloadedRail.ReplaceAll(albums.Skip(24).Take(12));
        lib.HasPinned = lib.HasShelf = lib.HasOnRepeat = lib.HasMostPlayed = lib.HasTopArtists = lib.HasDownloaded = true;
    }

    private static List<RailTile> Tiles(Visual root) =>
        root.GetVisualDescendants().OfType<RailTile>().Where(t => t.IsVisible).ToList();

    /// <summary>
    /// Home, the start tab: each rail was a plain horizontal panel, so every tile of every rail
    /// (up to 84) was built, styled and loaded its cover as the app came up, though a screen shows
    /// three and a peek of each of the first few rails. Rails now virtualise sideways; a new pin
    /// still glides in.
    /// </summary>
    [AvaloniaFact]
    public void Home_BuildsOnlyTheRailTilesOnScreen_AndANewPinStillGlidesIn()
    {
        using (var warm = MobileSearchPerfTests.BigLibrary(1_200, 100))   // warm-up: the JIT, the styles
        {
            FillHomeRails(warm);
            MobileFixtures.Mount(warm.Shell, out _).Close();
        }
        using var rig = MobileSearchPerfTests.BigLibrary(4_800, 300);
        FillHomeRails(rig);
        var start = Stopwatch.GetTimestamp();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Dispatcher.UIThread.RunJobs();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var home = MobileFixtures.Find<HomePage>(view);
        var tiles = Tiles(home);
        _out.WriteLine($"home, 6 rails x 12: {tiles.Count} tiles built, {ms:F0} ms to the first frame");
        // Before: 72 tiles (every item of every rail), 679 ms; after: 14, 191 ms. The first three rails fill the screen.
        Assert.InRange(tiles.Count, 6, 40);

        // The tiles on screen as Home comes up glide in (Appear), as before.
        Assert.All(tiles, t => Assert.NotNull(((Control)t.Content!).Transitions));

        // Every rail keeps its height as Home scrolls down to it (a rail off screen still measures
        // a tile), and shows its tiles there.
        string[] rails = { "PinnedRail", "RecentlyPlayedRail", "OnRepeatRail", "MostPlayedRail", "TopArtistsRail", "DownloadedRail" };
        var heights = rails.Select(r => MobileFixtures.Named<ItemsControl>(home, r).Bounds.Height).ToList();
        Assert.All(heights, h => Assert.True(h > 150, $"rail {h} dp tall"));
        var homeScroll = MobileFixtures.Named<ScrollViewer>(home, "HomeScroll");
        homeScroll.Offset = new Avalonia.Vector(0, homeScroll.Extent.Height);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(heights, rails.Select(r => MobileFixtures.Named<ItemsControl>(home, r).Bounds.Height));
        Assert.InRange(Tiles(MobileFixtures.Named<ItemsControl>(home, "DownloadedRail")).Count, 3, 12);
        homeScroll.Offset = default;
        window.UpdateLayout();

        // A rail scrolled sideways shows its later tiles, in order, each opening its own.
        var rail = MobileFixtures.Named<ItemsControl>(home, "RecentlyPlayedRail");
        var railScroll = rail.FindAncestorOfType<ScrollViewer>()!;
        var railTiles = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var scrollReplays = 0;
        void Watch()
        {
            foreach (var t in Tiles(rail))
                if (railTiles.Add((Control)t.Content!))
                    ((Control)t.Content!).PropertyChanged += (_, e) =>
                    {
                        if (e.Property == Avalonia.Animation.Animatable.TransitionsProperty && e.NewValue == null) scrollReplays++;
                    };
        }
        Watch();
        for (var x = 0.0; x <= railScroll.Extent.Width; x += 60)
        {
            railScroll.Offset = new Avalonia.Vector(x, 0);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Watch();
        }
        Assert.Equal(0, scrollReplays);   // a tile scrolled on had arrived already
        Assert.InRange(railTiles.Count, 3, 8);
        var last = rail.GetRealizedContainers().OrderBy(rail.IndexFromContainer).Last();
        var button = last.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row"));
        Assert.Same(rig.Shell.Library.RecentlyPlayedRail[^1], button.CommandParameter);
        Assert.Same(rig.Shell.OpenRailItemCommand, button.Command);

        // Pinning an album glides the Pinned rail's tiles in again (the rail is handed a new list),
        // as the plain panel's rebuilt tiles did.
        var pinned = MobileFixtures.Named<ItemsControl>(home, "PinnedRail");
        var replayed = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        foreach (var t in Tiles(pinned))
        {
            var content = (Control)t.Content!;
            content.Transitions = new Avalonia.Animation.Transitions();   // its arrival long finished
            seen.Add(content);
            content.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Animation.Animatable.TransitionsProperty && e.NewValue == null) replayed.Add(content);
            };
        }
        var next = rig.Shell.Library.PinnedRail.ToList();
        next.Insert(0, RailItem.ForAlbum(rig.Library.Albums[100]));
        rig.Shell.Library.PinnedRail.ReplaceAll(next);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var first = pinned.GetRealizedContainers().OrderBy(pinned.IndexFromContainer).First();
        Assert.Same(next[0], first.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row")).CommandParameter);
        var pinnedTiles = Tiles(pinned).Select(t => (Control)t.Content!).ToList();
        var glided = pinnedTiles.Count(c => seen.Contains(c) ? replayed.Contains(c) : c.Transitions != null);
        _out.WriteLine($"new pin: {glided} of {pinnedTiles.Count} pinned tiles glided in");
        Assert.Equal(pinnedTiles.Count, glided);
        window.Close();
    }

    /// <summary>A box set: <paramref name="discs"/> discs of <paramref name="perDisc"/> songs.</summary>
    private static (MobileFixtures.Rig Rig, Noctis.Models.Album Album) BoxSet(int discs, int perDisc)
    {
        var tracks = new List<Noctis.Models.Track>();
        for (var d = 1; d <= discs; d++)
            for (var n = 1; n <= perDisc; n++)
            {
                var t = MobileFixtures.Song($"Song {d}-{n}", "Box Artist");
                t.DiscNumber = d;
                t.TrackNumber = n;
                tracks.Add(t);
            }
        var album = MobileFixtures.MakeAlbum("The Box", "Box Artist", tracks.ToArray());
        return (MobileFixtures.MakeRig(tracks.ToArray(), new[] { album }), album);
    }

    /// <summary>
    /// An album page's songs, a box set's 150 over five discs: the list mixes disc headers and
    /// songs, so it was a plain virtualised ItemsControl, which rebuilt every row that scrolled on
    /// (its Button, check, number, bars, title, mark, ⋯ and every binding). Rows are now built once
    /// per kind and handed the next item of that kind.
    /// </summary>
    [AvaloniaFact]
    public void AlbumPage_ReusesItsRows_AsABoxSetScrolls_AndTheyStillPlayTheirSong()
    {
        var (rig, album) = BoxSet(5, 30);
        using var _ = rig;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        MobileFixtures.Land(window);
        Dispatcher.UIThread.RunJobs();
        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        var page = MobileFixtures.Find<AlbumPage>(view);
        var list = MobileFixtures.Named<ItemsControl>(page, "TrackList");
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "AlbumScroll");
        Assert.Equal(155, vm.Rows.Count);

        List<Button> RowButtons() => list.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("row") && b.IsEffectivelyVisible).ToList();
        var built = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var headers = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        void Watch()
        {
            foreach (var b in RowButtons()) built.Add(b);
            foreach (var t in list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.DataContext is AlbumDiscHeader)) headers.Add(t);
        }
        void ScrollTo(double y)
        {
            scroll.Offset = new Avalonia.Vector(0, y);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        Watch();
        for (var y = 0.0; y <= scroll.Extent.Height; y += 100) { ScrollTo(y); Watch(); }   // warm-up pass
        ScrollTo(0);
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var steps = 0;
        for (var y = 0.0; y <= scroll.Extent.Height; y += 100, steps++) { ScrollTo(y); Watch(); }
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / steps;
        var perStep = (GC.GetAllocatedBytesForCurrentThread() - alloc) / steps;
        _out.WriteLine($"box set, 150 songs on 5 discs, scrolled twice: {built.Count} song rows and {headers.Count} disc headers built, {ms:F1} ms and {perStep / 1024} KB a 100 dp step");
        // Before: 334 song rows and 10 disc headers built, 19.1 ms and 948 KB a step; after: 20, 1, 3.1 ms, 228 KB.
        Assert.InRange(built.Count, 10, 40);
        Assert.InRange(headers.Count, 1, 6);

        // At the end the rows on screen are the last songs, in order, and a tap plays its own;
        // the last disc's header still reads as a header.
        var shown = list.GetRealizedContainers().OrderBy(list.IndexFromContainer)
            .Select(c => c.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.DataContext is AlbumTrackRow or AlbumDiscHeader)?.DataContext)
            .ToList();
        var from = list.IndexFromContainer(list.GetRealizedContainers().OrderBy(list.IndexFromContainer).First());
        Assert.Equal(vm.Rows.Skip(from).Take(shown.Count), shown);
        var lastRow = RowButtons().OrderBy(b => b.TranslatePoint(default, page)!.Value.Y).Last();
        Assert.Same(vm.Rows[^1], lastRow.CommandParameter);
        Assert.Same(vm.PlayTrackCommand, lastRow.Command);
        Assert.Equal("Song 5-30", MobileFixtures.Named<TextBlock>(lastRow, "RowTitle").Text);
        window.Close();
    }

    /// <summary>
    /// A prolific artist's page (60 albums, 40 singles, 20 top songs): its rails virtualised
    /// sideways over a plain ItemsControl, which rebuilt every tile that scrolled on (and every
    /// Top Songs column, with its four rows). Tiles and columns are now built once and handed the
    /// next item.
    /// </summary>
    [AvaloniaFact]
    public void ArtistPage_ReusesItsRailTiles_AsTheyScroll_AndTheyStillOpenTheirAlbum()
    {
        var tracks = new List<Noctis.Models.Track>();
        var albums = new List<Noctis.Models.Album>();
        for (var a = 0; a < 100; a++)
        {
            var count = a < 60 ? 10 : 1;
            var own = Enumerable.Range(1, count).Select(n =>
            {
                var t = MobileFixtures.Song($"Song {a}-{n}", "Prolific", plays: (a * 7 + n) % 40);
                t.TrackNumber = n;
                return t;
            }).ToArray();
            tracks.AddRange(own);
            var album = MobileFixtures.MakeAlbum($"Release {a}", "Prolific", own);
            album.Year = 1950 + a;
            albums.Add(album);
        }
        using var rig = MobileFixtures.MakeRig(tracks.ToArray(), albums.ToArray());
        rig.Library.ArtistList.Add(new Noctis.Models.Artist { Id = Guid.NewGuid(), Name = "Prolific", TrackCount = tracks.Count });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Prolific");
        MobileFixtures.Land(window);
        Dispatcher.UIThread.RunJobs();
        var vm = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(60, vm.Albums.Count);
        Assert.Equal(40, vm.Singles.Count);
        var page = MobileFixtures.Find<ArtistPage>(view);

        var built = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        void Watch(ItemsControl rail)
        {
            foreach (var b in rail.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("row"))) built.Add(b);
        }
        long alloc = 0;
        var steps = 0;
        foreach (var name in new[] { "AlbumRail", "SingleRail", "TopSongPages", "AlbumRail", "SingleRail", "TopSongPages" })
        {
            var rail = MobileFixtures.Named<ItemsControl>(page, name);
            var scroll = rail.FindAncestorOfType<ScrollViewer>()!;
            if (steps == 0) alloc = GC.GetAllocatedBytesForCurrentThread();
            for (var x = 0.0; x <= scroll.Extent.Width; x += 80, steps++)
            {
                scroll.Offset = new Vector(x, 0);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Watch(rail);
            }
        }
        var perStep = (GC.GetAllocatedBytesForCurrentThread() - alloc) / steps;
        _out.WriteLine($"artist page, 60 albums + 40 singles + 20 top songs, each rail scrolled to its end twice: {built.Count} tiles and rows built, {perStep / 1024} KB an 80 dp step");
        // Before: 161 tiles and rows built, 179 KB a step; after: 17, 49 KB.
        Assert.InRange(built.Count, 10, 60);

        // At the end the tiles on screen are the last releases, in order, each opening its own.
        var albumRail = MobileFixtures.Named<ItemsControl>(page, "AlbumRail");
        var last = albumRail.GetRealizedContainers().OrderBy(albumRail.IndexFromContainer).Last();
        var tile = last.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row"));
        Assert.Same(vm.Albums[^1].Album, tile.CommandParameter);
        Assert.Same(rig.Shell.OpenAlbumCommand, tile.Command);
        var songs = MobileFixtures.Named<ItemsControl>(page, "TopSongPages");
        var column = songs.GetRealizedContainers().OrderBy(songs.IndexFromContainer).Last();
        var rows = column.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("row")).Select(b => b.CommandParameter).ToList();
        Assert.Equal(vm.TopSongPages[^1].Rows, rows);
        window.Close();
    }
}

/// <summary>
/// One cover on two surfaces of close sizes (the Albums grid's 180 dp tile, then Search's 140 dp
/// album row and Home's rail): each tile decodes at exactly the pixels it draws (PixelExact), so
/// each read and decoded the cover file again. A smaller size is now made from the larger decode
/// already in memory, resampled as sharply, with no second file decode.
/// </summary>
[Collection("ArtworkCache")]   // the decoder seam is process-wide
public class MobileCoverDecodeTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<(string Path, int Width)> _decodes = new();

    public MobileCoverDecodeTests(ITestOutputHelper output)
    {
        _out = output;
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            lock (_decodes) _decodes.Add((path, width));
            return new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(width, width), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        };
    }

    public void Dispose()
    {
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.ClearForTests();
    }

    private static void Settle(Window window)
    {
        var deadline = Environment.TickCount64 + 30_000;
        var quiet = 0;
        while (Environment.TickCount64 < deadline && quiet < 10)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
            System.Threading.Thread.Sleep(20);
            quiet = Noctis.Controls.CachedImage.RunningDecodes == 0 ? quiet + 1 : 0;
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ACoverShownOnAlbumsThenSearch_IsDecodedFromItsFileOnce()
    {
        using var rig = MobileSearchPerfTests.BigLibrary(1_200, 100);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Settle(window);
        HashSet<string> early;   // Library's Recently Added rail (built with the shell) decodes its covers first
        lock (_decodes) early = _decodes.Select(d => d.Path).ToHashSet();
        rig.Shell.OpenAlbumsCommand.Execute(null);
        MobileFixtures.Land(window);
        Settle(window);
        var onAlbums = MobileFixtures.Find<AlbumGridPage>(view).GetVisualDescendants().OfType<Noctis.Controls.CachedImage>()
            .Where(i => i.IsEffectivelyVisible && i.SourcePath != null).Select(i => i.SourcePath!).ToHashSet();
        int albumsDecodes;
        lock (_decodes) albumsDecodes = _decodes.Count;

        rig.Shell.SelectTabCommand.Execute(MobileTab.Search);
        MobileFixtures.Land(window);
        var search = rig.Shell.Search;
        var shownAlbum = rig.Library.Albums.First(a => onAlbums.Contains(a.ArtworkPath!) && !early.Contains(a.ArtworkPath!));
        search.Query = shownAlbum.Name;
        window.UpdateLayout();
        Settle(window);
        var onSearch = MobileFixtures.Find<SearchPage>(view).GetVisualDescendants().OfType<Noctis.Controls.CachedImage>()
            .Where(i => i.IsEffectivelyVisible && i.SourcePath != null).Select(i => i.SourcePath!).ToHashSet();
        var both = onAlbums.Intersect(onSearch).Where(p => !early.Contains(p)).ToList();
        List<(string Path, int Width)> decodes;
        lock (_decodes) decodes = _decodes.ToList();
        var twice = both.Count(p => decodes.Count(d => d.Path == p) > 1);
        _out.WriteLine($"Albums then Search: {albumsDecodes} + {decodes.Count - albumsDecodes} file decodes, {both.Count} covers on both, "
                       + $"{twice} of them decoded twice; cache {ArtworkCache.Count} bitmaps, {ArtworkCache.ResidentBytes / 1024} KB");
        foreach (var p in both) _out.WriteLine($"  {p}: decoded at {string.Join(", ", decodes.Where(d => d.Path == p).Select(d => d.Width))}");
        Assert.Contains(shownAlbum.ArtworkPath!, both);
        // Before: decoded at 180, then again at 140 for Search's album row.
        Assert.Equal(0, twice);
        window.Close();
    }

    /// <summary>
    /// A size made from a larger decode is as sharp as a decode of the file at that size: a
    /// detailed 2400 px cover decoded for a 180 dp tile at 2.625x (473 px), then made 368 px for a
    /// 140 dp tile, against the file decoded at 368 px (SkiaSharp itself; no Avalonia rendering).
    /// </summary>
    [Fact]
    public void ASizeMadeFromALargerDecode_IsAsSharpAsAFileDecode()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"noctis-cover-{Guid.NewGuid():N}.png");
        try
        {
            var rng = new Random(3);
            using (var art = new SkiaSharp.SKBitmap(2400, 2400))
            {
                using (var canvas = new SkiaSharp.SKCanvas(art))
                {
                    canvas.Clear(new SkiaSharp.SKColor(30, 60, 120));
                    using var paint = new SkiaSharp.SKPaint { IsAntialias = true, StrokeWidth = 7 };
                    for (var i = 0; i < 900; i++)   // thin strokes: cover text and line art
                    {
                        paint.Color = new SkiaSharp.SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                        canvas.DrawLine(rng.Next(2400), rng.Next(2400), rng.Next(2400), rng.Next(2400), paint);
                    }
                }
                using var data = art.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                System.IO.File.WriteAllBytes(file, data.ToArray());
            }
            using var reference = Noctis.Helpers.SkiaArtworkDecoder.DecodeToWidth(file, 368)!;
            using var larger = Noctis.Helpers.SkiaArtworkDecoder.DecodeToWidth(file, 473)!;
            using var made = ArtworkCache.Resized(larger, 368);
            // What drawing the 473 px decode into the 368 px tile would do (the renderer's trilinear).
            using var drawn = Noctis.Helpers.SkiaArtworkDecoder.Downscale(larger, made.Info);
            Assert.Equal(reference.Width, made.Width);
            Assert.Equal(reference.Height, made.Height);
            double Psnr(SkiaSharp.SKBitmap a)
            {
                double sum = 0;
                for (var y = 0; y < a.Height; y++)
                    for (var x = 0; x < a.Width; x++)
                    {
                        var p = a.GetPixel(x, y);
                        var q = reference.GetPixel(x, y);
                        sum += Math.Pow(p.Red - q.Red, 2) + Math.Pow(p.Green - q.Green, 2) + Math.Pow(p.Blue - q.Blue, 2);
                    }
                return 10 * Math.Log10(255.0 * 255 / (sum / (a.Width * a.Height * 3)));
            }
            double Sharpness(SkiaSharp.SKBitmap a)
            {
                double sum = 0;
                for (var y = 0; y < a.Height; y++)
                    for (var x = 1; x < a.Width; x++)
                        sum += Math.Abs(a.GetPixel(x, y).Green - a.GetPixel(x - 1, y).Green);
                return sum / (a.Height * (a.Width - 1));
            }
            var (madePsnr, drawnPsnr) = (Psnr(made), Psnr(drawn));
            var (madeEdge, drawnEdge, refEdge) = (Sharpness(made), Sharpness(drawn), Sharpness(reference));
            _out.WriteLine($"368 px from the 473 px decode vs the file decoded at 368: {madePsnr:F1} dB, edge contrast {madeEdge / refEdge:P0}"
                           + $" (the 473 drawn shrunk: {drawnPsnr:F1} dB, {drawnEdge / refEdge:P0})");
            // 29.6 dB and 105% of the file decode's edge contrast; drawing the 473 px decode shrunk: 29.2 dB, 90%.
            Assert.InRange(madeEdge / refEdge, 0.95, 1.15);   // neither softer nor ringing
            Assert.True(madePsnr >= drawnPsnr, $"{madePsnr:F1} dB");
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }
}
