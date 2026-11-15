using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Memory growth over a session, measured on the whole app (App.Initialize, the real DI graph,
/// MainWindow shown) with a fake audio engine and a synthetic library written into this run's
/// throw-away data root: N song changes, N page visits, N Settings / lyrics opens, each followed
/// by a full collection. Reports managed heap, GC committed bytes and private bytes per cycle.
/// Explicit only (it writes library.json into the shared test data root): NOCTIS_MEMORY_HARNESS=1.
///   Noctis.Tests.exe -method "Noctis.Tests.MemoryGrowthHarnessTests.SongChanges_Home_Long"
/// One method per process (each boots the app). NOCTIS_MEMORY_OUT appends the report lines to a
/// file; NOCTIS_MEMORY_DUMPDIR writes gcdumps at the checkpoints (diff them with dotnet-gcdump
/// report); NOCTIS_MEMORY_HOLD=seconds keeps the process up at the end for dotnet-dump + gcroot;
/// NOCTIS_MEMORY_LIBRARY names a library.json for <see cref="Baseline_LibraryStrings"/>.
/// Headless limits: bitmaps report 1x1 px (native pixel memory is not measured, so the artwork
/// cache is cleared before each sample) and the fake engine sends no position ticks, so view
/// models that only change during playback never sweep their dead weak-event slots here.
/// </summary>
public class MemoryGrowthHarnessTests
{
    private readonly ITestOutputHelper _o;
    public MemoryGrowthHarnessTests(ITestOutputHelper o) => _o = o;

    private static bool Enabled => Environment.GetEnvironmentVariable("NOCTIS_MEMORY_HARNESS") == "1";

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps the UI thread for <paramref name="ms"/>, letting pool work post back.</summary>
    private static void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Pump();
            Thread.Sleep(2);
        }
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs = 60_000)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("memory harness wait");
            Pump();
            Thread.Sleep(1);
        }
    }

    internal readonly record struct Sample(long Managed, long Committed, long Private, int LiveBitmaps, long CacheBytes);

    /// <summary>Full blocking collections with the UI thread pumped in between (finalizers post work).</summary>
    internal static Sample Settle()
    {
        // Headless bitmaps report 1x1 px, so the artwork cache's byte budget never evicts
        // here (it would at 128 MB in the app): drop it, so its entries don't read as growth.
        ArtworkCache.ClearForTests();
        for (var i = 0; i < 4; i++)
        {
            PumpFor(60);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var managed = GC.GetTotalMemory(true);
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return new Sample(managed, GC.GetGCMemoryInfo().TotalCommittedBytes, p.PrivateMemorySize64,
            ArtworkCache.LiveBitmapCount, ArtworkCache.ResidentBytes);
    }

    private void Log(string line)
    {
        _o.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("NOCTIS_MEMORY_OUT");
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, line + Environment.NewLine);
    }

    private void Report(string label, Sample before, Sample after, int cycles)
    {
        static string Mb(long b) => (b / 1048576.0).ToString("0.0");
        Log($"{label}: {cycles} cycles | managed {Mb(before.Managed)} -> {Mb(after.Managed)} MB " +
                     $"({(after.Managed - before.Managed) / Math.Max(1, cycles)} B/cycle) | committed {Mb(before.Committed)} -> {Mb(after.Committed)} MB " +
                     $"| private {Mb(before.Private)} -> {Mb(after.Private)} MB ({(after.Private - before.Private) / Math.Max(1, cycles)} B/cycle) " +
                     $"| live bitmaps {before.LiveBitmaps} -> {after.LiveBitmaps} | cache {Mb(before.CacheBytes)} -> {Mb(after.CacheBytes)} MB");
    }

    // ── the app as the app (copied from PerfAuditHarnessTests) ──

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

    private static ServiceProvider BuildAppServices(FakeAudioPlayer audio)
    {
        var services = new ServiceCollection();
        var program = typeof(App).Assembly.GetType("Noctis.Program")!;
        program.GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        services.AddSingleton<IAudioPlayer>(audio);
        return services.BuildServiceProvider();
    }

    private const int Albums = 40;
    private const int TracksPerAlbum = 8;

    /// <summary>
    /// Writes a library of <see cref="Albums"/> albums into the data root: real (tiny) audio files
    /// so nothing prunes them, an .lrc sidecar per song, and a 1200 px JPEG cover per album in the
    /// app's artwork folder (the size a downloaded cover usually is).
    /// </summary>
    private static void WriteLibrary()
    {
        var root = Noctis.Helpers.AppPaths.DataRoot;
        var music = Path.Combine(root, "music");
        var artDir = Path.Combine(root, "artwork");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(artDir);
        var tracks = new List<Track>();
        var rnd = new Random(7);
        for (var a = 0; a < Albums; a++)
        {
            var albumId = Guid.NewGuid();
            var art = Path.Combine(artDir, $"{albumId}.jpg");
            using (var bmp = new SKBitmap(1200, 1200))
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
                using var paint = new SKPaint { Color = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)) };
                for (var c = 0; c < 30; c++)
                    canvas.DrawCircle(rnd.Next(1200), rnd.Next(1200), rnd.Next(40, 300), paint);
                using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 90);
                using var fs = File.Create(art);
                data.SaveTo(fs);
            }
            var artist = $"Artist {a % 15}";
            var dir = Path.Combine(music, $"album{a}");
            Directory.CreateDirectory(dir);
            for (var t = 0; t < TracksPerAlbum; t++)
            {
                var file = Path.Combine(dir, $"{t + 1:00} song.mp3");
                File.WriteAllBytes(file, new byte[2048]);
                var lrc = new System.Text.StringBuilder();
                for (var l = 0; l < 40; l++)
                    lrc.Append($"[{l * 5 / 60:00}:{l * 5 % 60:00}.00]line {l} of song {t} on album {a} with some words\n");
                File.WriteAllText(Path.ChangeExtension(file, ".lrc"), lrc.ToString());
                tracks.Add(new Track
                {
                    Id = Guid.NewGuid(),
                    Title = $"Song {t + 1} ({a})",
                    Artist = artist,
                    AlbumArtist = artist,
                    Album = $"Album {a}",
                    AlbumId = albumId,
                    AlbumArtworkPath = art,
                    Genre = $"Genre {a % 6}",
                    Year = 1990 + a % 30,
                    TrackNumber = t + 1,
                    FilePath = file,
                    Duration = TimeSpan.FromSeconds(180 + t),
                    DateAdded = DateTime.Now.AddDays(-a),
                });
            }
        }
        var persistence = new PersistenceService();
        // Off the UI thread: the save awaits without ConfigureAwait(false), and its continuation
        // would queue behind this blocked dispatcher.
        Task.Run(() => persistence.SaveLibraryAsync(tracks)).GetAwaiter().GetResult();
    }

    private static (MainWindowViewModel vm, MainWindow window, ServiceProvider provider, FakeAudioPlayer audio) BootApp()
    {
        WriteLibrary();
        LoadAppAsTheApp();
        var audio = new FakeAudioPlayer();
        var provider = BuildAppServices(audio);
        App.Services = provider;
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow(vm) { Width = 1280, Height = 800 };
        window.Show();
        Pump();
        var library = provider.GetRequiredService<ILibraryService>();
        PumpUntil(() => library.Tracks.Count == Albums * TracksPerAlbum && library.Albums.Count >= Albums);
        PumpFor(1500);
        return (vm, window, provider, audio);
    }

    private static void Navigate(MainWindowViewModel vm, string key)
        => typeof(MainWindowViewModel).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [key]);

    [AvaloniaFact]
    public void Session_SongChanges_PageVisits_Overlays()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_MEMORY_HARNESS=1");
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        var (vm, window, provider, audio) = BootApp();
        var library = provider.GetRequiredService<ILibraryService>();
        var tracks = library.Tracks.ToList();
        try
        {
            // ── song changes on the Home page ──
            vm.Player.PlayFromRow(tracks, 0);
            PumpFor(100);
            for (var i = 0; i < 30; i++) { vm.Player.NextCommand.Execute(null); PumpFor(40); }
            var s0 = Settle();
            const int songs = 200;
            for (var i = 0; i < songs; i++)
            {
                if (!vm.Player.NextCommand.CanExecute(null)) vm.Player.PlayFromRow(tracks, 0);
                else vm.Player.NextCommand.Execute(null);
                PumpFor(40);
            }
            var s1 = Settle();
            Report("song changes (home)", s0, s1, songs);

            // ── song changes with the lyrics page up ──
            Navigate(vm, "lyrics");
            PumpFor(300);
            for (var i = 0; i < 30; i++) { vm.Player.NextCommand.Execute(null); PumpFor(40); }
            var l0 = Settle();
            for (var i = 0; i < songs; i++)
            {
                if (!vm.Player.NextCommand.CanExecute(null)) vm.Player.PlayFromRow(tracks, 0);
                else vm.Player.NextCommand.Execute(null);
                PumpFor(40);
            }
            var l1 = Settle();
            Report("song changes (lyrics page)", l0, l1, songs);
            Navigate(vm, "home");
            PumpFor(200);

            // ── album pages ──
            var albums = library.Albums.ToList();
            for (var i = 0; i < 40; i++) { vm.OpenAlbumDetail(albums[i % albums.Count]); PumpFor(30); }
            var a0 = Settle(); GcDump("pages-0");
            var albumRefs = new List<WeakReference>();
            const int albumVisits = 120;
            for (var i = 0; i < albumVisits; i++)
            {
                vm.OpenAlbumDetail(albums[i % albums.Count]);
                PumpFor(30);
                if (i < 40) albumRefs.Add(new WeakReference(vm.CurrentView));
            }
            Navigate(vm, "home");
            var a1 = Settle(); GcDump("pages-albums");
            Report("album pages", a0, a1, albumVisits);
            Log($"  album page VMs from the first 40 visits still alive (history keeps 30): {albumRefs.Count(r => r.IsAlive)}");

            // ── artist pages ──
            var artists = tracks.Select(t => t.Artist).Distinct().ToList();
            var artistMethod = typeof(MainWindowViewModel).GetMethod("OpenArtistDiscography", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var i = 0; i < 30; i++) { artistMethod.Invoke(vm, [artists[i % artists.Count]]); PumpFor(30); }
            var r0 = Settle();
            var artistRefs = new List<WeakReference>();
            const int artistVisits = 90;
            for (var i = 0; i < artistVisits; i++)
            {
                artistMethod.Invoke(vm, [artists[i % artists.Count]]);
                PumpFor(30);
                if (i < 30) artistRefs.Add(new WeakReference(vm.CurrentView));
            }
            Navigate(vm, "home");
            var r1 = Settle(); GcDump("pages-artists");
            Report("artist pages", r0, r1, artistVisits);
            Log($"  artist page VMs from the first 30 visits still alive: {artistRefs.Count(r => r.IsAlive)}");

            // ── section switches ──
            var keys = new[] { "home", "songs", "albums", "artists", "playlists", "favorites", "statistics", "queue", "genres", "folders" };
            foreach (var k in keys) { Navigate(vm, k); PumpFor(60); }
            var n0 = Settle(); GcDump("sections-0");
            const int rounds = 15;
            for (var r = 0; r < rounds; r++)
                foreach (var k in keys) { Navigate(vm, k); PumpFor(40); }
            Navigate(vm, "home");
            var n1 = Settle(); GcDump("sections-1");
            Report("section switches", n0, n1, rounds * keys.Length);

            // ── Settings sheet open/close ──
            for (var i = 0; i < 3; i++) { vm.OpenSettingsCommand.Execute(null); PumpFor(200); vm.CloseSettingsCommand.Execute(null); PumpFor(200); }
            var t0 = Settle();
            const int opens = 20;
            for (var i = 0; i < opens; i++) { vm.OpenSettingsCommand.Execute(null); PumpFor(150); vm.CloseSettingsCommand.Execute(null); PumpFor(150); }
            var t1 = Settle();
            Report("settings open/close", t0, t1, opens);

            // ── lyrics side panel toggles ──
            for (var i = 0; i < 3; i++) { vm.ToggleLyricsPanelCommand.Execute(null); PumpFor(100); vm.ToggleLyricsPanelCommand.Execute(null); PumpFor(100); }
            var p0 = Settle();
            for (var i = 0; i < opens; i++) { vm.ToggleLyricsPanelCommand.Execute(null); PumpFor(100); vm.ToggleLyricsPanelCommand.Execute(null); PumpFor(100); }
            var p1 = Settle();
            Report("lyrics panel toggles", p0, p1, opens);
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    /// <summary>With NOCTIS_MEMORY_DUMPDIR set, writes a gcdump of this process there (dotnet-gcdump on PATH or in ~/.dotnet/tools).</summary>
    private void GcDump(string name)
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_MEMORY_DUMPDIR");
        if (string.IsNullOrEmpty(dir)) return;
        var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-gcdump.exe");
        if (!File.Exists(tool)) tool = "dotnet-gcdump";
        var path = Path.Combine(dir, name + ".gcdump");
        using var p = Process.Start(new ProcessStartInfo(tool, $"collect -p {Environment.ProcessId} -o \"{path}\"")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit(120_000);
        Log($"  gcdump {path}");
    }

    [AvaloniaFact]
    public void SongChanges_Home_Long()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_MEMORY_HARNESS=1");
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        var (vm, window, provider, _) = BootApp();
        var tracks = provider.GetRequiredService<ILibraryService>().Tracks.ToList();
        try
        {
            vm.Player.PlayFromRow(tracks, 0);
            PumpFor(100);
            void Changes(int n)
            {
                for (var i = 0; i < n; i++)
                {
                    if (!vm.Player.NextCommand.CanExecute(null)) vm.Player.PlayFromRow(tracks, 0);
                    else vm.Player.NextCommand.Execute(null);
                    PumpFor(25);
                }
            }
            Changes(30);
            var s0 = Settle();
            GcDump("songs-0");
            Changes(320);
            var s1 = Settle();
            Report("song changes 1st lap (home)", s0, s1, 320);
            GcDump("songs-1");
            Changes(320);
            var s2 = Settle();
            Report("song changes 2nd lap (home)", s1, s2, 320);
            GcDump("songs-2");
            if (Environment.GetEnvironmentVariable("NOCTIS_MEMORY_HOLD") is { Length: > 0 } hold)
            {
                Log($"  holding {hold} s, pid {Environment.ProcessId}");
                PumpFor(int.Parse(hold) * 1000);
            }
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    [AvaloniaFact]
    public void SectionSwitches_Long()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_MEMORY_HARNESS=1");
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        var (vm, window, _, _) = BootApp();
        var keys = (Environment.GetEnvironmentVariable("NOCTIS_MEMORY_SECTIONS") ?? "home,songs,albums,artists,playlists,favorites,statistics,queue,genres,folders").Split(',');
        try
        {
            foreach (var k in keys) { Navigate(vm, k); PumpFor(60); }
            var prev = Settle();
            GcDump("sections-long-0");
            for (var block = 1; block <= 3; block++)
            {
                for (var r = 0; r < 10; r++)
                    foreach (var k in keys) { Navigate(vm, k); PumpFor(30); }
                Navigate(vm, "home");
                var now = Settle();
                Report($"section switches block {block}", prev, now, 10 * keys.Length);
                GcDump($"sections-long-{block}");
                prev = now;
            }
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    /// <summary>
    /// Baseline: what a real library costs in managed memory once loaded, and how much of it is
    /// repeated text (the same artist/album/genre string held once per song). NOCTIS_MEMORY_LIBRARY
    /// names a library.json; it is copied to a temp folder first (the load may migrate lyrics).
    /// </summary>
    [Fact]
    public void Baseline_LibraryStrings()
    {
        var source = Environment.GetEnvironmentVariable("NOCTIS_MEMORY_LIBRARY");
        Assert.SkipWhen(!Enabled || string.IsNullOrEmpty(source) || !File.Exists(source), "set NOCTIS_MEMORY_HARNESS=1 and NOCTIS_MEMORY_LIBRARY");
        var dir = Directory.CreateTempSubdirectory("noctis-mem-lib").FullName;
        try
        {
            File.Copy(source!, Path.Combine(dir, "library.json"));
            var persistence = new PersistenceService(dir);
            var before = GC.GetTotalMemory(true);
            var tracks = Task.Run(() => persistence.LoadLibraryAsync()).GetAwaiter().GetResult()!;
            var after = GC.GetTotalMemory(true);
            Log($"library: {tracks.Count} tracks, file {new FileInfo(source!).Length / 1048576.0:0.0} MB, retained {(after - before) / 1048576.0:0.0} MB managed ({(after - before) / Math.Max(1, tracks.Count)} B/track)");

            var props = typeof(Track).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                            && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() == null)
                .ToList();
            long totalBytes = 0, dupBytes = 0;
            var rows = new List<(string Name, long Dup, int Distinct)>();
            foreach (var p in props)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var seenRefs = new HashSet<object>(ReferenceEqualityComparer.Instance);
                long propDup = 0;
                foreach (var t in tracks)
                {
                    if (p.GetValue(t) is not string s || s.Length == 0) continue;
                    var size = 22 + 2L * s.Length; // object header + length + chars (x64)
                    if (!seenRefs.Add(s)) continue; // same instance already counted
                    totalBytes += size;
                    if (!seen.Add(s)) { dupBytes += size; propDup += size; }
                }
                rows.Add((p.Name, propDup, seen.Count));
            }
            foreach (var r in rows.OrderByDescending(r => r.Dup).Take(10))
                Log($"  {r.Name}: {r.Dup / 1024} KB in repeated copies, {r.Distinct} distinct values");
            Log($"  string fields total {totalBytes / 1048576.0:0.0} MB, of which repeated copies {dupBytes / 1048576.0:0.0} MB");
            GC.KeepAlive(tracks);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static readonly FieldInfo MiniField =
        typeof(MainWindow).GetField("_miniPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Opens the mini player, waits for it, closes it and waits out its close animation.</summary>
    internal static WeakReference CycleMiniPlayer(MainWindow window)
    {
        window.ToggleMiniPlayer();
        PumpUntil(() => MiniField.GetValue(window) != null, 5_000);
        PumpFor(150);
        var mini = new WeakReference(MiniField.GetValue(window));
        window.ToggleMiniPlayer();
        PumpUntil(() => MiniField.GetValue(window) == null, 5_000);
        PumpFor(50);
        return mini;
    }

    [AvaloniaFact]
    public void MiniPlayer_OpenClose()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_MEMORY_HARNESS=1");
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        var (vm, window, provider, _) = BootApp();
        var tracks = provider.GetRequiredService<ILibraryService>().Tracks.ToList();
        try
        {
            vm.Player.PlayFromRow(tracks, 0);
            PumpFor(200);
            for (var i = 0; i < 3; i++) CycleMiniPlayer(window);
            var m0 = Settle();
            GcDump("mini-0");
            const int opens = 20;
            var refs = new List<WeakReference>();
            for (var i = 0; i < opens; i++) refs.Add(CycleMiniPlayer(window));
            var m1 = Settle();
            Report("mini player open/close", m0, m1, opens);
            Log($"  mini player windows still alive: {refs.Count(r => r.IsAlive)} of {refs.Count}");
            // Past MarqueeTextBlock's 7 s rest timer, which holds a closed window until it fires.
            PumpFor(9000);
            var m2 = Settle();
            GcDump("mini-2");
            Report("mini player open/close, 9 s later", m0, m2, opens);
            Log($"  mini player windows still alive 9 s later: {refs.Count(r => r.IsAlive)} of {refs.Count}");
            if (Environment.GetEnvironmentVariable("NOCTIS_MEMORY_HOLD") is { Length: > 0 } hold)
            {
                // Time to attach dotnet-dump to this process (gcroot a MiniPlayerWindow).
                Log($"  holding {hold} s, pid {Environment.ProcessId}");
                PumpFor(int.Parse(hold) * 1000);
            }
            GC.KeepAlive(refs);
        }
        finally
        {
            window.Close();
            Pump();
        }
    }
}
