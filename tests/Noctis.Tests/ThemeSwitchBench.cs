using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Theme-switch benchmark (11-27, "switching theme used to be butter smooth"): the real app
/// (App.Initialize styles and resources, ConfigureServices, MainWindow, real LibraryService)
/// over PageScrollBench's synthetic library (NOCTIS_PERF_TRACKS songs, default 10000), every
/// root section visited once so PageDeck holds its kept pages, then the artist page of the
/// biggest artist in front. Each switch goes through the Settings commands (SetInkTheme...), so
/// the window's ThemeChanged handler (Liquid Glass refresh) and every other subscriber run as in
/// the app; App.SetTheme / SetAccent run on a Noctis.App whose ThemeHost is the headless
/// application (the windows resolve against that one).
/// Per switch: the synchronous switch (ms), the first jobs + layout + render pass after it (ms),
/// the posted tail until quiet (ms), resources-changed notifications raised on the application
/// and on the window (each one walks the window's whole logical tree, hidden kept pages
/// included), theme-variant changes, AccentApplied / AppGlass.Changed raises, controls left
/// measure- / arrange-invalid by the switch, layout passes, and UI-thread KB allocated.
/// Scenarios: artist page; + Liquid Glass; + accent window tint; + Settings sheet open.
/// Explicit only: NOCTIS_PERF_HARNESS=1; NOCTIS_PERF_OUT / NOCTIS_PERF_TAG append "tag\tline".
///   dotnet test --filter "FullyQualifiedName~ThemeSwitchBench"
/// Headless drawing stubs: frame numbers are layout + composition, not rasterization.
/// </summary>
public class ThemeSwitchBench
{
    private readonly ITestOutputHelper _o;
    public ThemeSwitchBench(ITestOutputHelper o) => _o = o;

    private static bool Enabled => Environment.GetEnvironmentVariable("NOCTIS_PERF_HARNESS") == "1";
    private static int TrackCount => int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_PERF_TRACKS"), out var n) && n > 0 ? n : 10000;

    private void Log(string line)
    {
        _o.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("NOCTIS_PERF_OUT");
        if (!string.IsNullOrEmpty(path))
            File.AppendAllText(path, (Environment.GetEnvironmentVariable("NOCTIS_PERF_TAG") ?? "?") + "\t" + line + "\n");
    }

    private static ServiceProvider BuildAppServices()
    {
        var services = new ServiceCollection();
        var program = typeof(App).Assembly.GetType("Noctis.Program")!;
        program.GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        services.AddSingleton<IAudioPlayer>(new FakeAudioPlayer());
        return services.BuildServiceProvider();
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
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("theme bench wait");
            Pump();
            Thread.Sleep(1);
        }
    }

    /// <summary>Pumps with short real-time gaps (posted Background work, timers) and returns the
    /// UI-thread time spent in the pumps, gaps excluded.</summary>
    private static double Quiet(int rounds = 25)
    {
        var busy = 0.0;
        for (var i = 0; i < rounds; i++)
        {
            Thread.Sleep(4);
            var sw = Stopwatch.StartNew();
            Pump();
            busy += sw.Elapsed.TotalMilliseconds;
        }
        return busy;
    }

    private sealed record Sample(string From, string To, double SyncMs, double FirstFrameMs, double TailMs,
        int AppRc, int WinRc, int Variant, int Accent, int Glass, int MeasureInvalid, int ArrangeInvalid,
        int LayoutPasses, long Kb, int ScrimCaptures);

    private static readonly string[] Cycle = { "Ink", "Smoke", "Midnight", "Dark", "Light", "Gray" };

    [AvaloniaFact]
    public void ThemeSwitch_HeavyWindow()
    {
        Assert.SkipUnless(Enabled, "set NOCTIS_PERF_HARNESS=1");
        var gen = Stopwatch.StartNew();
        var (tracks, _) = Task.Run(() => PageScrollBench.WriteLibrary(TrackCount,
            Environment.GetEnvironmentVariable("NOCTIS_PERF_COVERS") != "0")).GetAwaiter().GetResult();
        Log($"library: {tracks.Count} tracks ({gen.ElapsedMilliseconds} ms to write)");

        PageScrollBench.LoadAppAsTheApp();
        var host = Application.Current!;
        var noctis = new App { ThemeHost = host };
        var provider = BuildAppServices();
        App.Services = provider;
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var settings = vm.Settings;
        // As MainWindow's handlers do in the app (they find no Noctis.App as Application.Current
        // here): subscribed first, so the theme is applied before the window's own handler runs,
        // with the window's transitions suppressed as App.SetTheme does for the app's main window.
        MainWindow? shown = null;
        settings.ThemeChanged += (_, key) => App.RunWithTransitionsSuppressed(shown, () => noctis.SetTheme(key));
        settings.AccentChanged += (_, hex) => noctis.SetAccent(hex);

        var window = new MainWindow(vm) { Width = 1600, Height = 1000 };
        shown = window;
        window.Show();
        var library = provider.GetRequiredService<ILibraryService>();
        var libraryCount = tracks.Count(t => !t.IsInInbox);
        PumpUntil(() => library.Tracks.Count == libraryCount && vm.CurrentView != null);
        for (var i = 0; i < 100; i++) { Pump(); Thread.Sleep(10); }

        var navigate = vm.GetType().GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var page in new[] { "home", "songs", "albums", "artists", "genres", "playlists", "favorites", "statistics" })
        {
            navigate.Invoke(vm, [page]);
            for (var i = 0; i < 20; i++) { Pump(); Thread.Sleep(5); }
        }
        var bigArtist = library.Artists.OrderByDescending(a => a.AlbumCount).First();
        vm.OpenArtistByName(bigArtist.Name);
        for (var i = 0; i < 60; i++) { Pump(); Thread.Sleep(10); }

        var deck = window.GetVisualDescendants().OfType<Noctis.Controls.PageDeck>().First();
        var hiddenLogical = deck.KeptViews.Where(v => !ReferenceEquals(v, deck.ShownView))
            .Sum(v => v.GetLogicalDescendants().Count() + 1);
        Log($"window: {window.GetLogicalDescendants().Count()} logical ({hiddenLogical} in {deck.KeptViews.Count - 1} hidden kept pages), " +
            $"{window.GetVisualDescendants().Count()} visual; page {vm.CurrentView?.GetType().Name} ({bigArtist.Name}, {bigArtist.AlbumCount} albums)");

        int appRc = 0, winRc = 0, variant = 0, accent = 0, glass = 0, layouts = 0;
        host.ResourcesChanged += (_, _) => appRc++;
        window.ResourcesChanged += (_, _) => winRc++;
        window.ActualThemeVariantChanged += (_, _) => variant++;
        App.AccentApplied += (_, _) => accent++;
        AppGlass.Changed += (_, _) => glass++;
        window.LayoutUpdated += (_, _) => layouts++;
        var scrim = window.FindControl<Noctis.Controls.BlurredBackdrop>("SettingsScrim");

        void Apply(string theme) => (theme switch
        {
            "Ink" => settings.SetInkThemeCommand,
            "Smoke" => settings.SetSmokeThemeCommand,
            "Midnight" => settings.SetMidnightThemeCommand,
            "Dark" => settings.SetDarkThemeCommand,
            "Light" => settings.SetLightThemeCommand,
            _ => settings.SetGrayThemeCommand,
        }).Execute(null);

        var current = "Gray";
        Apply(current);
        Quiet();

        List<Sample> Run(string scenario)
        {
            var samples = new List<Sample>();
            for (var round = 0; round < 3; round++)
            {
                foreach (var to in Cycle)
                {
                    appRc = winRc = variant = accent = glass = layouts = 0;
                    var captures0 = scrim?.CaptureCount ?? 0;
                    var b0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    Apply(to);
                    var sync = sw.Elapsed.TotalMilliseconds;
                    var visuals = window.GetVisualDescendants().OfType<Layoutable>().ToList();
                    var measureInvalid = visuals.Count(v => !v.IsMeasureValid);
                    var arrangeInvalid = visuals.Count(v => !v.IsArrangeValid);
                    sw.Restart();
                    Pump();
                    var first = sw.Elapsed.TotalMilliseconds;
                    var tail = Quiet();
                    var kb = (GC.GetAllocatedBytesForCurrentThread() - b0) / 1024;
                    if (round > 0) // round 0 warms the JIT and the overlay loads
                        samples.Add(new Sample(current, to, sync, first, tail, appRc, winRc, variant, accent, glass,
                            measureInvalid, arrangeInvalid, layouts, kb, (scrim?.CaptureCount ?? 0) - captures0));
                    current = to;
                }
            }
            foreach (var s in samples.Take(Cycle.Length))
                Log($"{scenario} {s.From}->{s.To}: sync {s.SyncMs:0.0} ms, first frame {s.FirstFrameMs:0.0} ms, tail {s.TailMs:0.0} ms | " +
                    $"app RC {s.AppRc}, window RC {s.WinRc}, variant {s.Variant}, AccentApplied {s.Accent}, AppGlass {s.Glass} | " +
                    $"measure-invalid {s.MeasureInvalid}, arrange-invalid {s.ArrangeInvalid}, layout passes {s.LayoutPasses}, scrim captures {s.ScrimCaptures} | {s.Kb} KB");
            static double Median(IEnumerable<double> xs) { var l = xs.OrderBy(x => x).ToList(); return l[l.Count / 2]; }
            Log($"{scenario} median of {samples.Count}: sync {Median(samples.Select(s => s.SyncMs)):0.0} ms (max {samples.Max(s => s.SyncMs):0.0}), " +
                $"first frame {Median(samples.Select(s => s.FirstFrameMs)):0.0} ms (max {samples.Max(s => s.FirstFrameMs):0.0}), " +
                $"sync+first {Median(samples.Select(s => s.SyncMs + s.FirstFrameMs)):0.0} ms (max {samples.Max(s => s.SyncMs + s.FirstFrameMs):0.0}), " +
                $"tail {Median(samples.Select(s => s.TailMs)):0.0} ms | window RC total {samples.Sum(s => s.WinRc)} " +
                $"({samples.Sum(s => s.WinRc) / (double)samples.Count:0.0}/switch), variant {samples.Sum(s => s.Variant)}");
            return samples;
        }

        try
        {
            Run("artist");

            settings.LiquidGlassEnabled = true;
            Quiet();
            Run("artist+glass");

            noctis.SetTintWindowWithAccent(true);
            Quiet();
            Run("artist+glass+tint");

            vm.IsSettingsModalOpen = true;
            for (var i = 0; i < 30; i++) { Pump(); Thread.Sleep(10); }
            Run("settings+glass+tint");
        }
        finally
        {
            vm.IsSettingsModalOpen = false;
            noctis.SetTintWindowWithAccent(false);
            settings.LiquidGlassEnabled = false;
            Apply("Gray");
            Quiet();
            window.Close();
        }
    }
}
