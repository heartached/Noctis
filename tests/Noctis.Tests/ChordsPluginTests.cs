using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Plugins.Chords;
using Noctis.Plugins.Chords.Analysis;
using Noctis.Plugins.Chords.Views;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Chords (official, plugin API 1.6): the session (find, cache per file, cancel on a new song or on
/// Cancel, automatic finding only while the window is open, the Sevenths setting, no decoder, a
/// failed decode), the window in each state (the chord now and next, the key, "Estimate", click to
/// seek), and the plugin through the real host and its Release package.
/// </summary>
public class ChordsPluginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-chords-" + Guid.NewGuid().ToString("N")[..8]);

    public ChordsPluginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Song(string name, int bytes = 64)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static readonly List<(Chord Chord, double Seconds)> Pop = ChordSynth.Progression("C G Am F", 2, times: 2);

    /// <summary>A scripted Noctis: decoding returns synthesized chords (or waits, or fails); posted work waits for <see cref="Drain"/>.</summary>
    internal sealed class FakeHost : IChordsHost
    {
        private readonly Queue<Action> _posted = new();

        public string? TrackPath { get; set; }
        public string TrackTitle { get; set; } = "Northern Lights";
        public string TrackArtist { get; set; } = "Ada Vale";
        public TimeSpan Position { get; set; }
        public bool IsPlaying { get; set; }
        public event EventHandler? TrackChanged;
        public bool Sevenths { get; set; }
        public bool AutoFind { get; set; }
        public event EventHandler? SettingsChanged;
        public bool CanDecode { get; set; } = true;
        public string? DataDirectory { get; set; }
        public List<string> Decoded { get; } = new();
        public List<TimeSpan> Seeks { get; } = new();
        public Func<string, CancellationToken, Task<DecodedAudio>>? Decode;
        public List<(Chord, double)> Progression { get; set; } = Pop;

        public Task<DecodedAudio> DecodeAsync(string path, int sampleRate, TimeSpan maxDuration, CancellationToken ct)
        {
            lock (Decoded) Decoded.Add(path);
            return Decode is { } d ? d(path, ct) : Task.FromResult(new DecodedAudio(ChordSynth.Render(Progression), sampleRate));
        }

        public void Seek(TimeSpan position) => Seeks.Add(position);
        public void Log(string message) { }
        public void Post(Action action) { lock (_posted) _posted.Enqueue(action); }

        public void Drain()
        {
            while (true)
            {
                Action next;
                lock (_posted)
                {
                    if (_posted.Count == 0) return;
                    next = _posted.Dequeue();
                }
                next();
            }
        }

        public void Play(string? path)
        {
            TrackPath = path;
            TrackChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ChangeSettings() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task Finish(ChordsSession session, FakeHost host)
    {
        if (session.Running is { } t)
        {
            try { await t.WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { }
        }
        host.Drain();
    }

    [Fact]
    public async Task Find_AnalysesTheSong_CachesIt_AndTheNextTimeItIsThereAtOnce()
    {
        var host = new FakeHost { DataDirectory = Path.Combine(_dir, "data"), TrackPath = Song("pop.flac") };
        using (var session = new ChordsSession(host))
        {
            Assert.Equal(ChordsState.Idle, session.State);
            session.Find();
            Assert.Equal(ChordsState.Finding, session.State);
            await Finish(session, host);
            Assert.Equal(ChordsState.Ready, session.State);
            Assert.Equal(new[] { "C", "G", "Am", "F", "C", "G", "Am", "F" }, session.Analysis!.Segments.Select(s => s.Chord.Name()));
            Assert.Equal("C major", session.Analysis.Key?.Name);
            Assert.Single(Directory.GetFiles(Path.Combine(host.DataDirectory, "cache"), "*.json"));
        }

        using var again = new ChordsSession(host);
        Assert.Equal(ChordsState.Ready, again.State);
        Assert.Single(host.Decoded);
        Assert.Equal(new[] { "C", "G", "Am", "F", "C", "G", "Am", "F" }, again.Analysis!.Segments.Select(s => s.Chord.Name()));
        Assert.Equal("C major", again.Analysis.Key?.Name);
    }

    [Fact]
    public async Task ChangedFile_OrTheOtherSeventhsSetting_IsAnalysedAgain()
    {
        var path = Song("pop.flac");
        var host = new FakeHost { DataDirectory = Path.Combine(_dir, "data"), TrackPath = path };
        using var session = new ChordsSession(host);
        session.Find();
        await Finish(session, host);

        // Sevenths on: not the same result, so not taken from the cache.
        host.Sevenths = true;
        host.ChangeSettings();
        Assert.Equal(ChordsState.Idle, session.State);
        session.Find();
        await Finish(session, host);
        Assert.True(session.Analysis!.Sevenths);
        Assert.Equal(2, host.Decoded.Count);

        // The file was re-encoded (other size): analysed again.
        File.WriteAllBytes(path, new byte[128]);
        host.Sevenths = false;
        host.ChangeSettings();
        Assert.Equal(ChordsState.Idle, session.State);
    }

    [Fact]
    public async Task NewSong_CancelsTheRunningAnalysis_AndItsResultNeverShows()
    {
        var host = new FakeHost { DataDirectory = Path.Combine(_dir, "data"), TrackPath = Song("one.flac") };
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        host.Decode = async (_, ct) =>
        {
            seen = ct;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("not reached");
        };
        using var session = new ChordsSession(host);
        session.Find();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Decode = null;
        host.Play(Song("two.flac"));
        Assert.True(seen.IsCancellationRequested);
        await Finish(session, host);
        Assert.Equal(ChordsState.Idle, session.State);
        Assert.Null(session.Analysis);
        Assert.EndsWith("two.flac", session.TrackPath);
    }

    [Fact]
    public async Task Cancel_StopsIt()
    {
        var host = new FakeHost { DataDirectory = null, TrackPath = Song("one.flac") };
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        host.Decode = async (_, ct) =>
        {
            seen = ct;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("not reached");
        };
        using var session = new ChordsSession(host);
        session.Find();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Cancel();
        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(ChordsState.Idle, session.State);
        await Finish(session, host);
        Assert.Equal(ChordsState.Idle, session.State);
    }

    [Fact]
    public async Task AutoFind_OnlyWhileAWindowIsOpen()
    {
        var host = new FakeHost { DataDirectory = null, AutoFind = true, TrackPath = Song("one.flac") };
        using var session = new ChordsSession(host);
        Assert.Equal(ChordsState.Idle, session.State); // no window: nothing runs
        host.Play(Song("two.flac"));
        Assert.Equal(ChordsState.Idle, session.State);
        Assert.Empty(host.Decoded);

        session.ViewOpened();
        Assert.Equal(ChordsState.Finding, session.State);
        await Finish(session, host);
        Assert.Equal(ChordsState.Ready, session.State);

        host.Play(Song("three.flac"));
        Assert.Equal(ChordsState.Finding, session.State); // window open: the new song goes at once
        await Finish(session, host);
        session.ViewClosed();
        host.Play(Song("four.flac"));
        Assert.Equal(ChordsState.Idle, session.State);
        Assert.Equal(2, host.Decoded.Count);

        host.AutoFind = false;
        session.ViewOpened();
        Assert.Equal(ChordsState.Idle, session.State); // off: waits for the button
    }

    [Fact]
    public void NoSong_NoDecoder_AndAFailedDecode_SayWhy()
    {
        var host = new FakeHost { DataDirectory = null };
        using var session = new ChordsSession(host);
        Assert.Equal(ChordsState.NoSong, session.State);
        session.Find();
        Assert.Equal(ChordsState.NoSong, session.State);

        host.CanDecode = false;
        host.Play(Song("one.flac"));
        session.Find();
        Assert.Equal(ChordsState.NoDecoder, session.State);
        Assert.Empty(host.Decoded);
    }

    [Fact]
    public async Task FailedDecode_IsFailed_WithTheReason()
    {
        var host = new FakeHost { DataDirectory = null, TrackPath = Song("one.flac") };
        host.Decode = (_, _) => Task.FromException<DecodedAudio>(new InvalidOperationException("Noctis could not decode this file."));
        using var session = new ChordsSession(host);
        session.Find();
        await Finish(session, host);
        Assert.Equal(ChordsState.Failed, session.State);
        Assert.Contains("could not decode", session.Error);
    }

    [Fact]
    public void Cache_IgnoresBrokenFiles()
    {
        var song = Song("x.flac");
        var cache = new ChordCache(Path.Combine(_dir, "data"));
        var analysis = ChordAnalyzer.Analyze(ChordSynth.Render(Pop), ChordAnalyzer.SampleRate);
        cache.Save(song, analysis);
        var loaded = cache.TryLoad(song, sevenths: false)!;
        Assert.Equal(analysis.Segments, loaded.Segments);
        Assert.Equal(analysis.Key, loaded.Key);
        Assert.Null(cache.TryLoad(song, sevenths: true));
        File.WriteAllText(cache.FileFor(song, false)!, "{ not json");
        Assert.Null(cache.TryLoad(song, sevenths: false));
        File.WriteAllText(cache.FileFor(song, false)!, "{\"Engine\":1,\"Duration\":5,\"Times\":[[0,5,0.5]],\"Chords\":[\"H#\"]}");
        Assert.Null(cache.TryLoad(song, sevenths: false));
    }

    // ── The window ──

    private static ChordsView Shown(ChordsSession session)
    {
        var view = new ChordsView(session);
        var window = new Window { Content = view, Width = ChordsView.WindowWidth, Height = ChordsView.WindowHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    [AvaloniaFact]
    public async Task View_ShowsTheChordNow_TheNextOnes_AndTheKey_AsAnEstimate()
    {
        var host = new FakeHost { DataDirectory = null, TrackPath = Song("pop.flac"), Position = TimeSpan.FromSeconds(4.5) };
        using var session = new ChordsSession(host);
        var view = Shown(session);
        Assert.False(view.ChordsShown);
        Assert.Equal("Find chords", view.ActionText);
        Assert.Contains("listens to the file", view.MessageText);
        Assert.Equal("Estimate", view.BadgeText);

        view.ClickAction();
        Assert.Equal("Cancel", view.ActionText);
        Assert.True(view.ProgressShown);
        await Finish(session, host);
        view.UpdatePosition();
        Assert.True(view.ChordsShown);
        Assert.Equal("Am", view.NowText);
        Assert.Equal(new[] { "F", "C", "G" }, view.UpcomingTexts);
        Assert.Equal("Key: C major (estimated)", view.KeyText);

        host.Position = TimeSpan.FromSeconds(6.5);
        view.UpdatePosition();
        Assert.Equal("F", view.NowText);
        Assert.Equal(new[] { "C", "G", "Am" }, view.UpcomingTexts);
    }

    [AvaloniaFact]
    public async Task View_ClickingAChordInTheLane_SeeksToItsStart()
    {
        var host = new FakeHost { DataDirectory = null, TrackPath = Song("pop.flac"), Position = TimeSpan.FromSeconds(1) };
        using var session = new ChordsSession(host);
        var view = Shown(session);
        session.Find();
        await Finish(session, host);
        view.UpdatePosition();
        var lane = view.Lane;
        lane.UpdateLayout();
        // The G (2..4 s) sits right of the playhead (at 1 s): find its x and click it.
        var x = Enumerable.Range(0, (int)lane.Bounds.Width).First(px => lane.TimeAt(px) > 3);
        var at = Avalonia.VisualExtensions.TranslatePoint(lane, new Avalonia.Point(x, lane.Bounds.Height / 2), (Window)TopLevel.GetTopLevel(lane)!)!.Value;
        var window = (Window)TopLevel.GetTopLevel(lane)!;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, at, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, at, Avalonia.Input.MouseButton.Left);
        var seek = Assert.Single(host.Seeks);
        Assert.InRange(seek.TotalSeconds, 1.8, 2.2);
    }

    [AvaloniaFact]
    public void View_NoSong_AndNoDecoder()
    {
        var host = new FakeHost { DataDirectory = null };
        using var session = new ChordsSession(host);
        var view = Shown(session);
        Assert.Contains("Play a song", view.MessageText);
        Assert.False(view.ActionShown);
        host.CanDecode = false;
        host.Play(Song("x.flac"));
        view.ClickAction();
        Assert.Contains("ffmpeg", view.MessageText);
        Assert.Equal("Try again", view.ActionText);
    }

    [AvaloniaFact]
    public void Preview_TheWindowAt125Percent()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var dir = AlbumRoulettePluginTests.PreviewDir();
        var host = new FakeHost { DataDirectory = null, TrackPath = Song("pop.flac"), Position = TimeSpan.FromSeconds(5.1),
            Progression = ChordSynth.Progression("Am F C G", 2.5, times: 4) };
        using var session = new ChordsSession(host);
        session.Find();
        session.Running!.Wait(TimeSpan.FromSeconds(30));
        host.Drain();
        var view = new ChordsView(session);
        var window = new Window { Content = view, Width = ChordsView.WindowWidth, Height = ChordsView.WindowHeight, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        window.Show();
        view.Refresh();
        AlbumRoulettePluginTests.Render(view, Path.Combine(dir, "chords-preview.png"), 1.25);
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        view.Refresh();
        AlbumRoulettePluginTests.Render(view, Path.Combine(dir, "chords-preview-light.png"), 1.25);
        using var idle = new ChordsSession(new FakeHost { DataDirectory = null, TrackPath = Song("idle.flac") });
        var idleView = new ChordsView(idle);
        window.Content = idleView;
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        AlbumRoulettePluginTests.Render(idleView, Path.Combine(dir, "chords-preview-idle.png"), 1.25);
        window.Close();
    }

    // ── Through the real host ──

    [AvaloniaFact]
    public void Plugin_InTheRealHost_RegistersItsWindowAndMenuEntry()
    {
        using var box = new PluginSandbox();
        var permissions = new[] { "audio.decode", "playback.control", "menu.commands" };
        box.Approve((ChordsPlugin.Id, permissions));
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        var json = File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Chords/plugin.json"));
        var manifest = PluginManifest.Parse(json);
        var dir = Path.Combine(box.PluginsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        var chords = new ChordsPlugin();
        var plugin = host.AddInProcess(dir, manifest, () => chords);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("Chords", Assert.Single(plugin.Views).Name);
        Assert.Contains(host.TrackCommands, c => c.Label == ChordsPlugin.ShowChordsLabel);
        Assert.NotNull(chords.Session);
        Assert.Equal(ChordsState.NoSong, chords.Session!.State); // no player in this host
        host.SetEnabled(plugin, false);
        Assert.Null(chords.Session);
    }

    [AvaloniaFact]
    public void ReleaseZip_InstallsFromFile_AndRuns()
    {
        var zip = PluginSandbox.RepoFile("plugins/Noctis.Plugins.Chords/bin/Release/Noctis.Plugins.Chords-1.0.0.zip");
        if (!File.Exists(zip)) Assert.Skip("build it first: dotnet build plugins/Noctis.Plugins.Chords -c Release");

        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "Noctis.Plugins.Chords.deps.json", "Noctis.Plugins.Chords.dll", "icon.png", "plugin.json" }, names);
            Assert.Equal((256, 256), AlbumRoulettePluginTests.PngSize(archive.GetEntry("icon.png")!));
        }

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        host.LoadAll();
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var plugin = result.Plugin!;
        Assert.Equal(("Chords", "1.0.0", "Noctis"), (plugin.Name, plugin.Version, plugin.Manifest?.Author));
        Assert.Empty(plugin.Manifest!.Warnings);
        Assert.Equal(new[] { "audio.decode", "playback.control", "menu.commands" }, plugin.Manifest.Permissions);
        Assert.Equal(new[] { "autoFind", "sevenths" }, plugin.Manifest.Settings.Select(s => s.Key));

        host.SetEnabled(plugin, true);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("Chords", Assert.Single(plugin.Views).Name);
        Assert.Contains(host.TrackCommands, c => c.Label == ChordsPlugin.ShowChordsLabel);
        Assert.True(host.Remove(plugin, deleteData: true));
    }

    [Fact]
    public void Manifest_IsOfficial_AndMatchesTheCode()
    {
        var manifest = PluginManifest.TryLoad(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Chords"))!;
        Assert.Equal((ChordsPlugin.Id, ChordsPlugin.Version, "Noctis"), (manifest.Id, manifest.Version, manifest.Author));
        Assert.Equal(ChordsPlugin.About, manifest.Description);
        Assert.Equal("1.6", manifest.ApiVersion);
        Assert.Null(manifest.CheckCompatibility("1.6.6"));
        Assert.DoesNotContain('—', File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Chords/plugin.json")));
        Assert.True(File.Exists(PluginSandbox.RepoFile("plugins/Noctis.Plugins.Chords/icon.png")));
    }
}
