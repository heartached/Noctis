using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Now Playing's wash dither was a tiled ImageBrush on a full-screen Border. Avalonia draws a
/// tiled image brush through a new intermediate surface, image and shader every time it is
/// drawn (DitherLayer's note), and the compositor redraws every visual under a dirty rect: the
/// spinning record, the cover's swipe, the transport's press and the seek bar's four steps a
/// second each drew the full-screen tile again. It is now the lyrics page's DitherLayer: the same
/// tile from one Skia shader made once per process.
/// </summary>
public class MobileNowPlayingDitherTests
{
    private readonly ITestOutputHelper _out;
    public MobileNowPlayingDitherTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public void TheWashDither_IsDrawnFromOneShader_LikeTheLyricsPages()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        Assert.IsType<DitherLayer>(MobileFixtures.Named<Control>(page, "WashDither"));
        Assert.IsType<DitherLayer>(MobileFixtures.Named<Control>(page, "BlurDither"));
        window.Close();
    }

    /// <summary>
    /// Real rendering (NOCTIS_TEST_SKIA=1): the whole process's managed allocation per frame while
    /// the record turns on the default wash, the cost of redrawing what lies under its dirty rect.
    /// </summary>
    [AvaloniaFact]
    public void ASpinningRecord_RedrawsTheWashCheaply()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        rig.Shell.NowPlayingArtwork = NowPlayingArtworkStyle.Vinyl;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var disc = MobileFixtures.Named<DiscArtwork>(page, "Disc");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < DiscSpin.SpinUpSeconds + 0.5)
        {
            Tick(1);
            Thread.Sleep(8);
        }
        Assert.NotNull(disc.Handler);
        Tick(30);
        const int Frames = 300;
        GC.Collect();
        var frames = disc.Handler!.Frames;
        var bytes = GC.GetTotalAllocatedBytes(precise: true);
        Tick(Frames);
        bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes;
        frames = disc.Handler.Frames - frames;
        _out.WriteLine($"spinning record over the wash: {bytes / (double)Frames:0} B a frame ({frames} disc frames)");
        Assert.Equal(Frames, frames);
        // 8.6 KB a frame with the tiled brush, 6.3-6.6 KB with the layer (11-22, Debug); the rest
        // is the compositor redrawing the disc's own dirty rect.
        Assert.True(bytes / Frames < 7_500, $"{bytes / Frames} B a frame");
        window.Close();

        static void Tick(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }
    }
}
