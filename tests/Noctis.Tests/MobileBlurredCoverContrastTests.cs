using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Now Playing Background › Blurred Cover on a light cover: under the stock shade at 40% and the
/// default Dim (15%), white text over a white cover fell to about 2.6:1 under the transport, where
/// the Colour Wash holds 4.5:1 (MobileNowPlayingPageTests). The Dim drawn is now the user's or, on
/// a cover that needs more, the least that keeps white text at 4.5:1 there; the user's value stays
/// the floor, so the slider still darkens every cover.
/// </summary>
[Collection("ArtworkCache")]   // the decoder seam is process-wide
public class MobileBlurredCoverContrastTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public MobileBlurredCoverContrastTests(ITestOutputHelper output)
    {
        _out = output;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.ClearForTests();
    }

    /// <summary>White text over <paramref name="cover"/> under the shade at <paramref name="shade"/>
    /// (0..1 alpha, drawn at 40%) and a Dim of <paramref name="dim"/>, blended as the page draws it.</summary>
    private static double WhiteOn(Color cover, double shade, double dim)
    {
        var keep = (1 - shade * BlurredCoverBackdrop.ShadeOpacity) * (1 - dim);
        byte S(byte c) => (byte)Math.Round(c * keep);
        return MobileTheme.ContrastRatio(Colors.White, Color.FromRgb(S(cover.R), S(cover.G), S(cover.B)));
    }

    // NowPlayingShade at the title row (~44% down in portrait: between its 0.30 and 0.45 stops) and at the top.
    private const double ShadeAtTitle = (0x59 + (0x73 - 0x59) * (0.44 - 0.30) / 0.15) / 255.0, ShadeAtTop = 0x4D / 255.0;

    [Fact]
    public void ALightCover_GetsTheLeastDimThatKeepsWhiteTextReadable_ADarkOneNone()
    {
        var atDefault = WhiteOn(Colors.White, BlurredCoverBackdrop.ShadeAtTransport, ShellViewModel.DefaultNowPlayingDim / 100.0);
        var dim = BlurredCoverBackdrop.ContrastDimFor(Colors.White);
        var fixedContrast = WhiteOn(Colors.White, BlurredCoverBackdrop.ShadeAtTransport, dim);
        _out.WriteLine($"white cover, white text under the transport: {atDefault:0.00}:1 at Dim 15% -> {fixedContrast:0.00}:1 at Dim {dim:P0}");
        _out.WriteLine($"  title row {WhiteOn(Colors.White, ShadeAtTitle, 0.15):0.00}:1 -> {WhiteOn(Colors.White, ShadeAtTitle, dim):0.00}:1, "
                       + $"top (landscape title) {WhiteOn(Colors.White, ShadeAtTop, 0.15):0.00}:1 -> {WhiteOn(Colors.White, ShadeAtTop, dim):0.00}:1");
        Assert.True(atDefault < 3, $"{atDefault}");                               // the reported weakness
        Assert.True(fixedContrast >= BlurredCoverBackdrop.MinTextContrast);
        Assert.True(WhiteOn(Colors.White, BlurredCoverBackdrop.ShadeAtTransport, dim - 0.01) < BlurredCoverBackdrop.MinTextContrast);   // the least
        Assert.True(WhiteOn(Colors.White, ShadeAtTitle, dim) >= 3);                // the title's large bold type
        Assert.True(WhiteOn(Colors.White, ShadeAtTop, dim) >= 3);

        // Dark and mid covers need nothing: the user's Dim alone is drawn.
        foreach (var c in new[] { Colors.Black, Color.Parse("#404040"), Color.Parse("#1A3A6B"), Color.Parse("#8A2BE2") })
            Assert.Equal(0, BlurredCoverBackdrop.ContrastDimFor(c));
        // Pale colours need some, less than white.
        var pale = BlurredCoverBackdrop.ContrastDimFor(Color.Parse("#F5E6C8"));
        Assert.InRange(pale, 0.01, dim);
    }

    /// <summary>The constants mirror the page: BlurShade's opacity and NowPlayingShade's stop at the transport row.</summary>
    [AvaloniaFact]
    public void TheConstants_MatchThePagesShade()
    {
        var page = new NowPlayingPage();
        Assert.Equal(BlurredCoverBackdrop.ShadeOpacity, page.FindControl<Border>("BlurShade")!.Opacity, 3);
        var shade = Assert.IsType<LinearGradientBrush>(page.Resources["NowPlayingShade"]);
        var stop = Assert.Single(shade.GradientStops, s => Math.Abs(s.Offset - 0.72) < 1e-6);
        Assert.Equal(BlurredCoverBackdrop.ShadeAtTransport, stop.Color.A / 255.0, 6);
    }

    private static WriteableBitmap Plain(Color c, int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[size * 4];
        for (var x = 0; x < size; x++)
        {
            row[x * 4] = c.B;
            row[x * 4 + 1] = c.G;
            row[x * 4 + 2] = c.R;
            row[x * 4 + 3] = 255;
        }
        for (var y = 0; y < size; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        return bitmap;
    }

    private static (MobileFixtures.Rig Rig, Window Window, NowPlayingPage Page) OpenNowPlaying(Color cover)
    {
        ArtworkCache.DecoderOverride = (_, width) => Plain(cover, Math.Min(width, 512));
        var song = MobileFixtures.Song("Tone");
        var album = MobileFixtures.MakeAlbum("Record", "Artist", song);
        album.ArtworkPath = song.AlbumArtworkPath = "C:/covers/white.jpg";
        var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.BlurredCover;
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var c in page.GetLogicalDescendants().OfType<BlurredCoverBackdrop>()) c.PendingBuild.Wait(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Thread.Sleep(5);
        }
        return (rig, window, page);
    }

    /// <summary>The page draws the larger of the user's Dim and the cover's need, live as the
    /// slider moves. (The headless stub's cover pixels are whatever its read returns, so this holds
    /// the wiring to the measured brightest pixel; the Skia run below checks a real white cover.)</summary>
    [AvaloniaFact]
    public void ThePage_DrawsTheUsersDim_OrMoreWhereTheCoverNeedsIt()
    {
        var (rig, window, page) = OpenNowPlaying(Colors.White);
        using var _ = rig;
        var cover = MobileFixtures.Named<BlurredCoverBackdrop>(page, "BlurCover");
        var dim = MobileFixtures.Named<Border>(page, "BlurDim");
        Assert.True(cover.HasImage);
        var need = BlurredCoverBackdrop.ContrastDimFor(cover.Brightest);
        Assert.Equal(need, cover.ContrastDim, 6);
        foreach (var user in new[] { ShellViewModel.DefaultNowPlayingDim, 0, ShellViewModel.NowPlayingDimLimit })
        {
            rig.Shell.NowPlayingDim = user;
            Assert.Equal(Math.Max(user / 100.0, need), dim.Opacity, 6);
        }
        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.Wash;   // no blurred cover: no need
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, cover.ContrastDim);
        window.Close();
    }

    /// <summary>Real rendering (NOCTIS_TEST_SKIA=1): a white cover's blurred copy is white, and the
    /// page lays enough black over it for white text under the transport to read at 4.5:1.</summary>
    [AvaloniaFact]
    public void AWhiteCover_IsDimmedEnoughForWhiteText()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var (rig, window, page) = OpenNowPlaying(Colors.White);
        using var _ = rig;
        var cover = MobileFixtures.Named<BlurredCoverBackdrop>(page, "BlurCover");
        var drawn = MobileFixtures.Named<Border>(page, "BlurDim").Opacity;
        _out.WriteLine($"white cover: brightest {cover.Brightest}, Dim drawn {drawn:P0} (user's 15%)");
        Assert.Equal(Colors.White, cover.Brightest);
        Assert.True(WhiteOn(cover.Brightest, BlurredCoverBackdrop.ShadeAtTransport, drawn) >= 4.5);
        window.Close();
    }
}
