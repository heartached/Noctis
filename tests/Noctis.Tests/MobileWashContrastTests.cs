using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Converters;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Now Playing's Colour Wash at Brightness +50 on a light cover: brighter thins the shade to 40%
/// of it, so over a white cover white text under the transport fell to about 2:1, where the stock
/// Brightness keeps 4.5:1 (MobileNowPlayingPageTests). Blurred Cover's Dim already got the rule
/// (MobileBlurredCoverContrastTests); the wash now gets the same: the user's darkening is the
/// floor, and a cover that needs more gets the least black that keeps white text at 4.5:1 there.
/// </summary>
[Collection("ArtworkCache")]   // the decoder seam is process-wide
public class MobileWashContrastTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public MobileWashContrastTests(ITestOutputHelper output)
    {
        _out = output;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.ClearForTests();
    }

    /// <summary>White text over <paramref name="cover"/> under the shade's transport stop drawn at
    /// <paramref name="shadeOpacity"/> and black at <paramref name="dim"/>, blended as the page draws it.</summary>
    private static double WhiteOn(Color cover, double shadeOpacity, double dim)
    {
        var keep = (1 - BlurredCoverBackdrop.ShadeAtTransport * shadeOpacity) * (1 - dim);
        byte S(byte c) => (byte)Math.Round(c * keep);
        return MobileTheme.ContrastRatio(Colors.White, Color.FromRgb(S(cover.R), S(cover.G), S(cover.B)));
    }

    private static double Shade(int brightness) =>
        1 - 0.6 * Math.Max(0, brightness) / ShellViewModel.NowPlayingBrightnessLimit;

    private static double UserDim(int brightness) =>
        0.6 * Math.Max(0, -brightness) / ShellViewModel.NowPlayingBrightnessLimit;

    [Fact]
    public void AWhiteCoverAtBrightnessPlus50_GetsTheLeastBlackForWhiteText_DarkCoversAndOtherSettingsNone()
    {
        var before = WhiteOn(Colors.White, Shade(50), UserDim(50));
        var dim = WashContrast.DimFor(UserDim(50), Shade(50), Colors.White);
        var after = WhiteOn(Colors.White, Shade(50), dim);
        _out.WriteLine($"Colour Wash, white cover, Brightness +50, white text under the transport: {before:0.00}:1 -> {after:0.00}:1 (black {dim:P0})");
        Assert.True(before < 3, $"{before}");                                // the reported weakness
        Assert.True(after >= BlurredCoverBackdrop.MinTextContrast);
        Assert.True(WhiteOn(Colors.White, Shade(50), dim - 0.01) < BlurredCoverBackdrop.MinTextContrast);   // the least

        // The stock Brightness and darker need nothing extra on any cover: the user's value is drawn.
        foreach (var level in new[] { 0, -25, -50 })
            Assert.Equal(UserDim(level), WashContrast.DimFor(UserDim(level), Shade(level), Colors.White), 6);
        // Dark and mid covers keep +50 as it was.
        foreach (var c in new[] { Colors.Black, Color.Parse("#404040"), Color.Parse("#1A3A6B"), Color.Parse("#8A2BE2") })
            Assert.Equal(0, WashContrast.DimFor(0, Shade(50), c));
        // Brighter still brightens a white cover up to where white text holds: less black at +25 than at +50.
        Assert.InRange(WashContrast.DimFor(0, Shade(25), Colors.White), 0, dim);
        // No wash measured (no cover yet): the user's value.
        Assert.Equal(0.3, (double)WashContrast.Dim.Convert(new object?[] { 0.3, Shade(50), null }, typeof(double), null,
            System.Globalization.CultureInfo.InvariantCulture)!, 6);
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

    /// <summary>The page draws the user's darkening, or more where the wash needs it, live as
    /// Brightness moves. (The headless stub's cover pixels are whatever its read returns, so this
    /// holds the wiring to the wash's measured brightest pixel.)</summary>
    [AvaloniaFact]
    public void ThePage_DrawsTheWashDim_TheUsersOrMoreWhereTheCoverNeedsIt()
    {
        ArtworkCache.DecoderOverride = (_, width) => Plain(Colors.White, Math.Min(width, 512));
        var song = MobileFixtures.Song("Tone");
        var album = MobileFixtures.MakeAlbum("Record", "Artist", song);
        album.ArtworkPath = song.AlbumArtworkPath = "C:/covers/white.jpg";
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var backdrop = MobileFixtures.Named<Image>(page, "Backdrop");
        for (var i = 0; i < 40 && backdrop.Source == null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Thread.Sleep(10);
        }
        var wash = Assert.IsAssignableFrom<Bitmap>(backdrop.Source);
        var brightest = PreBlurredArtworkConverter.WashTonesOf(wash)!.Value.Brightest;
        _out.WriteLine($"the wash's brightest pixel: {brightest}");
        var dim = MobileFixtures.Named<Border>(page, "WashDim");
        foreach (var level in new[] { 50, 25, 0, -50 })
        {
            rig.Shell.NowPlayingBrightness = level;
            var expected = WashContrast.DimFor(rig.Shell.NowPlayingDimOpacity, rig.Shell.NowPlayingShadeOpacity, brightest);
            Assert.Equal(expected, dim.Opacity, 6);
            Assert.True(dim.Opacity >= rig.Shell.NowPlayingDimOpacity);
        }
        window.Close();
    }
}
