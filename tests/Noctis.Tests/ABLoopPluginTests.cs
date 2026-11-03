using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Plugins.ABLoop;
using Noctis.Plugins.ABLoop.Looping;
using Noctis.Plugins.ABLoop.Views;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A-B Loop (official, plugin API 1.4): capture, nudge, the repeat count, the gap, saved loops per
/// song, clearing on a song change, and the loop-back timing against a simulated host whose
/// position moves in ~100 ms steps (what Noctis reports), measured over hundreds of loops.
/// </summary>
public class ABLoopPluginTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>
    /// A simulated Noctis: the audio runs on a simulated clock; the host's position (what a
    /// plugin reads) only moves when the engine reports, every <see cref="ReportEvery"/> (with
    /// jitter), shows up on the UI thread after a dispatch delay, and is not updated for 300 ms
    /// after a seek (PlayerViewModel's SeekSettleWindowMs). A seek sets the reported position at
    /// once, as SeekToPosition does.
    /// </summary>
    internal sealed class SimPlayer : ILoopHost
    {
        private readonly Random _random;
        private TimeSpan _base;
        private TimeSpan _baseAt;
        private TimeSpan _nextReport;
        private TimeSpan _settleUntil;
        private (TimeSpan Due, TimeSpan Value)? _pending;

        public SimPlayer(int seed = 1, double reportMs = 100)
        {
            _random = new Random(seed);
            ReportEvery = Ms(reportMs);
            IsPlaying = true;
        }

        public TimeSpan Now { get; private set; }
        public TimeSpan ReportEvery { get; }
        public double ReportJitterMs { get; set; } = 8;
        public double DispatchDelayMaxMs { get; set; } = 6;
        public TimeSpan True => IsPlaying ? _base + (Now - _baseAt) : _base;
        public readonly List<(TimeSpan At, TimeSpan TruePos, TimeSpan Target)> Seeks = new();
        public readonly List<string> Calls = new();

        public TimeSpan Position { get; private set; }
        public bool IsPlaying { get; private set; }
        public TimeSpan Duration { get; set; } = S(240);

        public void Seek(TimeSpan position)
        {
            Seeks.Add((Now, True, position));
            Calls.Add("seek");
            _base = position;
            _baseAt = Now;
            Position = position;
            _pending = null;
            _settleUntil = Now + Ms(300);
        }

        public void Play()
        {
            Calls.Add("play");
            if (IsPlaying) return;
            _baseAt = Now;
            IsPlaying = true;
        }

        public void Pause()
        {
            Calls.Add("pause");
            if (!IsPlaying) return;
            _base = True;
            IsPlaying = false;
        }

        /// <summary>Jumps the song to <paramref name="at"/> without telling the loop (a fresh start).</summary>
        public void Start(TimeSpan at)
        {
            _base = at;
            _baseAt = Now;
            Position = at;
            _nextReport = Now + ReportEvery;
        }

        /// <summary>Simulated time passes in 1 ms steps; the engine reports on its cadence.</summary>
        public void Advance(TimeSpan by)
        {
            var end = Now + by;
            while (Now < end)
            {
                Now += Ms(1);
                if (_pending is { } p && Now >= p.Due)
                {
                    Position = p.Value;
                    _pending = null;
                }
                if (Now >= _nextReport)
                {
                    _nextReport = Now + ReportEvery + Ms((_random.NextDouble() * 2 - 1) * ReportJitterMs);
                    if (IsPlaying && Now >= _settleUntil)
                        _pending = (Now + Ms(_random.NextDouble() * DispatchDelayMaxMs), True);
                }
            }
        }

        // ILoopHost
        public string? TrackPath { get; set; } = "C:/music/song.flac";
        public string TrackTitle => "Song";
        public string TrackArtist => "Artist";
        public event EventHandler? TrackChanged;
        public void ChangeTrack(string? path)
        {
            TrackPath = path;
            TrackChanged?.Invoke(this, EventArgs.Empty);
        }
        public TimeSpan GapSetting { get; set; }
        public bool KeepLoops { get; set; } = true;
        public TimeSpan NudgeSetting { get; set; } = Ms(100);
        public event EventHandler? SettingsChanged;
        public void RaiseSettings() => SettingsChanged?.Invoke(this, EventArgs.Empty);
        public string? DataDirectory { get; set; }
        public void Log(string message) { }
    }

    /// <summary>Runs the engine's ~15 ms timer (with jitter) against the simulation.</summary>
    private static void Run(SimPlayer sim, LoopEngine engine, TimeSpan duration, Random random, double tickMs = 15, double jitterMs = 2)
    {
        var end = sim.Now + duration;
        while (sim.Now < end)
        {
            sim.Advance(Ms(Math.Max(1, Math.Round(tickMs + (random.NextDouble() * 2 - 1) * jitterMs))));
            engine.Tick();
        }
    }

    private static (SimPlayer, LoopEngine) NewLoop(int seed = 1)
    {
        var sim = new SimPlayer(seed);
        var engine = new LoopEngine(sim, () => sim.Now);
        sim.Start(S(10));
        return (sim, engine);
    }

    // ── Capture and nudge ──

    [Fact]
    public void Capture_UsesThePredictedPosition_NotTheLast100msStep()
    {
        var (sim, engine) = NewLoop();
        var random = new Random(5);
        Run(sim, engine, S(2.37), random);
        engine.CaptureA();
        Assert.True((engine.A!.Value - sim.True).Duration() < Ms(20), $"A {engine.A} vs {sim.True}");
        Run(sim, engine, S(3.11), random);
        engine.CaptureB();
        Assert.True((engine.B!.Value - sim.True).Duration() < Ms(20), $"B {engine.B} vs {sim.True}");
        Assert.True(engine.HasPart);
    }

    [Fact]
    public void Nudge_MovesByTheStep_KeepsAbeforeB_AndStaysInTheSong()
    {
        var (_, engine) = NewLoop();
        engine.SetA(S(10));
        engine.SetB(S(12));
        engine.Nudge(true, Ms(100));
        Assert.Equal(S(10.1), engine.A);
        engine.Nudge(true, S(-1));
        Assert.Equal(S(9.1), engine.A);
        engine.Nudge(false, S(1));
        Assert.Equal(S(13), engine.B);
        engine.Nudge(true, S(10)); // cannot pass B
        Assert.Equal(engine.B - LoopEngine.MinLength, engine.A);
        engine.Nudge(false, S(-100)); // nor B pass A
        Assert.Equal(engine.A + LoopEngine.MinLength, engine.B);
        engine.SetA(S(1));
        engine.Nudge(true, S(-5));
        Assert.Equal(TimeSpan.Zero, engine.A);
        engine.SetB(S(500)); // clamped to the song
        Assert.Equal(S(240), engine.B);

        // B before A swaps them; A past B drops B.
        engine.SetA(S(50));
        engine.SetB(S(40));
        Assert.Equal((S(40), S(50)), (engine.A!.Value, engine.B!.Value));
        engine.SetA(S(60));
        Assert.Null(engine.B);
        Assert.False(engine.HasPart);
    }

    [Fact]
    public void Session_NudgeStep_ShiftTakesTheOtherStep()
    {
        var sim = new SimPlayer();
        using var session = new LoopSession(sim, () => sim.Now, useTimer: false);
        session.Engine.SetPart(S(10), S(20));
        session.Nudge(true, 1);
        Assert.Equal(S(10.1), session.Engine.A);
        session.Nudge(true, 1, otherStep: true);
        Assert.Equal(S(11.1), session.Engine.A);
        sim.NudgeSetting = S(1);
        sim.RaiseSettings();
        session.Nudge(false, -1);
        Assert.Equal(S(19), session.Engine.B);
    }

    // ── Looping ──

    [Fact]
    public void Loop_PlaysThePartCountTimes_ThenPlaysOn()
    {
        var (sim, engine) = NewLoop();
        var finished = 0;
        engine.Finished += (_, _) => finished++;
        engine.SetPart(S(11), S(13));
        engine.Count = 3;
        engine.Enabled = true;
        Run(sim, engine, S(12), new Random(2)); // from 10 s: B at 13 s, back twice (at 3 s and 5 s), on from 7 s
        Assert.Equal(2, sim.Seeks.Count);
        Assert.Equal(1, finished);
        Assert.False(engine.Enabled);
        Assert.True(sim.True > S(17), sim.True.ToString()); // played on past B
        Assert.All(sim.Seeks, s => Assert.Equal(S(11), s.Target));
    }

    [Fact]
    public void Loop_Forever_UntilTurnedOff_AndNotWhenTheUserSeeksPastB()
    {
        var (sim, engine) = NewLoop();
        engine.SetPart(S(10.5), S(11.5));
        engine.Enabled = true;
        Run(sim, engine, S(10), new Random(3));
        Assert.InRange(sim.Seeks.Count, 8, 10);
        engine.Enabled = false;
        var seeks = sim.Seeks.Count;
        Run(sim, engine, S(3), new Random(3));
        Assert.Equal(seeks, sim.Seeks.Count);

        // A jump far past B (the user seeking on) is not pulled back.
        engine.Enabled = true;
        sim.Seek(S(60));
        seeks = sim.Seeks.Count;
        Run(sim, engine, S(2), new Random(3));
        Assert.Equal(seeks, sim.Seeks.Count);
    }

    [Fact]
    public void Gap_PausesAtB_SeeksToA_AndPlaysAfterTheGap()
    {
        var (sim, engine) = NewLoop();
        engine.SetPart(S(10.2), S(11));
        engine.Gap = Ms(500);
        engine.Enabled = true;
        var random = new Random(4);
        while (sim.Seeks.Count == 0) Run(sim, engine, Ms(15), random);
        var jumpedAt = sim.Now;
        Assert.Equal(new[] { "pause", "seek" }, sim.Calls.TakeLast(2));
        Assert.False(sim.IsPlaying);
        Assert.True(engine.InGap);
        while (!sim.IsPlaying) Run(sim, engine, Ms(15), random);
        var waited = sim.Now - jumpedAt;
        Assert.InRange(waited.TotalMilliseconds, 500, 530);
        Assert.Equal("play", sim.Calls.Last());
        Assert.False(engine.InGap);
        // Turning the loop off in a gap plays on right away.
        while (!engine.InGap) Run(sim, engine, Ms(15), random);
        engine.Enabled = false;
        Assert.True(sim.IsPlaying);
    }

    /// <summary>
    /// The loop-back error: where the song truly is when the jump to A is made, minus B. The host
    /// position moves in 100 ms steps (+-8 ms jitter, up to 6 ms to reach the UI thread); the loop
    /// timer runs every 15 ms (+-2 ms). Over 400 loops with B at random points, the predicted loop
    /// is compared with looping on the raw position (what a plugin without prediction gets).
    /// The engine's own seek latency comes on top of both and is not part of this measurement.
    /// </summary>
    [Fact]
    public void LoopBack_Timing_PredictedVersusRawPosition()
    {
        var predicted = MeasureLoops(usePrediction: true, loops: 400, seed: 11);
        var raw = MeasureLoops(usePrediction: false, loops: 400, seed: 11);
        var report = $"loop-back error over 400 loops (true position at the jump minus B):\n"
                     + $"  predicted, 15 ms timer: mean {predicted.Mean:0.0} ms, mean |e| {predicted.MeanAbs:0.0} ms, p95 |e| {predicted.P95:0.0} ms, max |e| {predicted.Max:0.0} ms\n"
                     + $"  raw 100 ms position:    mean {raw.Mean:0.0} ms, mean |e| {raw.MeanAbs:0.0} ms, p95 |e| {raw.P95:0.0} ms, max |e| {raw.Max:0.0} ms";
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(AlbumRoulettePluginTests.PreviewDir(), "abloop-precision.txt"), report + "\n");
        Assert.True(predicted.MeanAbs < 8, report);
        Assert.True(predicted.Max < 25, report);
        Assert.True(raw.MeanAbs > 3 * predicted.MeanAbs, report);
    }

    private static (double Mean, double MeanAbs, double P95, double Max) MeasureLoops(bool usePrediction, int loops, int seed)
    {
        var random = new Random(seed);
        var errors = new List<double>();
        var sim = new SimPlayer(seed) { Duration = S(100000) };
        var engine = new LoopEngine(sim, () => sim.Now);
        sim.Start(S(5));
        for (var i = 0; i < loops; i++)
        {
            // A part of 0.6 to 3 s somewhere ahead, B at a random sub-millisecond point.
            var a = sim.True + S(0.4 + random.NextDouble());
            var b = a + S(0.6 + random.NextDouble() * 2.4) + Ms(random.NextDouble());
            var seeks = sim.Seeks.Count;
            if (usePrediction)
            {
                engine.SetPart(a, b);
                engine.Count = 2; // one jump back, then on
                engine.Enabled = true;
                while (sim.Seeks.Count == seeks)
                {
                    sim.Advance(Ms(Math.Max(1, Math.Round(15 + (random.NextDouble() * 2 - 1) * 2))));
                    engine.Tick();
                }
            }
            else
            {
                // Without prediction: check the reported position as often as it changes.
                var last = sim.Position;
                while (sim.Seeks.Count == seeks)
                {
                    sim.Advance(Ms(1));
                    if (sim.Position != last && sim.Position >= b) sim.Seek(a);
                    last = sim.Position;
                }
            }
            errors.Add((sim.Seeks[^1].TruePos - b).TotalMilliseconds);
            // Let it run on a little before the next part.
            engine.Enabled = false;
            var on = sim.Now + S(0.5 + random.NextDouble());
            while (sim.Now < on)
            {
                sim.Advance(Ms(15));
                engine.Tick();
            }
        }
        var abs = errors.Select(Math.Abs).OrderBy(x => x).ToList();
        return (errors.Average(), abs.Average(), abs[(int)(abs.Count * 0.95)], abs[^1]);
    }

    // ── The song, the saved loops ──

    [Fact]
    public void Session_ClearsTheLoop_WhenTheSongChanges_NotWhenItRestarts()
    {
        var sim = new SimPlayer();
        using var session = new LoopSession(sim, () => sim.Now, useTimer: false);
        session.Engine.SetPart(S(10), S(20));
        session.ToggleLoop();
        Assert.True(session.Engine.Enabled);
        sim.ChangeTrack("C:/music/song.flac"); // the same song again
        Assert.True(session.Engine.HasPart);
        sim.ChangeTrack("C:/music/other.flac");
        Assert.False(session.Engine.HasPart);
        Assert.Null(session.Engine.A);
        Assert.False(session.Engine.Enabled);
        Assert.Equal("C:/music/other.flac", session.TrackPath);
        session.Engine.SetPart(S(1), S(2));
        sim.ChangeTrack(null); // playback cleared
        Assert.False(session.HasTrack);
        Assert.Null(session.Engine.A);
    }

    [Fact]
    public void SavedLoops_PerSong_PersistInTheDataFolder_UnlessKeepIsOff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-abloop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sim = new SimPlayer { DataDirectory = dir };
            using (var session = new LoopSession(sim, () => sim.Now, useTimer: false))
            {
                Assert.Null(session.SaveCurrent()); // no part yet
                session.Engine.SetPart(S(31.2), S(45.8));
                var chorus = session.SaveCurrent()!;
                Assert.Equal("Loop 1", chorus.Name);
                session.Rename(chorus, "Chorus");
                session.Engine.SetPart(S(60), S(62.5));
                session.SaveCurrent("Solo");
                sim.ChangeTrack("C:/music/other.flac");
                Assert.Empty(session.Saved);
                session.Engine.SetPart(S(5), S(6));
                session.SaveCurrent();
            }
            Assert.True(File.Exists(Path.Combine(dir, LoopStore.FileName)));

            var store = new LoopStore(dir);
            Assert.Equal(new[] { "Chorus", "Solo" }, store.For("C:/music/song.flac").Select(l => l.Name));
            Assert.Equal((31.2, 45.8), (store.For("C:/music/song.flac")[0].A, store.For("C:/music/song.flac")[0].B));
            Assert.Single(store.For("C:/music/other.flac"));
            if (OperatingSystem.IsWindows()) Assert.Equal(2, store.For("c:/MUSIC/song.flac").Count); // Windows paths ignore case

            // Jumping to a saved loop: its part, looping, at A.
            var sim2 = new SimPlayer { DataDirectory = dir };
            sim2.Start(S(100));
            using (var session = new LoopSession(sim2, () => sim2.Now, useTimer: false))
            {
                session.Jump(session.Saved[1]);
                Assert.Equal((S(60), S(62.5)), (session.Engine.A!.Value, session.Engine.B!.Value));
                Assert.True(session.Engine.Enabled);
                Assert.Equal(S(60), sim2.Seeks.Single().Target);
                session.Delete(session.Saved[0]);
                Assert.Equal("Solo", Assert.Single(session.Saved).Name);
            }

            // Keep loops off: nothing more is written.
            var before = File.ReadAllText(Path.Combine(dir, LoopStore.FileName));
            var sim3 = new SimPlayer { DataDirectory = dir, KeepLoops = false };
            using (var session = new LoopSession(sim3, () => sim3.Now, useTimer: false))
            {
                session.Engine.SetPart(S(1), S(3));
                Assert.NotNull(session.SaveCurrent("Intro"));
                Assert.Equal(2, session.Saved.Count); // there for this session
            }
            Assert.Equal(before, File.ReadAllText(Path.Combine(dir, LoopStore.FileName)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Settings_GapAndNudge_AreParsed()
    {
        Assert.Equal(TimeSpan.Zero, PluginLoopHost.ParseSeconds("None", 2));
        Assert.Equal(Ms(250), PluginLoopHost.ParseSeconds("0.25 s", 2));
        Assert.Equal(S(1.5), PluginLoopHost.ParseSeconds("1.5 s", 2));
        Assert.Equal(S(2), PluginLoopHost.ParseSeconds("9 s", 2));
        Assert.Equal(Ms(100), PluginLoopHost.ParseSeconds("0.1 s", 1));
        Assert.Equal("1:05.3", LoopSession.Time(S(65.36)));
        Assert.Equal("0:59.9", LoopSession.Time(S(59.99)));
        Assert.Equal("\u221E", LoopSession.CountText(0));
        Assert.Equal("x5", LoopSession.CountText(5));
        Assert.Equal("Gap 0.5 s", LoopSession.GapText(Ms(500)));
    }

    // ── The window ──

    [AvaloniaFact]
    public void View_ButtonsDriveTheLoop_AndTheSavedList()
    {
        var sim = new SimPlayer();
        sim.Start(S(30));
        using var session = new LoopSession(sim, () => sim.Now, useTimer: false);
        var view = new ABLoopView(session);
        var window = new Window { Content = view, Width = ABLoopView.WindowWidth, Height = ABLoopView.WindowHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ABLoopView.WindowWidth, window.Width);
        Click(view, "A");
        sim.Advance(S(4));
        Click(view, "B");
        Assert.True(session.Engine.HasPart);
        Assert.Equal("Loop", view.LoopText);
        Click(view, "Loop");
        Assert.True(view.LoopOn);
        Assert.Equal("Looping", view.LoopText);
        Click(view, "\u221E");
        Assert.Equal("x2", view.CountText);
        Click(view, "Save loop");
        Assert.Equal(1, view.SavedRows);
        Click(view, "Keep on top");
        Assert.True(window.Topmost);
        sim.ChangeTrack(null);
        Assert.True(view.NoSongShown);
        window.Close();
    }

    [AvaloniaFact]
    public void View_ShownInSettings_NeverPinsTheMainWindowOnTop()
    {
        // Settings shows the view in place inside Noctis's main window; "Keep on top" is for the
        // view's own window and must not pin the main one.
        var sim = new SimPlayer();
        sim.Start(S(30));
        using var session = new LoopSession(sim, () => sim.Now, useTimer: false);
        session.Store.SetOnTop(true);
        var view = new ABLoopView(session);
        var window = new Window { Content = new Border { Child = new ContentControl { Content = view } }, Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.Topmost);
        Click(view, "Keep on top"); // off
        Click(view, "Keep on top"); // on again
        Assert.False(window.Topmost);
        window.Close();
    }

    private static void Click(Control root, string name)
        => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>()
            .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Preview_TheWindowAt125Percent()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var dir = AlbumRoulettePluginTests.PreviewDir();
        var sim = new SimPlayer { Duration = S(227) };
        sim.Start(S(52.4));
        using var session = new LoopSession(new TitledSim(sim), () => sim.Now, useTimer: false);
        session.Engine.SetPart(S(41.3), S(63.8));
        session.Engine.Count = 5;
        session.Engine.Enabled = true;
        session.SaveCurrent("Chorus");
        session.Engine.SetPart(S(98.0), S(121.6));
        session.SaveCurrent("Guitar solo");
        session.Engine.SetPart(S(41.3), S(63.8));
        var view = new ABLoopView(session);
        var window = new Window { Content = view, Width = ABLoopView.WindowWidth, Height = ABLoopView.WindowHeight, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        window.Show();
        view.Refresh();
        AlbumRoulettePluginTests.Render(view, Path.Combine(dir, "abloop-preview.png"), 1.25);
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        view.Refresh();
        AlbumRoulettePluginTests.Render(view, Path.Combine(dir, "abloop-preview-light.png"), 1.25);
        window.Close();
    }

    /// <summary>The simulation with a real-looking song for the picture.</summary>
    private sealed class TitledSim : ILoopHost
    {
        private readonly SimPlayer _sim;
        public TitledSim(SimPlayer sim) => _sim = sim;
        public TimeSpan Position => _sim.Position;
        public bool IsPlaying => _sim.IsPlaying;
        public TimeSpan Duration => _sim.Duration;
        public void Seek(TimeSpan position) => _sim.Seek(position);
        public void Play() => _sim.Play();
        public void Pause() => _sim.Pause();
        public string? TrackPath => "C:/music/northern-lights.flac";
        public string TrackTitle => "Northern Lights";
        public string TrackArtist => "Ada Vale";
        public event EventHandler? TrackChanged { add { } remove { } }
        public TimeSpan GapSetting => TimeSpan.Zero;
        public bool KeepLoops => false;
        public TimeSpan NudgeSetting => Ms(100);
        public event EventHandler? SettingsChanged { add { } remove { } }
        public string? DataDirectory => null;
        public void Log(string message) { }
    }

    // ── Through the real loader ──

    [AvaloniaFact]
    public void ReleaseZip_InstallsFromFile_AndRuns()
    {
        var zip = PluginSandbox.RepoFile("plugins/Noctis.Plugins.ABLoop/bin/Release/Noctis.Plugins.ABLoop-1.0.0.zip");
        if (!File.Exists(zip)) Assert.Skip("build it first: dotnet build plugins/Noctis.Plugins.ABLoop -c Release");

        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "Noctis.Plugins.ABLoop.deps.json", "Noctis.Plugins.ABLoop.dll", "icon.png", "plugin.json" }, names);
            Assert.Equal((256, 256), AlbumRoulettePluginTests.PngSize(archive.GetEntry("icon.png")!));
        }

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        host.LoadAll();
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var plugin = result.Plugin!;
        Assert.Equal(("A-B Loop", "1.0.0", "Noctis"), (plugin.Name, plugin.Version, plugin.Manifest?.Author));
        Assert.Empty(plugin.Manifest!.Warnings);
        Assert.Equal(new[] { "playback.control", "menu.commands" }, plugin.Manifest.Permissions);
        Assert.Equal(new[] { "gap", "keepLoops", "nudge" }, plugin.Manifest.Settings.Select(s => s.Key));

        host.SetEnabled(plugin, true);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("A-B Loop", Assert.Single(plugin.Views).Name);
        Assert.Contains(host.TrackCommands, c => c.Label == ABLoopPlugin.LoopAPartLabel);
        Assert.True(host.Remove(plugin, deleteData: true));
    }

    [Fact]
    public void Manifest_IsOfficial_AndMatchesTheCode()
    {
        var manifest = PluginManifest.TryLoad(PluginSandbox.RepoFile("plugins/Noctis.Plugins.ABLoop"))!;
        Assert.Equal((ABLoopPlugin.Id, ABLoopPlugin.Version, "Noctis"), (manifest.Id, manifest.Version, manifest.Author));
        Assert.Equal(ABLoopPlugin.About, manifest.Description);
        Assert.DoesNotContain('\u2014', File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.ABLoop/plugin.json")));
        var gap = manifest.Settings.Single(s => s.Key == "gap");
        Assert.All(gap.Choices, c => Assert.Contains(PluginLoopHost.ParseSeconds(c, 2).TotalSeconds, LoopSession.Gaps));
        Assert.True(File.Exists(PluginSandbox.RepoFile("plugins/Noctis.Plugins.ABLoop/icon.png")));
    }
}
