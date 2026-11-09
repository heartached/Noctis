using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's navigation motion (Motion), sampled over time: every open, close, push, pop and
/// tab switch records where its pages sit on screen (their origin in window coordinates, so any
/// RenderTransform counts) and how opaque they show, frame by frame of the real-time headless
/// clock. A snap goes from start to end in one frame; a glide passes through many states, one
/// way, with no jump. With NOCTIS_MOTION_OUT set, each curve is written there as CSV.
/// Interruptions (Back mid-push, a double tap, Back mid-open) must land in a clean state.
/// </summary>
public class MobileMotionTests
{
    private const double W = 412, H = 915;

    // ── Recorder ──

    private sealed record Sample(double Ms, string Target, bool Present, double X, double Y, double Opacity, double Scale);

    /// <summary>Where <paramref name="v"/> shows: its origin in the window, its opacity through
    /// every ancestor (0 when hidden) and its horizontal scale.</summary>
    private static Sample Probe(double ms, string name, Visual? v, Window window)
    {
        if (v == null || !v.IsAttachedToVisualTree()) return new Sample(ms, name, false, 0, 0, 0, 0);
        var origin = v.TranslatePoint(default, window) ?? default;
        var far = v.TranslatePoint(new Point(100, 0), window) ?? default;
        double opacity = v.IsEffectivelyVisible ? 1 : 0;
        for (Visual? a = v; a != null && opacity > 0; a = a.GetVisualParent()) opacity *= a.Opacity;
        return new Sample(ms, name, true, origin.X, origin.Y, opacity, (far.X - origin.X) / 100);
    }

    /// <summary>Runs <paramref name="act"/>, then the headless clock in real time for
    /// <paramref name="ms"/>, probing each target every frame.</summary>
    private static List<Sample> Record(string scenario, Window window, Action act, int ms, params (string Name, Func<Visual?> Find)[] targets)
    {
        var samples = new List<Sample>();
        void ProbeAll(double t)
        {
            foreach (var (name, find) in targets) samples.Add(Probe(t, name, find(), window));
        }
        ProbeAll(-1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        act();
        window.UpdateLayout();
        ProbeAll(clock.Elapsed.TotalMilliseconds);
        while (clock.ElapsedMilliseconds < ms)
        {
            Pump(window);
            ProbeAll(clock.Elapsed.TotalMilliseconds);
        }
        Write(scenario, samples);
        return samples;
    }

    private static void Pump(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
        System.Threading.Thread.Sleep(12);
    }

    /// <summary>Real time passes, then whatever still moves lands.</summary>
    private static void Settle(Window window, int ms = 700)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) Pump(window);
        MobileFixtures.Land(window);
    }

    private static void Write(string scenario, List<Sample> samples)
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_MOTION_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string> { "ms,target,present,x,y,opacity,scale" };
        lines.AddRange(samples.Select(s => string.Join(",", s.Ms.ToString("0.0", c), s.Target, s.Present ? 1 : 0,
            s.X.ToString("0.0", c), s.Y.ToString("0.0", c), s.Opacity.ToString("0.000", c), s.Scale.ToString("0.000", c))));
        File.WriteAllLines(Path.Combine(dir, scenario + ".csv"), lines);
    }

    /// <summary>The most of a run's distance Motion's curve covers per unit of the run's time
    /// (its steepest slope, about 4.2 for the glide curve).</summary>
    private static readonly double Steepest =
        Enumerable.Range(0, 1000).Max(i => Motion.Ease.Ease((i + 1) / 1000.0) - Motion.Ease.Ease(i / 1000.0)) * 1000;

    /// <summary>
    /// The target's <paramref name="axis"/> glides from <paramref name="from"/> to <paramref name="to"/>:
    /// it starts there, ends there, passes through at least five states in between, only ever
    /// moves one way, and no frame jumps more than <paramref name="maxStep"/> of the distance (the
    /// headless frames come unevenly, so a step can be large; a snap is all of it in one).
    /// A frame the machine took long over (a loaded CI runner: 80 ms and more between two
    /// samples, or a first step drawn well after the run's clock began) may carry the glide
    /// further, up to what its curve covers, over <paramref name="run"/>, in the time since the
    /// target last moved (the run's length, <see cref="Motion.Push"/> unless given; a shorter
    /// one only loosens this). Frames where the target is absent or transparent (a page not
    /// yet on screen) are skipped.
    /// </summary>
    private static void AssertGlides(List<Sample> all, string target, Func<Sample, double> axis, double from, double to,
        double maxStep = 0.6, int minMs = 200, int maxMs = 900, bool shownOnly = true, int minBetween = 5, TimeSpan? run = null)
    {
        var runMs = (run ?? Motion.Push).TotalMilliseconds;
        var s = all.Where(x => x.Target == target && x.Ms >= 0 && x.Present && (!shownOnly || x.Opacity > 0.01)).ToList();
        Assert.NotEmpty(s);
        var values = s.Select(axis).ToList();
        var span = Math.Abs(to - from);
        Assert.True(Math.Abs(values[0] - from) <= Math.Max(2, span * 0.1), $"{target} starts at {values[0]:0.#}, not {from}");
        Assert.True(Math.Abs(values[^1] - to) <= Math.Min(0.5, span * 0.01), $"{target} ends at {values[^1]:0.###}, not {to}");
        var tol = Math.Min(0.5, span * 0.01);
        var between = values.Count(v => v > Math.Min(from, to) + tol && v < Math.Max(from, to) - tol);
        Assert.True(between >= minBetween, $"{target}: {between} frames between {from} and {to} (a snap has none)");
        var sign = Math.Sign(to - from);
        var since = s[0].Ms;   // when the target took the value it holds: a glide has run at most since then
        for (var i = 1; i < values.Count; i++)
        {
            var step = values[i] - values[i - 1];
            Assert.True(step * sign >= -Math.Min(0.5, span * 0.01), $"{target} turned back at {s[i].Ms:0} ms ({values[i - 1]:0.#} -> {values[i]:0.#})");
            var movingMs = s[i].Ms - since;
            Assert.True(Math.Abs(step) <= span * Math.Max(maxStep, Steepest * movingMs / runMs),
                $"{target} jumped {step:0.#} at {s[i].Ms:0} ms ({movingMs:0} ms after it last moved)");
            if (Math.Abs(step) > tol) since = s[i].Ms;
        }
        var landed = s.First(x => Math.Abs(axis(x) - to) <= Math.Min(0.5, span * 0.01)).Ms;
        Assert.InRange(landed, minMs, maxMs);
    }

    /// <summary>When the target first moved after the action, in ms. A close starts at once: the
    /// second of its two start frames once waited for something else to redraw (440 ms).</summary>
    private static double FirstMove(List<Sample> all, string target)
    {
        var s = all.Where(x => x.Target == target && x.Ms >= 0).ToList();
        return s.First(x => x != s[0] && (x.X, x.Y, x.Opacity, x.Scale) != (s[0].X, s[0].Y, s[0].Opacity, s[0].Scale)).Ms;
    }

    /// <summary>The target is gone (or hidden) by the end of the recording.</summary>
    private static void AssertGone(List<Sample> all, string target) =>
        Assert.True(all.Where(x => x.Target == target).Last() is { Present: false } or { Opacity: 0 }, $"{target} still on screen");

    // ── Rig ──

    private static (MobileFixtures.Rig Rig, Window Window, ShellView View) Open(bool account = false, bool playing = false)
    {
        var songs = Enumerable.Range(0, 4).Select(i => MobileFixtures.Song($"Song {i}")).ToArray();
        var album = MobileFixtures.MakeAlbum("Record", "Band", songs);
        var rig = MobileFixtures.MakeRig(songs, new[] { album });
        if (account)
        {
            var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
            {
                Account = new MobileAccountTests.FakeAccount(),
                Marshal = a => a(),
                TintFactory = rig.Shell.TintFactory,
            };
            rig = new MobileFixtures.Rig
            {
                Shell = shell, Library = rig.Library, Player = rig.Player, Persistence = rig.Persistence, History = rig.History, Root = rig.Root,
            };
        }
        var window = MobileFixtures.Mount(rig.Shell, out var view, W, H);
        if (playing) rig.Shell.Player.PlayTracks(songs, 0);
        Settle(window, 200);
        return (rig, window, view);
    }

    private static T? Find<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    /// <summary>The tab content, which steps back under a sheet.</summary>
    private static Visual App(ShellView view) => view.FindControl<Panel>("AppLayer")!;

    /// <summary>Nothing left moving, half-way, hidden-but-hit, or untouchable: the state a tap needs.</summary>
    private static void AssertAtRest(ShellView view)
    {
        Assert.False(Motion.AnyRunning, "something is still moving");
        var app = view.FindControl<Panel>("AppLayer")!;
        Assert.Null(app.RenderTransform);
        var dim = view.FindControl<Border>("SheetDim")!;
        Assert.False(dim.IsVisible);
        Assert.Null(view.FindControl<PageStack>("SheetHost")!.Top);
        Assert.True(view.FindControl<PageStack>("SheetHost")!.IsHitTestVisible);
        var root = view.FindControl<Panel>("RootLayer")!;
        Assert.True(root.IsVisible);
        Assert.True(root.IsHitTestVisible);
        Assert.Null(root.RenderTransform);
        Assert.Equal(1, view.FindControl<Panel>("TabContent")!.Opacity);
    }

    // ── Sheets: Settings rises, Account is pushed inside it ──

    [AvaloniaFact]
    public void ProfileCircle_RaisesSettings_AsTheAppStepsBack()
    {
        var (rig, window, view) = Open(account: true);
        using var _ = rig;
        var s = Record("01_profile_to_settings", window, () => rig.Shell.OpenSettingsCommand.Execute(null), 900,
            ("settings", () => Find<SettingsPage>(view)), ("home", () => App(view)));
        AssertGlides(s, "settings", x => x.Y, H, 0);
        AssertGlides(s, "home", x => x.Scale, 1, Motion.BehindScale, maxStep: 0.6);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsClose_DropsTheSheet_AndTheAppComesForward()
    {
        var (rig, window, view) = Open(account: true);
        using var _ = rig;
        rig.Shell.OpenSettingsCommand.Execute(null);
        Settle(window);
        var s = Record("02_settings_close", window, () => rig.Shell.NavigateBackCommand.Execute(null), 900,
            ("settings", () => Find<SettingsPage>(view)), ("home", () => App(view)));
        AssertGlides(s.Where(x => x.Y < H - 0.5 || x.Target != "settings" || x.Ms < 5).ToList(), "settings", x => x.Y, 0, H - 1, maxStep: 0.5);
        AssertGone(s, "settings");
        AssertGlides(s, "home", x => x.Scale, Motion.BehindScale, 1, maxStep: 0.6);
        Assert.InRange(FirstMove(s, "settings"), 0, 150);
        AssertAtRest(view);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsToAccount_PushesInsideTheSheet_AndBackReverses()
    {
        var (rig, window, view) = Open(account: true);
        using var _ = rig;
        rig.Shell.OpenSettingsCommand.Execute(null);
        Settle(window);
        var s = Record("03_settings_to_account", window, () => rig.Shell.OpenAccountCommand.Execute(null), 800,
            ("account", () => Find<AccountPage>(view)), ("settings", () => Find<SettingsPage>(view)));
        AssertGlides(s, "account", x => x.X, W - 1, 0);
        AssertGlides(s.Where(x => x.Target != "settings" || x.Present).ToList(), "settings", x => x.X, 0, -W * Motion.PushShift);
        AssertGone(s, "settings");   // once covered, the page under is let go (re-made on Back)

        var back = Record("04_account_close", window, () => rig.Shell.NavigateBackCommand.Execute(null), 800,
            ("account", () => Find<AccountPage>(view)), ("settings", () => Find<SettingsPage>(view)));
        AssertGlides(back.Where(x => x.Target != "account" || x.Present).ToList(), "account", x => x.X, 0, W);
        AssertGone(back, "account");
        AssertGlides(back, "settings", x => x.X, -W * Motion.PushShift, 0);
        Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsPane_SlidesOnTheSamePush()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        rig.Shell.OpenSettingsCommand.Execute(null);
        Settle(window);
        var vm = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        var page = Find<SettingsPage>(view)!;
        Visual Sub() => MobileFixtures.Named<ScrollViewer>(page, "SubScroll");
        Visual Main() => MobileFixtures.Named<ScrollViewer>(page, "MainScroll");
        var s = Record("05_settings_pane_push", window, () => vm.OpenPaneCommand.Execute(SettingsPane.Folders), 700,
            ("sub", Sub), ("main", Main));
        AssertGlides(s, "sub", x => x.X, W, 0);
        var back = Record("06_settings_pane_back", window, () => rig.Shell.TryHandleBack(), 700, ("sub", Sub), ("main", Main));
        AssertGlides(back, "sub", x => x.X, 0, W, shownOnly: false);   // hidden once it is out
        window.Close();
    }

    // ── Pushes over a tab ──

    [AvaloniaFact]
    public void LibraryRow_PushesSongs_AndAndroidBackReverses()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        Settle(window);
        var s = Record("07_library_to_songs", window, () => rig.Shell.OpenSongsCommand.Execute(null), 700,
            ("songs", () => Find<SongListPage>(view)), ("library", () => Find<LibraryPage>(view)));
        AssertGlides(s, "songs", x => x.X, W - 1, 0);
        AssertGlides(s.Where(x => x.Target != "library" || x.Opacity > 0).ToList(), "library", x => x.X, 0, -W * Motion.PushShift);
        Assert.False(Find<LibraryPage>(view)!.IsEffectivelyVisible);   // covered: hidden once the page lands

        var back = Record("08_songs_back", window, () => rig.Shell.TryHandleBack(), 700,
            ("songs", () => Find<SongListPage>(view)), ("library", () => Find<LibraryPage>(view)));
        AssertGlides(back.Where(x => x.Target != "songs" || x.Present).ToList(), "songs", x => x.X, 0, W);
        AssertGone(back, "songs");
        AssertGlides(back, "library", x => x.X, -W * Motion.PushShift, 0);
        AssertAtRest(view);
        window.Close();
    }

    [AvaloniaFact]
    public void AlbumsGrid_PushesAlbum_AndBackReverses()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        Settle(window);
        var album = rig.Library.Albums.First();
        var s = Record("09_albums_to_album", window, () => rig.Shell.OpenAlbumCommand.Execute(album), 700,
            ("album", () => Find<AlbumPage>(view)), ("grid", () => Find<AlbumGridPage>(view)));
        AssertGlides(s, "album", x => x.X, W - 1, 0);
        AssertGone(s, "grid");
        // Landed, nothing is under the album: its shade went too (it stayed on, hidden behind the page).
        Assert.All(view.FindControl<PageStack>("PageHost")!.Children.OfType<Border>(), b => Assert.False(b.IsVisible));
        var back = Record("10_album_back", window, () => rig.Shell.NavigateBackCommand.Execute(null), 700,
            ("album", () => Find<AlbumPage>(view)), ("grid", () => Find<AlbumGridPage>(view)));
        AssertGlides(back.Where(x => x.Target != "album" || x.Present).ToList(), "album", x => x.X, 0, W);
        AssertGlides(back, "grid", x => x.X, -W * Motion.PushShift, 0);
        AssertGone(back, "album");
        window.Close();
    }

    // ── The player ──

    [AvaloniaFact]
    public void NowPlaying_RisesAndDrops_AsASheet()
    {
        var (rig, window, view) = Open(playing: true);
        using var _ = rig;
        var s = Record("11_nowplaying_open", window, () => rig.Shell.OpenNowPlayingCommand.Execute(null), 900,
            ("nowplaying", () => Find<NowPlayingPage>(view)), ("home", () => App(view)));
        AssertGlides(s, "nowplaying", x => x.Y, H, 0);
        AssertGlides(s, "home", x => x.Scale, 1, Motion.BehindScale, maxStep: 0.6);
        var close = Record("12_nowplaying_close", window, () => rig.Shell.CloseNowPlayingCommand.Execute(null), 900,
            ("nowplaying", () => Find<NowPlayingPage>(view)), ("home", () => App(view)));
        AssertGlides(close.Where(x => x.Target != "nowplaying" || x.Opacity > 0).ToList(), "nowplaying", x => x.Y, 0, H, maxStep: 0.5);
        AssertGone(close, "nowplaying");
        AssertAtRest(view);
        window.Close();
    }

    [AvaloniaFact]
    public void Lyrics_FadesOverThePlayer()
    {
        var (rig, window, view) = Open(playing: true);
        using var _ = rig;
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        Settle(window);
        var s = Record("13_lyrics_open", window, () => rig.Shell.ToggleLyricsCommand.Execute(null), 600,
            ("lyrics", () => Find<LyricsPage>(view)));
        AssertGlides(s.Where(x => x.Ms > 0).ToList().Prepend(s.First(x => x.Ms >= 0) with { Opacity = 1e-3 }).ToList(), "lyrics", x => x.Opacity, 0, 1, minMs: 100, maxMs: 500, maxStep: 0.7, minBetween: 2, run: Motion.Fade);
        var close = Record("14_lyrics_close", window, () => rig.Shell.TryHandleBack(), 600,
            ("lyrics", () => Find<LyricsPage>(view)));
        Assert.Contains(close, x => x.Opacity is > 0.05 and < 0.95);   // faded, not snapped
        AssertGone(close, "lyrics");
        Assert.InRange(FirstMove(close, "lyrics"), 0, 150);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Queue_RisesOnTheSheetTiming()
    {
        var (rig, window, view) = Open(playing: true);
        using var _ = rig;
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        Settle(window);
        Visual? Sheet() => Find<QueuePage>(view)?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Sheet");
        var s = Record("15_queue_open", window, () => rig.Shell.ToggleQueueCommand.Execute(null), 800, ("queue", Sheet));
        var top = s.Last().Y;
        AssertGlides(s.Where(x => x.Y > 0).ToList(), "queue", x => x.Y, s.Where(x => x.Present && x.Ms >= 0).Max(x => x.Y), top, maxStep: 0.5);
        Record("16_queue_close", window, () => rig.Shell.TryHandleBack(), 800, ("queue", Sheet));
        window.Close();
    }

    // ── Tabs ──

    [AvaloniaFact]
    public void TabSwitch_FadesTheNewTabIn_QuicklyAndWithoutSlide()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        var s = Record("17_tab_home_to_library", window, () => rig.Shell.SelectTabCommand.Execute(MobileTab.Library), 500,
            ("library", () => Find<LibraryPage>(view)), ("home", () => Find<HomePage>(view)));
        var library = s.Where(x => x.Target == "library" && x.Ms >= 0).ToList();
        Assert.All(library, x => Assert.Equal(0, x.X, 1));                     // no slide
        Assert.Contains(library, x => x.Opacity is > 0.05 and < 0.95);         // a fade, not a snap
        Assert.InRange(library.First(x => x.Opacity >= 0.999).Ms, 60, 400);    // a quick one
        Record("18_back_to_home_tab", window, () => rig.Shell.TryHandleBack(), 500,
            ("library", () => Find<LibraryPage>(view)), ("home", () => Find<HomePage>(view)));
        Assert.True(rig.Shell.IsHomeSelected);
        window.Close();
    }

    // ── Interruptions ──

    /// <summary>Back while a page is still sliding in turns it around from where it is (no jump
    /// to either end), and everything lands touchable.</summary>
    [AvaloniaFact]
    public void BackMidPush_TurnsThePageAround_AndLeavesNothingStuck()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        Settle(window);
        var s = Record("19_back_mid_push", window, () =>
        {
            rig.Shell.OpenSongsCommand.Execute(null);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Until the page is on its way, as BackMidSheet waits: a loaded runner's slow frames
            // had not started the slide by 140 ms ("Back came at 411").
            while (clock.ElapsedMilliseconds < 140 || Probe(0, "songs", Find<SongListPage>(view), window) is not { Opacity: > 0.01, X: < W - 50 })
            {
                Pump(window);
                if (clock.ElapsedMilliseconds > 1500) break;
            }
            rig.Shell.TryHandleBack();
        }, 800, ("songs", () => Find<SongListPage>(view)), ("library", () => Find<LibraryPage>(view)));
        var songs = s.Where(x => x.Target == "songs" && x.Ms >= 0 && x.Present).Select(x => x.X).ToList();
        Assert.True(songs[0] is > 1 and < W - 1, $"Back came at {songs[0]:0.#}: not mid-slide");
        for (var i = 1; i < songs.Count; i++) Assert.True(songs[i] >= songs[i - 1] - 0.5, "the page did not turn straight back");
        AssertGone(s, "songs");
        Assert.Null(rig.Shell.CurrentPage);
        AssertAtRest(view);
        window.Close();
    }

    /// <summary>Back while Settings rises: it drops from where it got to; nothing is left dimmed,
    /// scaled or blocking taps.</summary>
    [AvaloniaFact]
    public void BackMidSheet_DropsItFromWhereItIs_AndLeavesTheAppTouchable()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        var s = Record("20_back_mid_settings_open", window, () =>
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 120 || Find<SettingsPage>(view) is not { } p || p.TranslatePoint(default, window)!.Value.Y > H - 50)
            {
                Pump(window);
                if (clock.ElapsedMilliseconds > 1500) break;
            }
            rig.Shell.TryHandleBack();
        }, 900, ("settings", () => Find<SettingsPage>(view)));
        var ys = s.Where(x => x.Target == "settings" && x.Ms >= 0 && x.Present && x.Opacity > 0).Select(x => x.Y).ToList();
        Assert.True(ys[0] < H - 1, "Back came before the sheet moved");
        for (var i = 1; i < ys.Count; i++) Assert.True(ys[i] >= ys[i - 1] - 0.5, "the sheet did not drop straight back");
        AssertGone(s, "settings");
        AssertAtRest(view);

        // The app takes a tap again: the profile circle opens Settings.
        Settle(window, 50);
        var profile = view.GetVisualDescendants().OfType<ProfileButton>().First(b => b.IsEffectivelyVisible);
        var centre = profile.TranslatePoint(new Point(profile.Bounds.Width / 2, profile.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        window.Close();
    }

    /// <summary>A second tap on the profile circle while Settings rises opens nothing more: the
    /// dim takes the tap, and Settings is one sheet whatever asks for it.</summary>
    [AvaloniaFact]
    public void DoubleTapOnTheProfileCircle_OpensOneSettings()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        Settle(window, 50);
        var profile = view.GetVisualDescendants().OfType<ProfileButton>().First(b => b.IsEffectivelyVisible);
        var centre = profile.TranslatePoint(new Point(profile.Bounds.Width / 2, profile.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        for (var i = 0; i < 4; i++) Pump(window);
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        rig.Shell.OpenSettingsCommand.Execute(null);   // and a command that slips through anyway
        Settle(window);
        Assert.Single(rig.Shell.Pages);
        Assert.Single(view.GetVisualDescendants().OfType<SettingsPage>());
        window.Close();
    }

    /// <summary>A double tap on a row while its page slides in pushes it once: the pages in
    /// motion take no touches.</summary>
    [AvaloniaFact]
    public void DoubleTapOnARow_PushesOnePage()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        Settle(window);
        var row = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && b.Name == "RecentlyAddedHeader");
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < 4; i++) Pump(window);
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Assert.Single(rig.Shell.Pages);
        for (var i = 0; i < 4; i++) Pump(window);
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Settle(window);
        Assert.Single(rig.Shell.Pages);
        window.Close();
    }

    /// <summary>Back pressed fast through Account, Settings and an album lands at the tab root
    /// with nothing half-way.</summary>
    [AvaloniaFact]
    public void RapidBacks_LandAtTheRoot_WithNothingHalfWay()
    {
        var (rig, window, view) = Open(account: true);
        using var _ = rig;
        rig.Shell.OpenSettingsCommand.Execute(null);
        for (var i = 0; i < 6; i++) Pump(window);
        rig.Shell.OpenAccountCommand.Execute(null);
        for (var i = 0; i < 6; i++) Pump(window);
        Assert.True(rig.Shell.TryHandleBack());
        for (var i = 0; i < 3; i++) Pump(window);
        Assert.True(rig.Shell.TryHandleBack());
        Settle(window, 900);
        Assert.Empty(rig.Shell.Pages);
        // Settings' view stays in the tree, hidden, for the next open (MobilePage.KeepsView).
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<SettingsPage>(), p => p.IsEffectivelyVisible);
        Assert.Empty(view.GetVisualDescendants().OfType<AccountPage>());
        AssertAtRest(view);
        window.Close();
    }

    /// <summary>A tab tap while a page slides in jumps straight to the tab: nothing keeps moving.</summary>
    [AvaloniaFact]
    public void TabTapMidPush_JumpsToTheTab()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        Settle(window);
        rig.Shell.OpenSongsCommand.Execute(null);
        for (var i = 0; i < 5; i++) Pump(window);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Home);
        Settle(window);
        Assert.Empty(view.GetVisualDescendants().OfType<SongListPage>());
        Assert.True(Find<HomePage>(view)!.IsEffectivelyVisible);
        AssertAtRest(view);
        window.Close();
    }

    // ── Swipe down to dismiss ──

    /// <summary>Drags the mouse from <paramref name="from"/> down by <paramref name="by"/> in
    /// steps, the frames running between them; <paramref name="during"/> sees each step.</summary>
    private static void Drag(Window window, Point from, double by, Action<double>? during = null, int msPerStep = 0)
    {
        // Hit-testing reads the compositor's committed scene: let the landed state be drawn first.
        for (var i = 0; i < 4; i++) Pump(window);
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        for (var dy = 10.0; dy <= by; dy += 10)
        {
            window.MouseMove(from + new Point(0, dy), RawInputModifiers.LeftMouseButton);
            window.UpdateLayout();
            during?.Invoke(dy);
            if (msPerStep > 0) System.Threading.Thread.Sleep(msPerStep);   // slow: no flick
        }
        window.MouseUp(from + new Point(0, by), MouseButton.Left, RawInputModifiers.None);
    }

    [AvaloniaFact]
    public void SwipeDownOnTheSettingsHeader_FollowsTheFinger_AndDropsTheSheet()
    {
        var (rig, window, view) = Open(account: true);
        using var _ = rig;
        rig.Shell.OpenSettingsCommand.Execute(null);
        rig.Shell.OpenAccountCommand.Execute(null);   // the whole sheet goes, Account inside it too
        Settle(window);
        var host = view.FindControl<PageStack>("SheetHost")!;
        var app = view.FindControl<Panel>("AppLayer")!;
        // From the close button: the header is the handle, and a drag that starts on a button is not a tap.
        var close = MobileFixtures.Named<Button>(Find<AccountPage>(view)!, "CloseButton");
        var start = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        Drag(window, start, 400, dy =>
        {
            if (dy <= 20) return;   // inside the slop the sheet stays
            Assert.Equal(dy, host.RenderTransform!.Value.M32, 1);              // under the finger
            Assert.InRange(app.RenderTransform!.Value.M11, Motion.BehindScale, 1);   // the app coming forward
        });
        Assert.Empty(rig.Shell.Pages);
        Settle(window);
        AssertAtRest(view);
        window.Close();
    }

    [AvaloniaFact]
    public void AShortSwipe_SettlesTheSheetBackUp()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        rig.Shell.OpenSettingsCommand.Execute(null);
        Settle(window);
        var host = view.FindControl<PageStack>("SheetHost")!;
        var close = MobileFixtures.Named<Button>(Find<SettingsPage>(view)!, "CloseButton");
        var centre = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        var moved = 0.0;
        Drag(window, centre, 80, _ => moved = Math.Max(moved, host.RenderTransform?.Value.M32 ?? 0), msPerStep: 50);
        Assert.True(moved > 50, $"the sheet followed the finger only {moved:0} px");
        Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);   // a drag that started on Close did not close
        Settle(window);
        Assert.Equal(0, host.RenderTransform?.Value.M32 ?? 0, 1);
        Assert.Equal(Motion.BehindScale, view.FindControl<Panel>("AppLayer")!.RenderTransform!.Value.M11, 3);
        // Still a sheet that takes taps: its close button closes it.
        for (var i = 0; i < 4; i++) Pump(window);
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Assert.Empty(rig.Shell.Pages);
        window.Close();
    }

    [AvaloniaFact]
    public void SwipeDownOnTheCover_ClosesNowPlaying_ButNotOnTheLowerControls()
    {
        var (rig, window, view) = Open(playing: true);
        using var _ = rig;
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        Settle(window);
        Drag(window, new Point(200, H * 0.75), 300);   // the transport and sliders: theirs
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Settle(window);
        Drag(window, new Point(200, H * 0.3), 300);
        Assert.False(rig.Shell.IsNowPlayingOpen);
        Settle(window);
        AssertAtRest(view);
        window.Close();
    }
}
