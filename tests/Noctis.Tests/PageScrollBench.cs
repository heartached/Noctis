using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Main-window page benchmark (11-14, "reduce lag when I'm scrolling around pages"): the real
/// app (App.Initialize, ConfigureServices, MainWindow, real LibraryService) over a synthetic
/// library written to the run's throw-away data root (NOCTIS_PERF_TRACKS songs, default 10000,
/// 12 a album, skewed albums per artist, a 600 px cover per album unless NOCTIS_PERF_COVERS=0,
/// 30 playlists, one of 3000 songs). For each page: time from Navigate to the last change of
/// its visual tree (first and second visit), visuals on the page, then a wheel glide (40 px a
/// frame), a flick (600 px a frame) and jumps through its main scroller: UI-thread ms per frame
/// (jobs + render tick, which runs layout headless), UI-thread bytes allocated per frame, and
/// controls loaded per frame (new, not recycled, containers and their templates).
/// NOCTIS_PERF_ROWS=1 also prints the visible rows and the first row's layout (to compare two builds).
/// Explicit only: NOCTIS_PERF_HARNESS=1; NOCTIS_PERF_PAGES=songs,albums,... picks pages;
/// NOCTIS_PERF_OUT/NOCTIS_PERF_TAG append "tag\tline" for comparing runs.
///   Noctis.Tests.exe -method "Noctis.Tests.PageScrollBench.Pages_OpenAndScroll"
/// Headless drawing stubs: frame numbers are layout + composition, not rasterization.
/// </summary>
public class PageScrollBench
{
    private readonly ITestOutputHelper _o;
    public PageScrollBench(ITestOutputHelper o) => _o = o;

    private static bool Enabled => Environment.GetEnvironmentVariable("NOCTIS_PERF_HARNESS") == "1";
    private static int TrackCount => int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_PERF_TRACKS"), out var n) && n > 0 ? n : 10000;

    private void Log(string line)
    {
        _o.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("NOCTIS_PERF_OUT");
        if (!string.IsNullOrEmpty(path))
            File.AppendAllText(path, (Environment.GetEnvironmentVariable("NOCTIS_PERF_TAG") ?? "?") + "\t" + line + "\n");
    }

    // ── synthetic library ──

    private static readonly string[] Genres =
        ["Rock", "Pop", "Hip-Hop/Rap", "Electronic", "Jazz", "Classical", "R&B/Soul", "Metal", "Folk", "Indie",
         "Ambient", "Soundtrack", "Country", "Blues", "Reggae", "Punk", "Latin", "K-Pop", "Funk", "Alternative"];

    internal static (List<Track> Tracks, List<Playlist> Playlists) WriteLibrary(int trackCount, bool covers)
    {
        var rnd = new Random(1234);
        var persistence = new PersistenceService();
        var root = Path.Combine(Path.GetTempPath(), "noctis-pagebench-music");
        var tracks = new List<Track>(trackCount);
        byte[]? cover = null;
        if (covers)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "noctis-pagebench-cover-" + Guid.NewGuid().ToString("N") + ".jpg");
            HiDpiGridScrollCostTests.WriteCover(tmp, 600, 7, SKEncodedImageFormat.Jpeg);
            cover = File.ReadAllBytes(tmp);
            File.Delete(tmp);
        }
        var albums = (trackCount + 11) / 12;
        var artists = Math.Max(10, albums / 3);
        var now = DateTime.UtcNow;
        for (var a = 0; a < albums; a++)
        {
            // Skewed: a few artists own many albums, most own one or two.
            var artistIndex = (int)(Math.Pow(rnd.NextDouble(), 2.2) * artists);
            var artist = $"Artist {artistIndex:000} {(char)('A' + artistIndex % 26)}{(char)('a' + artistIndex % 23)}nd";
            var album = $"Album {a:0000} {(char)('A' + a % 26)}ight {(a % 7 == 0 ? "(Deluxe Edition)" : "")}".Trim();
            var albumId = Track.ComputeAlbumId(artist, album);
            var year = 1965 + rnd.Next(60);
            var genre = Genres[rnd.Next(Genres.Length)];
            var count = Math.Min(12, trackCount - tracks.Count);
            var added = now.AddDays(-rnd.Next(1500));
            for (var t = 0; t < count; t++)
            {
                var feat = rnd.Next(8) == 0 ? $" feat. Artist {rnd.Next(artists):000} Guest" : "";
                var played = rnd.Next(3) == 0;
                tracks.Add(new Track
                {
                    Title = $"Song {tracks.Count:00000} {(char)('a' + t)}way the night{(rnd.Next(5) == 0 ? " (Remastered 2011)" : "")}",
                    Artist = artist + feat,
                    AlbumArtist = artist,
                    Album = album,
                    AlbumId = albumId,
                    TrackNumber = t + 1,
                    TrackCount = count,
                    DiscNumber = 1,
                    Year = year,
                    Genre = genre,
                    Duration = TimeSpan.FromSeconds(150 + rnd.Next(200)),
                    FilePath = Path.Combine(root, artist, album, $"{t + 1:00} Song {tracks.Count}.flac"),
                    FileSize = 30_000_000,
                    DateAdded = added,
                    PlayCount = played ? rnd.Next(1, 80) : 0,
                    LastPlayed = played ? now.AddHours(-rnd.Next(24 * 200)) : null,
                    IsFavorite = rnd.Next(12) == 0,
                    Rating = rnd.Next(6) == 0 ? rnd.Next(1, 6) : 0,
                    Bitrate = 900 + rnd.Next(500),
                    SampleRate = 44100,
                    BitsPerSample = 16,
                    Codec = "FLAC",
                });
            }
            if (cover != null) persistence.SaveArtwork(albumId, cover);
        }
        foreach (var t in tracks) if (t.IsFavorite) t.FavoritedAt = now.AddDays(-rnd.Next(400));
        persistence.SaveLibraryAsync(tracks).GetAwaiter().GetResult();

        var playlists = new List<Playlist>();
        for (var p = 0; p < 30; p++)
        {
            var size = p == 0 ? Math.Min(3000, tracks.Count) : 10 + rnd.Next(150);
            var pl = new Playlist { Name = p == 0 ? "Big Mix" : $"Playlist {p}" };
            foreach (var t in tracks.OrderBy(_ => rnd.Next()).Take(size)) pl.TrackIds.Add(t.Id);
            playlists.Add(pl);
        }
        persistence.SavePlaylistsAsync(playlists).GetAwaiter().GetResult();
        return (tracks, playlists);
    }

    // ── the app's own App.Initialize, as PerfAuditHarnessTests ──

    private static bool _appLoaded;

    internal static void LoadAppAsTheApp()
    {
        if (_appLoaded) return;
        _appLoaded = true;
        var app = new App();
        app.Initialize();
        var current = Application.Current!;
        current.RequestedThemeVariant = ThemeVariant.Dark;
        foreach (var style in app.Styles.Where(s => s is not Avalonia.Themes.Fluent.FluentTheme).ToList())
        {
            app.Styles.Remove(style);
            current.Styles.Add(style);
        }
        foreach (var template in app.DataTemplates.ToList())
        {
            app.DataTemplates.Remove(template);
            current.DataTemplates.Add(template);
        }
        var resources = app.Resources;
        foreach (var merged in resources.MergedDictionaries.ToList())
        {
            resources.MergedDictionaries.Remove(merged);
            current.Resources.MergedDictionaries.Add(merged);
        }
        foreach (var kv in resources.ToList())
            current.Resources[kv.Key] = kv.Value;
    }

    private static ServiceProvider BuildAppServices()
    {
        var services = new ServiceCollection();
        var program = typeof(App).Assembly.GetType("Noctis.Program")!;
        program.GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        services.AddSingleton<IAudioPlayer>(new FakeAudioPlayer());
        return services.BuildServiceProvider();
    }

    // ── pumping ──

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs = 120_000)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("page bench wait");
            Pump();
            Thread.Sleep(1);
        }
    }

    private static int _loaded;

    private static void CountLoaded(object? sender, RoutedEventArgs e) => _loaded++;

    private static Control? PageRoot(Window window, object? page) =>
        window.GetVisualDescendants().OfType<UserControl>().FirstOrDefault(c => ReferenceEquals(c.DataContext, page)
            && c.GetType().Namespace == "Noctis.Views");

    private static int CountVisuals(Visual? root) => root == null ? 0 : root.GetVisualDescendants().Count();

    /// <summary>Navigate, then pump until the page's tree stops changing for 300 ms; returns the
    /// time to its last change (tree walks excluded) and the visual count.</summary>
    private static (double Ms, int Visuals, int Loaded) Settle(Window window, Func<object?> page, Action navigate)
    {
        var walk = TimeSpan.Zero;
        var loaded0 = _loaded;
        var sw = Stopwatch.StartNew();
        navigate();
        Pump();
        var last = sw.Elapsed;
        var w = Stopwatch.StartNew();
        var count = CountVisuals(PageRoot(window, page()));
        walk += w.Elapsed;
        var stableSince = sw.Elapsed;
        var lastLoaded = _loaded;
        while (sw.Elapsed - stableSince < TimeSpan.FromMilliseconds(300) && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(2);
            Pump();
            var now = sw.Elapsed;
            w.Restart();
            var c = CountVisuals(PageRoot(window, page()));
            walk += w.Elapsed;
            if (c != count || _loaded != lastLoaded)
            {
                count = c;
                lastLoaded = _loaded;
                last = now;
                stableSince = sw.Elapsed;
            }
        }
        return ((last - walk).TotalMilliseconds, count, _loaded - loaded0);
    }

    private sealed record Frames(string Kind, int N, double Mean, double P95, double Max, double KbPerFrame, double LoadedPerFrame, int Gc0, int Gc1, int Gc2,
        int Containers, int TemplateRoots);

    private static Frames? RunFrames(string kind, ScrollViewer scroller, IReadOnlyList<double> offsets)
    {
        if (offsets.Count < 2) return null;
        // Recycling check: every item container and every container's template root seen.
        var containers = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var roots = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        void Seen()
        {
            foreach (var c in scroller.GetVisualDescendants().OfType<ListBoxItem>())
            {
                containers.Add(c);
                if (c.Presenter?.Child is { } child) roots.Add(child);
            }
        }
        scroller.Offset = new Vector(scroller.Offset.X, offsets[0]);
        Pump(); Pump();
        var times = new List<double>(offsets.Count);
        long bytes = 0;
        var loaded0 = _loaded;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        for (var i = 1; i < offsets.Count; i++)
        {
            var b = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            scroller.Offset = new Vector(scroller.Offset.X, offsets[i]);
            Pump();
            times.Add(sw.Elapsed.TotalMilliseconds);
            bytes += GC.GetAllocatedBytesForCurrentThread() - b;
            Seen();
        }
        var sorted = times.OrderBy(x => x).ToList();
        return new Frames(kind, times.Count, times.Average(), sorted[(int)(sorted.Count * 0.95)], sorted[^1],
            bytes / 1024.0 / times.Count, (_loaded - loaded0) / (double)times.Count,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
            containers.Count, roots.Count);
    }

    private static ScrollViewer? MainScroller(Control? root)
    {
        if (root == null) return null;
        return root.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.Extent.Height - s.Viewport.Height > 200)
            .OrderByDescending(s => s.Viewport.Height * 10 + s.Extent.Height / 1e6)
            .FirstOrDefault();
    }

    private void ScrollPage(string name, Control? root)
    {
        var scroller = MainScroller(root);
        if (scroller == null)
        {
            Log($"{name}: no scroller with more than a screen of content");
            return;
        }
        var max = scroller.Extent.Height - scroller.Viewport.Height;
        var glide = new List<double>();
        for (var y = 0.0; y <= Math.Min(max, 6000); y += 40) glide.Add(y);
        var flick = new List<double>();
        for (var y = 0.0; y <= max && flick.Count < 40; y += 600) flick.Add(y);
        var jump = Enumerable.Range(0, 30).Select(i => max * ((i * 7919) % 30) / 30.0).ToList();
        Log($"{name}: scroller extent {scroller.Extent.Height:0} viewport {scroller.Viewport.Height:0}");
        if (Environment.GetEnvironmentVariable("NOCTIS_PERF_ROWS") == "1")
            Log($"{name}: visible rows " + string.Join(", ", scroller.GetVisualDescendants().OfType<ListBoxItem>()
                .Where(i => i.IsVisible).Select(i => $"{i.Bounds.Top:0}+{i.Bounds.Height:0}")));
        if (Environment.GetEnvironmentVariable("NOCTIS_PERF_ROWS") == "1"
            && scroller.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.IsVisible) is { } first)
        {
            Log($"{name}: first row chain " + string.Join(" > ", first.GetVisualDescendants().Take(12)
                .Select(v => $"{v.GetType().Name}[{v.Bounds.Width:0.#}x{v.Bounds.Height:0.#}]")));
            if (first.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Classes.Contains("tile-text")) is { } text)
                Log($"{name}: first tile text [{string.Join(" ", text.Classes)}] " + string.Join(" | ", text.GetVisualDescendants()
                    .Where(v => v.IsVisible).Select(v => $"{v.GetType().Name}{(v is StyledElement se && se.Classes.Count > 0 ? "." + string.Join(".", se.Classes) : "")}@{v.Bounds.Y:0.#}[{v.Bounds.Width:0.#}x{v.Bounds.Height:0.#}]")));
        }
        foreach (var (kind, offsets) in new[] { ("glide", glide), ("glide2", glide), ("flick", flick), ("jump", jump) })
        {
            if (RunFrames(kind, scroller, offsets) is not { } f) continue;
            Log($"{name}.{f.Kind}: {f.N} frames, mean {f.Mean:0.00} p95 {f.P95:0.00} max {f.Max:0.0} ms | {f.KbPerFrame:0.0} KB/frame | loaded {f.LoadedPerFrame:0.0}/frame | GC {f.Gc0}/{f.Gc1}/{f.Gc2}" +
                (f.Containers > 0 ? $" | containers {f.Containers}, template roots {f.TemplateRoots}" : ""));
        }
        scroller.Offset = default;
        Pump();
        RebindProbe(name, scroller);
    }

    /// <summary>What a row would cost if it stayed in the tree and only took the next item:
    /// every visible ListBoxItem gets another item as its Content while attached (the
    /// ContentPresenter keeps its template child), per row ms and bytes.</summary>
    private void RebindProbe(string name, ScrollViewer scroller)
    {
        var items = scroller.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsVisible).ToList();
        if (items.Count == 0 || items[0].Content is not { } first) return;
        var list = items[0].FindAncestorOfType<ListBox>();
        if (list?.ItemsSource is not System.Collections.IList source || source.Count < items.Count * 40) return;
        var roots = items.Select(i => i.Presenter?.Child).ToList();
        var times = 0.0; long bytes = 0; var loaded0 = _loaded; var n = 0;
        for (var round = 1; round <= 30; round++)
        {
            var b = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (var k = 0; k < items.Count; k++)
                items[k].Content = source[(round * items.Count + k) % source.Count];
            Pump();
            times += sw.Elapsed.TotalMilliseconds;
            bytes += GC.GetAllocatedBytesForCurrentThread() - b;
            n += items.Count;
        }
        var kept = items.Select(i => i.Presenter?.Child).Zip(roots).Count(p => ReferenceEquals(p.First, p.Second));
        Log($"{name}.rebind-probe: {n} row rebinds, {times / n:0.00} ms/row, {bytes / 1024.0 / n:0.0} KB/row, loaded {(_loaded - loaded0) / (double)n:0.0}/row, template roots kept {kept}/{items.Count}");
        // Put the rows back the way the panel had them.
        for (var k = 0; k < items.Count; k++) items[k].Content = first;
        scroller.Offset = new Vector(0, 1);
        Pump();
        scroller.Offset = default;
        Pump();
    }

    [AvaloniaFact]
    public void Pages_OpenAndScroll()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_PERF_HARNESS=1");
        var covers = Environment.GetEnvironmentVariable("NOCTIS_PERF_COVERS") != "0";
        var gen = Stopwatch.StartNew();
        // Off the UI thread: the persistence awaits would post back to the blocked dispatcher.
        var (tracks, playlists) = Task.Run(() => WriteLibrary(TrackCount, covers)).GetAwaiter().GetResult();
        Log($"library: {tracks.Count} tracks, {tracks.Select(t => t.AlbumId).Distinct().Count()} albums, " +
            $"{tracks.Select(t => t.AlbumArtist).Distinct().Count()} album artists, covers={covers} ({gen.ElapsedMilliseconds} ms to write)");

        LoadAppAsTheApp();
        Control.LoadedEvent.AddClassHandler(typeof(Control), CountLoaded, handledEventsToo: true);
        var provider = BuildAppServices();
        App.Services = provider;
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow(vm) { Width = 1600, Height = 1000 };
        window.Show();
        var library = provider.GetRequiredService<ILibraryService>();
        PumpUntil(() => library.Tracks.Count == tracks.Count && vm.CurrentView != null);
        // Let startup settle (plugins, background index work posting back).
        for (var i = 0; i < 100; i++) { Pump(); Thread.Sleep(10); }
        Log($"loaded: {library.Tracks.Count} tracks, {library.Albums.Count} albums, {library.Artists.Count} artists; start page {vm.CurrentView?.GetType().Name}");

        var only = Environment.GetEnvironmentVariable("NOCTIS_PERF_PAGES")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        bool Want(string p) => only == null || only.Contains(p);

        var navigate = vm.GetType().GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Go(string key) => navigate.Invoke(vm, [key]);

        var bigArtist = library.Artists.OrderByDescending(a => a.AlbumCount).First();
        var album = library.Albums.First(a => a.Tracks.Count >= 10);
        var pages = new List<(string Name, Action Open)>
        {
            ("home", () => Go("home")),
            ("songs", () => Go("songs")),
            ("albums", () => Go("albums")),
            ("artists", () => Go("artists")),
            ("genres", () => Go("genres")),
            ("playlists", () => Go("playlists")),
            ("playlist-big", () => Go("playlist:" + playlists[0].Id)),
            ("favorites", () => Go("favorites")),
            ("statistics", () => Go("statistics")),
            ("inbox", () => Go("inbox")),
            ("album", () => vm.OpenAlbumDetail(album)),
            ("artist", () => vm.OpenArtistByName(bigArtist.Name)),
        };
        try
        {
            foreach (var (name, open) in pages.Where(p => Want(p.Name)))
            {
                Go("queue");
                Settle(window, () => vm.CurrentView, () => { });
                var first = Settle(window, () => vm.CurrentView, open);
                Go("queue");
                Settle(window, () => vm.CurrentView, () => { });
                var second = Settle(window, () => vm.CurrentView, open);
                Log($"{name}: open {first.Ms:0} ms ({first.Loaded} loaded), again {second.Ms:0} ms ({second.Loaded} loaded), {second.Visuals} visuals [{vm.CurrentView?.GetType().Name}]" +
                    (name == "artist" ? $" ({bigArtist.Name}, {bigArtist.AlbumCount} albums)" : ""));
                ScrollPage(name, PageRoot(window, vm.CurrentView));
            }

            // Artists as a list (the top bar's view toggle).
            if (Want("artists-list"))
            {
                Go("artists");
                Settle(window, () => vm.CurrentView, () => { });
                var artistsVm = (LibraryArtistsViewModel)vm.CurrentView!;
                var open = Settle(window, () => vm.CurrentView, () => artistsVm.ToggleViewModeCommand.Execute(null));
                Log($"artists-list: switch {open.Ms:0} ms ({open.Loaded} loaded), list view {artistsVm.IsListView}");
                var table = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "ArtistTable");
                ScrollPage("artists-list", table);
                artistsVm.ToggleViewModeCommand.Execute(null);
                Pump();
            }

            // Queue: every song queued (the fake player plays nothing), the Queue page and the
            // queue panel scrolled.
            if (Want("queue") || Want("queue-panel"))
            {
                vm.Player.ReplaceQueueAndPlay(library.Tracks.ToList(), 0);
                for (var i = 0; i < 20; i++) { Pump(); Thread.Sleep(5); }
                Log($"queue: {vm.Player.QueueRows.Count} rows");
            }
            if (Want("queue"))
            {
                Go("home");
                Settle(window, () => vm.CurrentView, () => { });
                var open = Settle(window, () => vm.CurrentView, () => Go("queue"));
                Log($"queue: open {open.Ms:0} ms ({open.Loaded} loaded), {open.Visuals} visuals");
                ScrollPage("queue", PageRoot(window, vm.CurrentView));
            }
            if (Want("queue-panel"))
            {
                Go("home");
                Settle(window, () => vm.CurrentView, () => { });
                var open = Settle(window, () => vm.CurrentView, () => vm.Player.IsQueuePopupOpen = true);
                Log($"queue-panel: open {open.Ms:0} ms ({open.Loaded} loaded)");
                var list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "QueuePopupListBox");
                ScrollPage("queue-panel", list);
                vm.Player.IsQueuePopupOpen = false;
                Pump();
            }
        }
        finally
        {
            window.Close();
            Pump();
        }
    }
}
