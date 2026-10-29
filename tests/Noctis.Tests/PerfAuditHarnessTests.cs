using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Pre-release performance harness (startup, library load, Songs list, lyrics page, plugin
/// host). Written against the API shared with v1.6.5 (newer members by reflection) so the
/// same file runs on both trees and the numbers compare.
/// Explicit only: NOCTIS_PERF_HARNESS=1, with NOCTIS_PERF_DATA naming a profile folder (copied
/// into the run's throw-away data root, never written). NOCTIS_PERF_OUT appends "tag\tkey\tms" lines
/// (tag = NOCTIS_PERF_TAG) for comparing runs; NOCTIS_PERF_RUNS sets measured runs (5).
/// Run one test per process (the startup number is a cold-process number):
///   Noctis.Tests.exe -method "Noctis.Tests.PerfAuditHarnessTests.Startup_FirstFrameAndFirstPage"
/// Headless drawing stubs: frame numbers are layout + composition, not rasterization.
/// </summary>
public class PerfAuditHarnessTests
{
    private readonly ITestOutputHelper _o;
    public PerfAuditHarnessTests(ITestOutputHelper o) => _o = o;

    private static bool Enabled => Environment.GetEnvironmentVariable("NOCTIS_PERF_HARNESS") == "1";
    private static int Runs => int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_PERF_RUNS"), out var n) && n > 0 ? n : 5;

    private void Emit(string key, double ms)
    {
        _o.WriteLine($"{key}: {ms:0.000} ms");
        var path = Environment.GetEnvironmentVariable("NOCTIS_PERF_OUT");
        if (string.IsNullOrEmpty(path)) return;
        var tag = Environment.GetEnvironmentVariable("NOCTIS_PERF_TAG") ?? "?";
        File.AppendAllText(path, $"{tag}\t{key}\t{ms.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}\n");
    }

    /// <summary>Warm-up once, then <see cref="Runs"/> measured runs; emits median and min.</summary>
    private void Measure(string key, Func<double> run)
    {
        run();
        var xs = Enumerable.Range(0, Runs).Select(_ => run()).OrderBy(x => x).ToList();
        Emit(key + ".median", xs[xs.Count / 2]);
        Emit(key + ".min", xs[0]);
    }

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
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("perf harness wait");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(0);
        }
    }

    private static T Await<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    // ── the app's own App.Initialize (App.axaml resources, styles, view locator, installs) ──

    private static bool _appLoaded;

    private static void LoadAppAsTheApp()
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
        // Program.Main warms libvlc on a pool thread beside this resolve; headless has no audio.
        services.AddSingleton<IAudioPlayer>(new FakeAudioPlayer());
        return services.BuildServiceProvider();
    }

    private static bool _profileCopied;

    /// <summary>Copies the profile named by NOCTIS_PERF_DATA into this run's throw-away data
    /// root (TestAssemblyInit), so the harness never writes to it.</summary>
    private static void RequireProfile()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_PERF_HARNESS=1");
        var source = Environment.GetEnvironmentVariable("NOCTIS_PERF_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(source) || !File.Exists(Path.Combine(source, "library.json")),
            "set NOCTIS_PERF_DATA to a profile folder with a library.json (it is copied, not written)");
        if (_profileCopied) return;
        _profileCopied = true;
        var root = Noctis.Helpers.AppPaths.DataRoot;
        foreach (var file in Directory.EnumerateFiles(source!, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(source!, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── 1. startup ──

    [AvaloniaFact]
    public void Startup_FirstFrameAndFirstPage()
    {
        RequireProfile();
        var origin = StartupTrace.ElapsedMs;
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        LoadAppAsTheApp();
        Emit("startup.app-initialize", sw.Elapsed.TotalMilliseconds);

        sw.Restart();
        var provider = BuildAppServices();
        App.Services = provider;
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        Emit("startup.vm-graph", sw.Elapsed.TotalMilliseconds);

        sw.Restart();
        var window = new MainWindow(vm) { Width = 1280, Height = 800 };
        Emit("startup.mainwindow-ctor", sw.Elapsed.TotalMilliseconds);

        sw.Restart();
        window.Show();
        Pump();
        Emit("startup.show-to-first-frame", sw.Elapsed.TotalMilliseconds);
        Emit("startup.total-to-first-frame", total.Elapsed.TotalMilliseconds);

        // Loaded → settings → library → playlists → queue restore → Navigate(default page),
        // then plugins load on Background priority. The app's own StartupTrace marks, rebased
        // on this test's clock.
        var marks = (System.Collections.IList)typeof(StartupTrace)
            .GetField("Marks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        double? MarkAt(string name)
        {
            lock (marks)
                foreach (var m in marks)
                    if (m is ValueTuple<string, double> t && t.Item1 == name) return t.Item2 - origin;
            return null;
        }
        // (Marks after the Flush that follows initialize-async-done are dropped by design.)
        PumpUntil(() => MarkAt("initialize-async-done") != null);
        Pump();
        foreach (var name in new[] { "settings-loaded", "library-loaded", "playlists-loaded", "first-page-navigated",
                     "initialize-async-done", "plugins-loaded" })
            Emit($"startup.total-to-{name}", MarkAt(name) ?? double.NaN);
        _o.WriteLine($"page={vm.CurrentView?.GetType().Name}, tracks={provider.GetRequiredService<ILibraryService>().Tracks.Count}");
        window.Close();
    }

    // ── 2. library load ──

    [AvaloniaFact]
    public void LibraryLoad_LargeProfile()
    {
        RequireProfile();
        var counts = "";
        Measure("library.load", () =>
        {
            using var provider = BuildAppServices();
            var library = provider.GetRequiredService<ILibraryService>();
            var sw = Stopwatch.StartNew();
            Await(library.LoadAsync());
            var ms = sw.Elapsed.TotalMilliseconds;
            counts = $"tracks={library.Tracks.Count}, albums={library.Albums.Count}, artists={library.Artists.Count}";
            // Let the background SQLite pass finish so it does not overlap the next run.
            var bg = library.GetType().GetProperty("BackgroundInit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(library) as Task;
            if (bg != null) Await(bg);
            return ms;
        });
        _o.WriteLine(counts);
    }

    // ── 3. Songs list ──

    private static List<Track> LoadTracksDirect(string dataDir)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        using var fs = File.OpenRead(Path.Combine(dataDir, "library.json"));
        return JsonSerializer.Deserialize<List<Track>>(fs, options)!;
    }

    /// <summary>The profile's tracks, repeated <paramref name="copies"/> times as distinct albums.</summary>
    private static FakeLibraryService ScaledLibrary(int copies)
    {
        var dir = Noctis.Helpers.AppPaths.DataRoot;
        var lib = new FakeLibraryService();
        for (var c = 0; c < copies; c++)
        {
            var albumMap = new Dictionary<Guid, Guid>();
            foreach (var t in LoadTracksDirect(dir))
            {
                if (c > 0)
                {
                    t.Id = Guid.NewGuid();
                    if (!albumMap.TryGetValue(t.AlbumId, out var a)) albumMap[t.AlbumId] = a = Guid.NewGuid();
                    t.AlbumId = a;
                    t.Album += $" ({c})";
                }
                lib.TrackList.Add(t);
            }
        }
        return lib;
    }

    [AvaloniaFact]
    public void SongsList_PopulateSortScrollReRank()
    {
        RequireProfile();
        LoadAppAsTheApp();
        var copies = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_PERF_SCALE"), out var s) && s > 0 ? s : 1;
        var library = ScaledLibrary(copies);
        _o.WriteLine($"tracks={library.Tracks.Count}, albums={library.Tracks.Select(t => t.AlbumId).Distinct().Count()}");
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, library);
        var key = copies == 1 ? "songs" : $"songs.x{copies}";

        // Populate: a fresh VM made active (Refresh → off-thread build → ReplaceAll).
        Measure(key + ".populate-vm", () =>
        {
            var sw = Stopwatch.StartNew();
            var fresh = new LibrarySongsViewModel(library, player, sidebar, persistence);
            fresh.IsActive = true;
            PumpUntil(() => fresh.FilteredTracks.Count == library.Tracks.Count);
            var ms = sw.Elapsed.TotalMilliseconds;
            fresh.Dispose();
            return ms;
        });

        var vm = new LibrarySongsViewModel(library, player, sidebar, persistence);
        vm.IsActive = true;
        PumpUntil(() => vm.FilteredTracks.Count == library.Tracks.Count);
        var view = new LibrarySongsView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Pump();
        Pump();
        try
        {
            var resets = 0;
            vm.FilteredTracks.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };

            // NOCTIS_PERF_SONGS=rerank runs only the re-rank part.
            var onlyReRank = Environment.GetEnvironmentVariable("NOCTIS_PERF_SONGS") == "rerank";

            // Sort by each common column, with the list mounted (re-realizing the visible rows).
            foreach (var column in onlyReRank ? Array.Empty<string>() : new[] { "Title", "Artist", "Album", "Plays", "Date Added" })
            {
                Measure($"{key}.sort.{column.Replace(" ", "")}", () =>
                {
                    var before = resets;
                    var sw = Stopwatch.StartNew();
                    vm.SortCommand.Execute(column);
                    PumpUntil(() => resets > before);
                    Pump();
                    return sw.Elapsed.TotalMilliseconds;
                });
            }

            // Scroll: N viewport positions through the virtualized list.
            var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TrackList");
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            if (!onlyReRank)
            Measure($"{key}.scroll.frame-mean", () =>
            {
                scroller.Offset = new Vector(0, 0);
                Pump();
                const int frames = 120;
                var sw = Stopwatch.StartNew();
                for (var i = 1; i <= frames; i++)
                {
                    scroller.Offset = new Vector(0, i * 90.0);
                    Pump();
                }
                return sw.Elapsed.TotalMilliseconds / frames;
            });
            if (!onlyReRank)
            Measure($"{key}.scroll.jump-frame-mean", () =>
            {
                const int frames = 40;
                var max = Math.Max(1, scroller.Extent.Height - scroller.Viewport.Height);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < frames; i++)
                {
                    scroller.Offset = new Vector(0, max * ((i * 7919) % frames) / frames);
                    Pump();
                }
                return sw.Elapsed.TotalMilliseconds / frames;
            });

            // One counted play re-ranked in a Plays sort (HEAD: FinishedPlays → ReRankFinished;
            // v1.6.5 has no live re-rank, so its row is the full re-sort a user had to trigger).
            vm.SelectSortCommand.Execute("Plays");
            vm.SelectSortCommand.Execute("Descending");
            PumpUntil(() => vm.SortColumn == "Plays");
            Pump();
            var reRank = typeof(LibrarySongsViewModel).GetMethod("ReRankFinished", BindingFlags.Instance | BindingFlags.NonPublic);
            var apply = typeof(LibrarySongsViewModel).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(m => m.Name == "ApplyFilterAndSort");
            // The re-rank itself, not its "user is scrolling, retry in 2 s" wait.
            vm.GetType().GetProperty("IsUserBusy")?.SetValue(vm, null);
            var rng = new Random(7);
            var reRankEvents = new Dictionary<NotifyCollectionChangedAction, int>();
            var toEvent = new List<double>();
            Measure($"{key}.counted-play-rerank", () =>
            {
                // A row from the middle becomes the most played: it has to move to the top.
                var t = vm.FilteredTracks[vm.FilteredTracks.Count / 2 + rng.Next(100)];
                t.PlayCount = vm.FilteredTracks.Max(x => x.PlayCount) + 1;
                var target = vm.FilteredTracks.Count;
                var moved = false;
                var sw = new Stopwatch();
                NotifyCollectionChangedEventHandler h = (_, e) =>
                {
                    if (!moved) toEvent.Add(sw.Elapsed.TotalMilliseconds);
                    moved = true;
                    reRankEvents[e.Action] = reRankEvents.GetValueOrDefault(e.Action) + 1;
                };
                vm.FilteredTracks.CollectionChanged += h;
                sw.Start();
                if (reRank != null)
                    reRank.Invoke(vm, [new List<Track> { t }]);
                else
                    apply.Invoke(vm, apply.GetParameters().Select(p => p.DefaultValue).ToArray());
                PumpUntil(() => moved);
                Pump();
                var ms = sw.Elapsed.TotalMilliseconds;
                vm.FilteredTracks.CollectionChanged -= h;
                Assert.Equal(target, vm.FilteredTracks.Count);
                return ms;
            });
            toEvent.Sort();
            Emit($"{key}.counted-play-rerank.to-list-change.median", toEvent[toEvent.Count / 2]);
            _o.WriteLine($"rerank path: {(reRank != null ? "ReRankFinished (move)" : "ApplyFilterAndSort (full re-sort)")}, list events: " +
                         string.Join(", ", reRankEvents.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    // ── 4. lyrics page ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private static readonly string[] Words =
        ["I", "kept", "the", "light", "on", "for", "you", "all", "night", "long", "and", "the", "city", "never", "slept"];

    /// <summary>A normal song: 56 lines over ~3:30, 4 s apart.</summary>
    private static string Lrc()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 56; i++)
        {
            var t = TimeSpan.FromSeconds(8 + i * 3.6);
            sb.Append($"[{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}]");
            sb.AppendLine(string.Join(' ', Enumerable.Range(0, 7).Select(w => Words[(i + w) % Words.Length])));
        }
        return sb.ToString();
    }

    /// <summary>The same song word-synced (TTML, 7 timed words a line).</summary>
    private static string Ttml()
    {
        static string Ts(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\.fff");
        var sb = new System.Text.StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>");
        for (var i = 0; i < 56; i++)
        {
            var start = 8 + i * 3.6;
            sb.Append($"<p begin=\"{Ts(start)}\" end=\"{Ts(start + 3.4)}\">");
            for (var w = 0; w < 7; w++)
                sb.Append($"<span begin=\"{Ts(start + w * 0.45)}\" end=\"{Ts(start + w * 0.45 + 0.4)}\">{Words[(i + w) % Words.Length]}</span> ");
            sb.Append("</p>");
        }
        return sb.Append("</div></body></tt>").ToString();
    }

    [AvaloniaFact]
    public void LyricsPage_NormalSongs()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_PERF_HARNESS=1");
        LoadAppAsTheApp();
        var lrc = LrcParser.Parse(Lrc());
        var ttml = TtmlParser.Parse(Ttml()).Lines!;
        Assert.Equal(56, lrc.Count);
        Assert.True(ttml.All(l => l.Words?.Count == 7));

        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadataService(),
            new TestPersistenceService(), library);
        var view = new LyricsView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Pump();
        var update = typeof(LyricsViewModel).GetMethod("UpdateActiveLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var synced = typeof(LyricsViewModel).GetField("_hasSyncedLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            foreach (var (name, lines) in new[] { ("lrc", lrc), ("word", ttml) })
            {
                Measure($"lyrics.{name}.page-build", () =>
                {
                    vm.LyricLines.ReplaceAll(Array.Empty<LyricLine>());
                    Pump();
                    var sw = Stopwatch.StartNew();
                    vm.LyricLines.ReplaceAll(lines);
                    Pump();
                    return sw.Elapsed.TotalMilliseconds;
                });
                synced.SetValue(vm, true);
                var frameTimes = new List<double>();
                Measure($"lyrics.{name}.tick-mean", () =>
                {
                    update.Invoke(vm, [TimeSpan.FromSeconds(30)]);
                    Pump();
                    const int frames = 600; // 10 s at 60 fps across ~3 line changes
                    var sw = new Stopwatch();
                    frameTimes.Clear();
                    for (var i = 1; i <= frames; i++)
                    {
                        var t = TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(i * 1000.0 / 60);
                        sw.Restart();
                        update.Invoke(vm, [t]);
                        Pump();
                        frameTimes.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    return frameTimes.Average();
                });
                var sorted = frameTimes.OrderBy(x => x).ToList();
                Emit($"lyrics.{name}.tick-p95(last run)", sorted[(int)(sorted.Count * 0.95)]);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class StubMetadataService : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    // ── 5. plugin host with no plugins ──

    [AvaloniaFact]
    public void PluginHost_NoPlugins()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_PERF_HARNESS=1");
        var dir = Directory.CreateTempSubdirectory("noctis-perf-plugins").FullName;
        try
        {
            var library = new FakeLibraryService();
            for (var i = 0; i < 400; i++)
                library.TrackList.Add(new Track { Title = $"t{i}", Artist = "a", Album = "b", FilePath = Path.Combine(dir, $"{i}.flac"), Duration = TimeSpan.FromMinutes(3) });
            var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
            var settings = new AppSettings();

            Measure("plugins.ctor+LoadAll(x100)", () =>
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 100; i++)
                    new PluginHost(player, dir, () => settings, () => { }, "1.6.6", library).LoadAll();
                return sw.Elapsed.TotalMilliseconds;
            });

            var host = new PluginHost(player, dir, () => settings, () => { }, "1.6.6", library);
            host.LoadAll();
            Measure("plugins.queries(x10000)", () =>
            {
                var sw = Stopwatch.StartNew();
                var n = 0;
                for (var i = 0; i < 10_000; i++)
                    n += host.VisualLayers.Count + host.LyricsProviders.Count + host.TrackCommands.Count + (host.HasScrobbleListeners ? 1 : 0);
                Assert.Equal(0, n);
                return sw.Elapsed.TotalMilliseconds;
            });

            // Track changes through the player with the (empty) host attached.
            var idx = 0;
            Measure("plugins.track-change(x200)", () =>
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 200; i++)
                {
                    player.PlayFromRow(library.TrackList, idx++ % library.TrackList.Count);
                    Dispatcher.UIThread.RunJobs();
                }
                return sw.Elapsed.TotalMilliseconds;
            });
            GC.KeepAlive(host);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
