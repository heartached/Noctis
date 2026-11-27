using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Plugins.ABLoop.Looping;
using Noctis.Plugins.ABLoop.Views;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Plugins.AlbumRoulette.Views;
using Noctis.Plugins.Chords;
using Noctis.Plugins.Chords.Views;
using Noctis.Helpers;
using Noctis.Plugins.TrueShuffle;
using Noctis.Services.Plugins;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Review pictures of each official plugin's own window as Noctis shows it (the app's styles and
/// the plugin-window theme), at its opening size and at its minimum size, dark and light, with long
/// titles. Explicit only: runs when NOCTIS_TEST_SKIA=1 and NOCTIS_PLUGIN_SHOTS_DIR names the folder
/// (plugin-&lt;name&gt;-&lt;size&gt;-&lt;theme&gt;.png).
/// </summary>
public class PluginViewsEachShotsTests : IDisposable
{
    private const string LongTitle = "The Extraordinarily Long Title of a Song That Never Seems to End (Remastered Deluxe Edition)";
    private const string LongArtist = "Juniper Hollow and the Midnight Orchestra of Somewhere Far Away";

    private readonly PluginSandbox _box = new();
    public void Dispose() => _box.Dispose();

    private static string? Dir()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_PLUGIN_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Renders <paramref name="view"/> in a themed plugin window at each size and theme.</summary>
    private static void Shoot(string dir, string name, Func<Control> make, params (string Label, double W, double H)[] sizes)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            foreach (var (label, w, h) in sizes)
            {
                var view = make();
                var window = new Window { Width = w, Height = h, Content = view, RequestedThemeVariant = theme };
                PluginViewTheme.Apply(window);
                window.Show();
                for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
                AlbumRoulettePluginTests.Render(window, Path.Combine(dir, $"plugin-{name}-{label}-{(theme == ThemeVariant.Dark ? "dark" : "light")}.png"), 1.25);
                window.Close();
            }
        }
    }

    private sealed class StillLoopHost : ILoopHost
    {
        public TimeSpan Position { get; set; } = TimeSpan.FromSeconds(52.4);
        public bool IsPlaying => false;
        public TimeSpan Duration => TimeSpan.FromSeconds(227);
        public void Seek(TimeSpan position) => Position = position;
        public void Play() { }
        public void Pause() { }
        public string? TrackPath { get; set; } = "C:/Music/song.flac";
        public string TrackTitle { get; set; } = LongTitle;
        public string TrackArtist { get; set; } = LongArtist;
        public event EventHandler? TrackChanged { add { } remove { } }
        public TimeSpan GapSetting => TimeSpan.FromSeconds(1.5);
        public bool KeepLoops => false;
        public TimeSpan NudgeSetting => TimeSpan.FromMilliseconds(100);
        public event EventHandler? SettingsChanged { add { } remove { } }
        public string? DataDirectory => null;
        public void Log(string message) { }
    }

    [AvaloniaFact]
    public void ABLoop_Shots()
    {
        if (Dir() is not { } dir) return;
        Shoot(dir, "abloop", () =>
        {
            var session = new LoopSession(new StillLoopHost(), useTimer: false);
            session.Engine.SetPart(TimeSpan.FromSeconds(41.3), TimeSpan.FromSeconds(63.8));
            session.SaveCurrent("A rather long loop name for the second chorus and bridge");
            session.SaveCurrent("Solo");
            session.Engine.Count = 10;
            session.CycleGap();
            return new ABLoopView(session);
        }, ("normal", ABLoopView.WindowWidth, ABLoopView.WindowHeight), ("narrow", ABLoopView.MinWindowWidth, ABLoopView.MinWindowHeight));
    }

    [AvaloniaFact]
    public void Chords_Shots()
    {
        if (Dir() is not { } dir) return;
        Directory.CreateDirectory(_box.Root);
        var song = Path.Combine(_box.Root, "song.flac");
        File.WriteAllBytes(song, new byte[64]);
        var host = new ChordsPluginTests.FakeHost
        {
            DataDirectory = null, TrackPath = song, Position = TimeSpan.FromSeconds(5.1), TrackTitle = LongTitle, TrackArtist = LongArtist,
            Progression = ChordSynth.Progression("Am F C G", 2.5, times: 4),
        };
        var session = new ChordsSession(host);
        session.Find();
        session.Running!.Wait(TimeSpan.FromSeconds(30));
        host.Drain();
        Shoot(dir, "chords", () => new ChordsView(session),
            ("normal", ChordsView.WindowWidth, ChordsView.WindowHeight), ("narrow", ChordsView.MinWindowWidth, ChordsView.MinWindowHeight));
        var idle = new ChordsSession(new ChordsPluginTests.FakeHost { DataDirectory = null, TrackPath = song, TrackTitle = LongTitle, TrackArtist = LongArtist });
        Shoot(dir, "chords-idle", () => new ChordsView(idle), ("narrow", ChordsView.MinWindowWidth, ChordsView.MinWindowHeight));
    }

    [AvaloniaFact]
    public void AlbumRoulette_Shots()
    {
        if (Dir() is not { } dir) return;
        var covers = AlbumRoulettePluginTests.PreviewCovers(Path.Combine(dir, "covers"));
        var tracks = new List<TrackInfo>();
        for (var i = 0; i < 12; i++)
            tracks.Add(AlbumRoulettePluginTests.T($"t{i}", i == 0 ? LongTitle : "Album " + i, i == 0 ? LongArtist : "Artist " + i, 1990 + i,
                albumId: Guid.NewGuid().ToString(), art: covers[i % covers.Count]));
        Shoot(dir, "albumroulette", () =>
        {
            var view = new RouletteView(new AlbumRoulettePluginTests.FakeRouletteHost(), new Random(4), () => DateTimeOffset.Now);
            view.Reel.ManualFrames = true;
            var albums = AlbumCatalog.Group(tracks);
            view.SetAlbums(albums);
            foreach (var t in tracks) view.Covers.GetNow(t.ArtworkPath);
            view.ShowOnStage(albums.First(a => a.Title == LongTitle));
            return view;
        }, ("normal", RouletteView.WindowWidth, RouletteView.WindowHeight), ("narrow", RouletteView.MinWindowWidth, RouletteView.MinWindowHeight));
    }

    [AvaloniaFact]
    public void TrueShuffle_Shots()
    {
        if (Dir() is not { } dir) return;
        _box.Approve(("dev.noctis.plugins.trueshuffle", new[] { "playback.control", "library.read", "menu.commands", "notifications" }));
        var host = _box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        var manifest = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/plugin.json")));
        var pluginDir = Path.Combine(_box.PluginsDir, manifest.Id);
        Directory.CreateDirectory(pluginDir);
        var plugin = new TrueShufflePlugin();
        host.AddInProcess(pluginDir, manifest, () => plugin);
        // Default minimum window (no MinWindowSize): 480 x 360.
        Shoot(dir, "trueshuffle", () => plugin.CreateView(), ("normal", 600, 440), ("narrow", 480, 360));
    }

    [AvaloniaFact]
    public void YouTubeImport_Shots()
    {
        if (Dir() is not { } dir) return;
        var n = 0;
        Shoot(dir, "youtubeimport", () =>
        {
            var root = Path.Combine(_box.Root, "yt" + n++);
            var state = new Noctis.Plugins.YouTubeImport.ImportState(Path.Combine(root, "data"));
            state.AddRecent(new Noctis.Plugins.YouTubeImport.RecentDownload(LongTitle, LongArtist, "C:/Music/YouTube/a.m4a", DateTimeOffset.Now.AddMinutes(-5), true));
            state.AddRecent(new Noctis.Plugins.YouTubeImport.RecentDownload("Short one", "Ada Vale", "C:/Music/YouTube/b.m4a", DateTimeOffset.Now.AddHours(-2), false));
            var view = YouTubeImportPluginTests.NewView(root);
            var vm = view.ViewModel;
            foreach (var (id, title) in new[] { ("a1", LongTitle), ("b2", "Midnight Arcade - Paper Moons (Official Audio)") })
                vm.Results.Add(new Noctis.Plugins.YouTubeImport.YouTubeImportViewModel.ResultRow(
                    new Noctis.Plugins.YouTubeImport.YouTubeTrackInfo(id, "https://www.youtube.com/watch?v=" + id, title, LongArtist, TimeSpan.FromSeconds(214), null, null, null, null, null, null), vm));
            return view;
        }, ("normal", 840, 680), ("narrow", 560, 600));
    }
}
