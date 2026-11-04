using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings › Appearance › Now Playing Artwork: the cover, a spinning record or a CD.</summary>
public class MobileDiscArtworkTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _files = new();

    public MobileDiscArtworkTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _files) try { File.Delete(f); } catch { }
    }

    // ---- The spin --------------------------------------------------------------------------

    /// <summary>Play eases the record up to 33⅓, Pause eases it to a stop and it stays where it
    /// stopped, Play takes it up again from there; the speed never jumps between frames, a press
    /// mid-ramp included.</summary>
    [Fact]
    public void TheSpin_EasesUpAndDown_KeepsItsPlace_AndNeverJumps()
    {
        const double Frame = 1 / 60.0;
        var spin = new DiscSpin { MaxSpeed = DiscSpin.VinylSpeed, Playing = true };
        var maxJump = 0.0;
        void Run(double seconds)
        {
            for (var t = 0.0; t < seconds; t += Frame)
            {
                var v = spin.Velocity;
                spin.Step(Frame);
                maxJump = Math.Max(maxJump, Math.Abs(spin.Velocity - v));
            }
        }

        spin.Step(Frame);
        Assert.InRange(spin.Velocity, 0, DiscSpin.VinylSpeed * 0.01);    // eases off the mark
        Run(DiscSpin.SpinUpSeconds);
        Assert.Equal(DiscSpin.VinylSpeed, spin.Velocity, 6);              // at 33⅓
        Assert.Equal(33.333, spin.Velocity * 60 / (2 * Math.PI), 2);

        spin.Playing = false;
        Run(DiscSpin.SpinDownSeconds + 0.05);
        Assert.Equal(0, spin.Velocity);
        Assert.False(spin.Moving);                                       // at rest: no frames wanted
        var rest = spin.Angle;
        Run(1);
        Assert.Equal(rest, spin.Angle);                                   // the place is kept

        spin.Playing = true;
        spin.Step(Frame);
        Assert.InRange(spin.Angle - rest, 0, 0.001);                      // from where it stopped, no jump

        // Pause halfway up, play halfway down: the speed turns around where it is.
        Run(DiscSpin.SpinUpSeconds / 2);
        spin.Playing = false;
        Run(0.3);
        spin.Playing = true;
        Run(DiscSpin.SpinUpSeconds);
        // A 60 Hz frame never changes the speed by more than the smoothstep's steepest slope allows.
        var steepest = DiscSpin.VinylSpeed * 1.5 / DiscSpin.SpinUpSeconds * Frame;
        Assert.True(maxJump <= steepest * 1.01, $"speed jumped {maxJump:0.0000} rad/s in a frame (max {steepest:0.0000})");
        Assert.InRange(spin.Angle, 0, 2 * Math.PI);
    }

    /// <summary>The per-frame step (the render thread's whole job besides two bitmap draws)
    /// allocates nothing.</summary>
    [Fact]
    public void TheSpin_StepAllocatesNothing()
    {
        var spin = new DiscSpin { MaxSpeed = DiscSpin.VinylSpeed, Playing = true };
        spin.Step(0.016);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            if (i % 5000 == 0) spin.Playing = !spin.Playing;
            spin.Step(1 / 120.0);
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"DiscSpin.Step x100000: {bytes} bytes");
        Assert.Equal(0, bytes);
    }

    // ---- The setting -----------------------------------------------------------------------

    [Fact]
    public void TheSavedName_ReadsBack_AndAnythingElseIsTheCover()
    {
        Assert.Equal(NowPlayingArtworkStyle.Vinyl, NowPlayingArtworkStyles.Parse("Vinyl"));
        Assert.Equal(NowPlayingArtworkStyle.CD, NowPlayingArtworkStyles.Parse(" cd "));
        foreach (var bad in new[] { null, "", "2", "Cassette", "Record" })
            Assert.Equal(NowPlayingArtworkStyle.Cover, NowPlayingArtworkStyles.Parse(bad));
        Assert.Equal("Cover", new Noctis.Models.AppSettings().MobileNowPlayingArtwork);   // the default
    }

    [Fact]
    public async Task TheStyle_IsSaved_AndReadBack()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileNowPlayingArtwork = "Vinyl";
            await p.SaveSettingsAsync(s);
        });
        Assert.Equal(NowPlayingArtworkStyle.Cover, rig.Shell.NowPlayingArtwork);   // before the read
        Assert.False(rig.Shell.ShowsDiscArtwork);
        await rig.Shell.InitializeAsync();
        Assert.Equal(NowPlayingArtworkStyle.Vinyl, rig.Shell.NowPlayingArtwork);
        Assert.True(rig.Shell.ShowsDiscArtwork);

        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.CD;
        await rig.Shell.PendingPlayerBarSave;
        Assert.Equal("CD", (await rig.Persistence.LoadSettingsAsync()).MobileNowPlayingArtwork);
        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.Cover;
        await rig.Shell.PendingPlayerBarSave;
        Assert.Equal("Cover", (await rig.Persistence.LoadSettingsAsync()).MobileNowPlayingArtwork);
    }

    /// <summary>The Appearance row opens Cover / Vinyl / CD on the glass menu, the one in use
    /// ticked; a pick applies at once and the row says it.</summary>
    [AvaloniaFact]
    public async Task TheSettingsRow_PicksTheStyle()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        await vm.Loaded;
        Assert.Equal("Cover", vm.NowPlayingArtworkValue);
        Assert.Equal("Now Playing Artwork, Cover", vm.NowPlayingArtworkRowName);

        vm.ShowNowPlayingArtworkMenuCommand.Execute(null);
        var menu = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        Assert.Equal(new[] { "Cover", "Vinyl", "CD" }, menu.Items.Select(i => i.Label));
        Assert.Equal(new[] { true, false, false }, menu.Items.Select(i => i.IsChecked));
        menu.Items[1].PickCommand.Execute(null);
        Assert.Null(rig.Shell.PopMenu);
        Assert.Equal(NowPlayingArtworkStyle.Vinyl, rig.Shell.NowPlayingArtwork);
        Assert.Equal("Vinyl", vm.NowPlayingArtworkValue);
        Assert.Equal("Now Playing Artwork, Vinyl", vm.NowPlayingArtworkRowName);
    }

    // ---- The page --------------------------------------------------------------------------

    /// <summary>Cover shows the square as before; Vinyl and CD hide it (kept in the tree: it
    /// decodes the artwork the disc prints and the wash blurs) and show the disc.</summary>
    [AvaloniaFact]
    public void ThePage_PicksTheStyle_Live()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var frame = MobileFixtures.Named<Border>(page, "ArtworkFrame");
        var stage = MobileFixtures.Named<Panel>(page, "DiscStage");
        var disc = MobileFixtures.Named<DiscArtwork>(page, "Disc");

        Assert.False(stage.IsVisible);
        Assert.False(frame.Classes.Contains("disc"));
        Assert.Equal(1, frame.Opacity);

        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.Vinyl;
        window.UpdateLayout();
        Assert.True(stage.IsEffectivelyVisible);
        Assert.True(frame.Classes.Contains("disc"));
        Assert.Equal(0, frame.Opacity);
        Assert.Equal(NowPlayingArtworkStyle.Vinyl, disc.Kind);
        Assert.True(disc.IsSpinning);
        Assert.Equal(new Size(296, 296), disc.Bounds.Size);               // the cover's place, its shadow inside it

        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.CD;
        Assert.Equal(NowPlayingArtworkStyle.CD, disc.Kind);
        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.False(disc.IsSpinning);
        Assert.True(stage.Classes.Contains("paused"));

        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.Cover;
        Assert.False(stage.IsVisible);
        Assert.Equal(1, frame.Opacity);
        window.Close();
    }

    /// <summary>The disc stops asking for frames when the page closes, the app goes to the
    /// background, or the style changes; and comes back on when all is well again.</summary>
    [AvaloniaFact]
    public void TheDisc_SleepsWhenHiddenOrSuspended()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.Vinyl;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var disc = MobileFixtures.Named<DiscArtwork>(page, "Disc");
        Assert.True(disc.Awake);

        rig.Shell.IsSuspended = true;                                     // screen off / home
        Assert.False(disc.IsActive);
        Assert.False(disc.Awake);
        rig.Shell.IsSuspended = false;
        Assert.True(disc.Awake);

        page.IsVisible = false;                                           // closed, or under the lyrics
        Assert.False(disc.Awake);
        page.IsVisible = true;
        Assert.True(disc.Awake);

        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.Cover;       // another style
        Assert.False(disc.Awake);
        window.Close();
    }

    // ---- Real rendering (NOCTIS_TEST_SKIA=1) -------------------------------------------------

    /// <summary>
    /// On the compositor: playing, the handler steps the angle every frame; paused, it eases to a
    /// stop and stops asking for frames, its angle kept; asleep (hidden), it stops at once. The
    /// disc is painted once (never per frame) at the drawn size in device pixels, and a spinning
    /// frame costs no more managed allocation than a still one (measured over 240 frames).
    /// </summary>
    [AvaloniaFact]
    public void OnTheCompositor_ItTurns_Stops_Sleeps_AndAllocatesNothingPerFrame()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var song = MobileFixtures.Song("Tone");
        var album = MobileFixtures.MakeAlbum("Record", "Artist", song);
        album.ArtworkPath = song.AlbumArtworkPath = WriteCover(512);
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.Vinyl;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var disc = MobileFixtures.Named<DiscArtwork>(page, "Disc");
        Settle(window, 60);
        var handler = disc.Handler!;
        Assert.NotNull(handler);
        Assert.Equal(disc.DevicePixels(), disc.PaintedPixels);           // crisp: painted at the drawn size
        Assert.True(disc.PaintedPixels > 300);
        var paints = disc.Paints;

        // Playing: up to speed and turning (the compositor's clock is the wall clock).
        RunFor(window, DiscSpin.SpinUpSeconds + 0.3);
        Assert.True(handler.Ticking);
        Assert.Equal(DiscSpin.VinylSpeed, handler.Spin.Velocity, 3);

        // Allocation per frame: the handler's own work (step + two bitmap draws), counted on
        // whichever thread runs it, and the whole process, spinning vs still.
        DiscSpinHandler.CountAllocations = true;
        long own;
        try
        {
            var start = handler.AllocatedBytes;
            Tick(window, 240);
            own = handler.AllocatedBytes - start;
        }
        finally
        {
            DiscSpinHandler.CountAllocations = false;
        }
        _out.WriteLine($"handler (frame update + draw): {own} B over 240 frames ({own / 240.0:0.0} B a frame)");
        // Ours allocate nothing; the Skia lease Avalonia hands the draw is two small objects a frame.
        Assert.True(own <= 240 * 96, $"the handler allocated {own / 240.0:0.0} B a frame");
        var spinning = Measure(window, 240, out var spunFrames);
        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);           // pause
        RunFor(window, DiscSpin.SpinDownSeconds + 0.3);                   // eased to a stop
        Assert.False(handler.Spin.Moving);
        Assert.False(handler.Ticking);
        var still = Measure(window, 240, out var stillFrames);
        _out.WriteLine($"spinning: {spinning} B over 240 ticks ({spunFrames} disc frames); still: {still} B ({stillFrames} disc frames)");
        Assert.Equal(240, spunFrames);
        Assert.Equal(0, stillFrames);                                     // at rest: no frames at all
        // The rest of a spinning frame's managed cost is the compositor redrawing the disc's dirty
        // rect (the wash, shade and dither under it): Avalonia's, the same for any animation there.
        Assert.True(spinning - still <= 240 * 16 * 1024, $"spinning allocated {(spinning - still) / 240.0:0.0} B a frame more than still");

        var rest = handler.Spin.Angle;
        Tick(window, 30);
        Assert.Equal(rest, handler.Spin.Angle);                           // the place is kept

        // Play again, then hide the page: it freezes where it is, at once.
        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        RunFor(window, 0.2);
        Assert.True(handler.Spin.Velocity > 0);
        page.IsVisible = false;
        Tick(window, 2);
        var frozen = handler.Spin.Angle;
        var frames = handler.Frames;
        Tick(window, 30);
        Assert.Equal(frozen, handler.Spin.Angle);
        Assert.Equal(frames, handler.Frames);
        Assert.False(handler.Ticking);
        page.IsVisible = true;
        Tick(window, 10);
        Assert.True(handler.Frames > frames);                             // and turns on from there

        Assert.Equal(paints, disc.Paints);                                // never repainted per frame
        window.Close();
    }

    private static void Settle(Window window, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        window.UpdateLayout();
    }

    private static void RunFor(Window window, double seconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            Tick(window, 1);
            System.Threading.Thread.Sleep(8);
        }
    }

    private static void Tick(Window window, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static long Measure(Window window, int ticks, out int discFrames)
    {
        var disc = window.GetVisualDescendantsOf<DiscArtwork>().Single();
        var before = disc.Handler!.Frames;
        Tick(window, 10);   // warm
        before = disc.Handler!.Frames;
        GC.Collect();
        var bytes = GC.GetTotalAllocatedBytes(precise: true);
        Tick(window, ticks);
        bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes;
        discFrames = disc.Handler!.Frames - before;
        return bytes;
    }

    /// <summary>
    /// The PNGs: Now Playing in each style, dark and light theme (the page is always dark; the
    /// light theme shows it stays so), and each disc's bitmap pair at a phone's 1000 px, to judge
    /// the detail. NOCTIS_TEST_SKIA=1 and NOCTIS_VINYL_SHOTS=folder.
    /// </summary>
    [AvaloniaFact]
    public void Shots()
    {
        var shots = Environment.GetEnvironmentVariable("NOCTIS_VINYL_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(shots))
            Assert.Skip("needs NOCTIS_TEST_SKIA=1 and NOCTIS_VINYL_SHOTS");
        Directory.CreateDirectory(shots);
        var cover = WriteCover(768);

        // The discs alone, at a phone's size, turned a little under their light.
        using (var art = new Bitmap(cover))
        foreach (var kind in new[] { NowPlayingArtworkStyle.Vinyl, NowPlayingArtworkStyle.CD })
        {
            using var discImage = DiscPainter.PaintDisc(kind, 1000, art);
            using var light = DiscPainter.PaintLight(kind, 1000);
            using var surface = SKSurface.Create(new SKImageInfo(1000, 1000, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(0x2A, 0x24, 0x30));
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            canvas.Save();
            canvas.RotateRadians(0.6f, 500, 500);
            canvas.DrawImage(discImage, new SKRect(0, 0, 1000, 1000), sampling);
            canvas.Restore();
            canvas.DrawImage(light, new SKRect(0, 0, 1000, 1000), sampling);
            using var png = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(Path.Combine(shots, $"vinyl-disc-{kind.ToString().ToLowerInvariant()}-1000px.png"));
            png.SaveTo(fs);
        }

        var song = MobileFixtures.Song("Midnight City", artist: "M83");
        var album = MobileFixtures.MakeAlbum("Hurry Up, We're Dreaming", "M83", song);
        album.ArtworkPath = song.AlbumArtworkPath = cover;
        using var stack = new MobileThemeSweepTests.PhoneStack();
        foreach (var theme in new[] { "Ink", "Light" })
        {
            stack.Apply(theme);
            foreach (var style in NowPlayingArtworkStyles.All)
            {
                using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
                rig.Shell.NowPlayingArtwork = style;
                var window = MobileFixtures.Mount(rig.Shell, out var view);
                rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
                rig.Shell.OpenNowPlayingCommand.Execute(null);
                MobileFixtures.Land(window);
                Settle(window, 40);
                Tick(window, 45);   // turned a little
                MobileFixtures.Land(window);
                var name = theme == "Light" ? "light" : "dark";
                window.CaptureRenderedFrame()!.Save(Path.Combine(shots, $"vinyl-{style.ToString().ToLowerInvariant()}-{name}.png"));
                window.Close();
            }
        }
    }

    /// <summary>A cover with something to see turn: a warm-to-cool gradient, a sun, a horizon.</summary>
    private string WriteCover(int size)
    {
        using var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp))
        {
            using var sky = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, size),
                    new[] { new SKColor(0x1B, 0x10, 0x4A), new SKColor(0xC8, 0x2E, 0x6E), new SKColor(0xFF, 0xA0, 0x4A) }, null, SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, size, size, sky);
            using var sun = new SKPaint { Color = new SKColor(0xFF, 0xE2, 0x8A), IsAntialias = true };
            canvas.DrawCircle(size * 0.62f, size * 0.58f, size * 0.17f, sun);
            using var sea = new SKPaint { Color = new SKColor(0x10, 0x0C, 0x2A, 0xE6) };
            canvas.DrawRect(0, size * 0.66f, size, size, sea);
            using var line = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xCC), IsAntialias = true, StrokeWidth = size / 90f };
            canvas.DrawLine(size * 0.08f, size * 0.12f, size * 0.45f, size * 0.12f, line);
            canvas.DrawLine(size * 0.08f, size * 0.17f, size * 0.32f, size * 0.17f, line);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Path.GetTempPath(), $"noctis-disc-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        _files.Add(path);
        return path;
    }
}

internal static class DiscTestExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOf<T>(this Visual root) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
