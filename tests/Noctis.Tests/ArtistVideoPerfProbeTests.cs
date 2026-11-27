using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26 "the app feels slow and choppy" with Settings › Artwork › Artist videos on: the
/// numbers behind the fix, printed to the test output. Off by default.
/// <list type="bullet">
/// <item>NOCTIS_AV_CLIPS=dir (H.264 clips art_1920x1080.mp4, art_1280x720.mp4, art_960x540.mp4,
/// e.g. ffmpeg testsrc2 at 24 fps): the real LibVLC path, process CPU and frames shown per clip size.</item>
/// <item>NOCTIS_TEST_SKIA=1 and NOCTIS_AV_PROBE=1: real Skia render time of the artist page per
/// video frame (a 24 fps fake frame source through the same presentation step), and the Settings
/// sheet's stand-in retakes a playing hero causes.</item>
/// </list>
/// </summary>
public class ArtistVideoPerfProbeTests
{
    private readonly ITestOutputHelper _out;
    public ArtistVideoPerfProbeTests(ITestOutputHelper output) => _out = output;

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
    }

    [AvaloniaTheory]
    [InlineData("art_1920x1080.mp4")]
    [InlineData("art_1280x720.mp4")]
    [InlineData("art_960x540.mp4")]
    public void DecodeCost_PerClipSize(string file)
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_AV_CLIPS") is not { Length: > 0 } dir) return;
        var path = System.IO.Path.Combine(dir, file);
        Assert.True(File.Exists(path), path);
        var video = new VideoBackdrop { LogName = "Probe" };
        var win = new Window { Width = 1280, Height = 400, Content = video };
        win.Show();
        var proc = Process.GetCurrentProcess();

        // Idle baseline of this pump loop.
        Pump(300);
        proc.Refresh();
        var c0 = proc.TotalProcessorTime;
        Pump(2000);
        proc.Refresh();
        var idleCpu = (proc.TotalProcessorTime - c0).TotalMilliseconds / 2000;

        Noctis.Services.DebugLogger.IsEnabled = true;
        video.Source = path;
        Pump(4000); // LibVLC start-up, probe, first frames
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        var f0 = video.FramesPresented;
        var sw = Stopwatch.StartNew();
        Pump(5000);
        proc.Refresh();
        var seconds = sw.Elapsed.TotalSeconds;
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / (seconds * 1000);
        var fps = (video.FramesPresented - f0) / seconds;
        var live = VideoBackdrop.LiveSessionCount;
        _out.WriteLine($"{file}: shown {fps:0.0} fps, process CPU {cpu * 100:0}% of one core (pump idle {idleCpu * 100:0}%), live players {live}");

        // Paused (window in the background, Settings over the page, hero scrolled away).
        video.IsPaused = true;
        Pump(500);
        proc.Refresh();
        cpu0 = proc.TotalProcessorTime;
        f0 = video.FramesPresented;
        sw.Restart();
        Pump(3000);
        proc.Refresh();
        seconds = sw.Elapsed.TotalSeconds;
        _out.WriteLine($"  paused: shown {(video.FramesPresented - f0) / seconds:0.0} fps, process CPU {(proc.TotalProcessorTime - cpu0).TotalMilliseconds / (seconds * 1000) * 100:0}% of one core");
        win.Close();
        Pump(500);
        _out.WriteLine($"after close: live players {VideoBackdrop.LiveSessionCount}, active {VideoBackdrop.ActiveCount}");
        foreach (var e in Noctis.Services.DebugLogger.GetEntries().Where(e => e.Action.StartsWith("Probe.")))
            _out.WriteLine($"  log: {e.Action} | {e.Metadata}");
        Noctis.Services.DebugLogger.Clear();
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    internal static WriteableBitmap[] Frames(int w = 960, int h = 540)
    {
        var frames = new WriteableBitmap[3];
        var row = new byte[w * 4];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var fb = frames[i].Lock();
            for (var x = 0; x < w; x++) { row[x * 4] = (byte)(x + i * 60); row[x * 4 + 1] = (byte)(i * 80); row[x * 4 + 2] = 120; row[x * 4 + 3] = 255; }
            for (var y = 0; y < h; y++) Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return frames;
    }

    /// <summary>Median render time per new frame (a busy machine skews the mean).</summary>
    private static double MsPerFrame(Window win, VideoBackdrop? hero, WriteableBitmap[] frames, int count = 72)
    {
        // Warm up, then time the render each new frame causes.
        for (var i = 0; i < 6; i++) { hero?.PresentForTests(frames[i % 3]); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        var times = new List<double>();
        for (var i = 0; i < count; i++)
        {
            hero?.PresentForTests(frames[i % 3]);
            var sw = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    [AvaloniaFact]
    public void HeroFrame_RenderCost()
    {
        if (!HeadlessTestApp.RealRendering || Environment.GetEnvironmentVariable("NOCTIS_AV_PROBE") != "1") return;
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = new Album { Id = Guid.NewGuid(), Name = "Phases", Artist = "Chase Atlantic", Year = 2019, Tracks = new List<Track>() };
        for (var i = 1; i <= 10; i++)
            album.Tracks.Add(new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Chase Atlantic", AlbumArtist = "Chase Atlantic", Album = "Phases", AlbumId = album.Id, TrackNumber = i, Duration = TimeSpan.FromMinutes(3) });
        album.TrackCount = 10;
        ((List<Album>)lib.Albums).Add(album);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Pump(200);
        var hero = view.FindControl<VideoBackdrop>("HeroVideo")!;
        var stills = view.FindControl<Panel>("HeroStills")!;
        vm.ArtistVideoPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missing-artist-video.mp4"); // shown, never decoded
        Pump(100);
        var frames = Frames();

        _out.WriteLine($"static page (no new frame): {MsPerFrame(win, null, frames):0.00} ms/tick");
        _out.WriteLine($"new video frame, stills at Opacity 0: {MsPerFrame(win, hero, frames):0.00} ms/frame");
        Pump(400); // cross-fade done
        _out.WriteLine($"new video frame after the fade: {MsPerFrame(win, hero, frames):0.00} ms/frame (stills opacity {stills.Opacity})");
        stills.IsVisible = false;
        _out.WriteLine($"  stills IsVisible=false: {MsPerFrame(win, hero, frames):0.00} ms/frame");
        stills.IsVisible = true;
        // Before the fix: the fade was a page-coloured Border under a gradient OpacityMask.
        var fade = view.FindControl<Border>("HeroPageFade")!;
        var gradient = fade.Background;
        fade.Background = new SolidColorBrush(Color.Parse("#252525"));
        fade.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#00000000"), 0.4), new GradientStop(Color.Parse("#B0000000"), 0.82), new GradientStop(Color.Parse("#FF000000"), 1) },
        };
        _out.WriteLine($"  BEFORE (fade = OpacityMask layer): {MsPerFrame(win, hero, frames):0.00} ms/frame");
        fade.OpacityMask = null;
        fade.Background = gradient;
        _out.WriteLine($"  AFTER (fade = gradient brush): {MsPerFrame(win, hero, frames):0.00} ms/frame");
        foreach (var mode in new[] { BitmapInterpolationMode.LowQuality, BitmapInterpolationMode.MediumQuality, BitmapInterpolationMode.HighQuality })
        {
            RenderOptions.SetBitmapInterpolationMode(hero, mode);
            _out.WriteLine($"  after, video {mode}: {MsPerFrame(win, hero, frames):0.00} ms/frame");
        }
        RenderOptions.SetBitmapInterpolationMode(hero, BitmapInterpolationMode.Unspecified);
        var heroGrid = view.FindControl<Grid>("Hero")!;
        var others = heroGrid.Children.Where(c => c is not VideoBackdrop).ToList();
        foreach (var c in others) c.IsVisible = false;
        _out.WriteLine($"  video alone in the hero: {MsPerFrame(win, hero, frames):0.00} ms/frame");
        foreach (var c in others) c.IsVisible = true;
        hero.IsVisible = false;
        _out.WriteLine($"  video hidden (frames still pushed): {MsPerFrame(win, hero, frames):0.00} ms/frame");
        hero.IsVisible = true;
        win.Close();
    }

    [AvaloniaFact]
    public void SettingsSheet_OverAPlayingHero_RetakesTheStandIn()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_AV_PROBE") != "1") return;
        var s = BlurredBackdropStandInTests.Build();
        var video = new VideoBackdrop { Height = 400, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        s.Layer.Children.Add(video);
        var frames = Frames();
        try
        {
            BlurredBackdropStandInTests.Open(s.Scrim);
            var sw0 = Stopwatch.StartNew();
            while (!s.Scrim.IsStandingIn && sw0.ElapsedMilliseconds < 1000) Pump(10);
            Assert.True(s.Scrim.IsStandingIn);
            var before = s.Scrim.RetakeCount;
            var sw = Stopwatch.StartNew();
            var busy = 0.0;
            var n = 0;
            while (sw.ElapsedMilliseconds < 2000)
            {
                video.PresentForTests(frames[n++ % 3]);
                var t = Stopwatch.StartNew();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                busy += t.Elapsed.TotalMilliseconds;
                Thread.Sleep(Math.Max(0, 42 - (int)t.ElapsedMilliseconds)); // ~24 fps
            }
            _out.WriteLine($"24 fps hero under the Settings sheet: {(s.Scrim.RetakeCount - before) / 2.0:0.0} stand-in retakes/s, UI+render {busy / n:0.00} ms per video frame ({n} frames)");
        }
        finally { BlurredBackdropStandInTests.Close(s.Scrim); s.Win.Close(); }
    }
}
