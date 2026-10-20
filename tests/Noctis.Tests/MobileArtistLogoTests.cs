using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Apple Music's artist logos on the phone artist page: the artist's own logo in the name's
/// place when there is one (the name otherwise), sized by its shape, kept readable by a halo
/// instead of a recolour, Play in the logo's accent when it has one; the logo trimmed and its ink
/// read once, beside the file.
/// </summary>
public class MobileArtistLogoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public MobileArtistLogoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A logo PNG: an 800 × 310 transparent canvas with a <paramref name="ink"/> block
    /// <paramref name="w"/> × <paramref name="h"/> in its middle (a quarter of it in
    /// <paramref name="second"/> when given).</summary>
    private static byte[] LogoPng(SKColor ink, int w = 600, int h = 120, SKColor? second = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(800, 310, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        int x0 = (800 - w) / 2, y0 = (310 - h) / 2;
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
            bitmap.SetPixel(x, y, second is { } s && x < x0 + w / 4 ? s : ink);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private string Save(byte[] png, string name = "logo.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, png);
        return path;
    }

    private static ArtistLogoInk Ink(double meanLuminanceBin, Color? accent = null, int w = 600, int h = 120)
    {
        var bins = new double[ArtistLogoInk.Bins];
        bins[(int)meanLuminanceBin] = 1;
        return new ArtistLogoInk(bins, accent, w, h);
    }

    // ---- The logo file and its ink -----------------------------------------------------------

    [Fact]
    public void Trim_CutsTheEmptyCanvas_AndKeepsTheInk()
    {
        var trimmed = ArtistLogoInk.Trim(LogoPng(SKColors.White));
        Assert.NotNull(trimmed);
        using var bitmap = SKBitmap.Decode(trimmed);
        Assert.Equal(600 + 8, bitmap.Width);           // the ink plus 4 px a side
        Assert.Equal(120 + 8, bitmap.Height);
        Assert.Equal(SKColors.White, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
        Assert.Null(ArtistLogoInk.Trim(Array.Empty<byte>()));   // unreadable: the original is kept
    }

    [Fact]
    public void Ink_AWhiteLogo_IsLight_WithoutAccent()
    {
        var ink = ArtistLogoInk.Analyse(Save(LogoPng(SKColors.White)))!;
        Assert.True(ink.IsLight);
        Assert.Null(ink.Accent);
        Assert.Equal(1, ink.LegibleShare(Colors.Black), 3);
        Assert.Equal(0, ink.LegibleShare(Colors.White), 3);
    }

    [Fact]
    public void Ink_APinkLogo_HasItsPinkAsAccent()
    {
        var pink = new SKColor(0xF4, 0xA7, 0xB9);
        var ink = ArtistLogoInk.Analyse(Save(LogoPng(new SKColor(0xE8, 0x4A, 0x8C), second: SKColors.White)))!;
        Assert.NotNull(ink.Accent);
        Assert.Equal(Color.FromRgb(0xE8, 0x4A, 0x8C), ink.Accent!.Value);
        Assert.Null(ArtistLogoInk.Analyse(Save(LogoPng(new SKColor(0x30, 0x30, 0x30), second: pink), "grey.png"))!.Accent);   // a quarter is too little
        // BLACKPINK's pastel pink is a colour, an off-white is not.
        Assert.Equal(Color.FromRgb(0xF4, 0xA7, 0xB9), ArtistLogoInk.Analyse(Save(LogoPng(pink), "pastel.png"))!.Accent);
        Assert.Null(ArtistLogoInk.Analyse(Save(LogoPng(new SKColor(0xF0, 0xE6, 0xE1)), "offwhite.png"))!.Accent);
        Assert.Null(ArtistLogoInk.FromJson("""{ "V": 1, "L": [1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0], "W": 10, "H": 5 }"""));   // an older reading is read again
    }

    [Fact]
    public void Ink_RoundTripsThroughItsSidecar()
    {
        var ink = ArtistLogoInk.Analyse(Save(LogoPng(new SKColor(0xE8, 0x4A, 0x8C))))!;
        var back = ArtistLogoInk.FromJson(ink.ToJson())!;
        Assert.Equal(ink.Luminance, back.Luminance);
        Assert.Equal(ink.Accent, back.Accent);
        Assert.Equal((ink.Width, ink.Height), (back.Width, back.Height));
    }

    [Fact]
    public async Task TheSource_TrimsAndReadsALogoOnce_ThenServesItFromDisk()
    {
        var png = LogoPng(SKColors.White);
        var handler = new LogoHandler(png);
        var service = new ArtistLogoService(new System.Net.Http.HttpClient(handler), Path.Combine(_dir, "artist_logos"),
            new ArtistLogoKeys("project"), prepare: ArtistLogoInk.Trim, musicBrainzSpacing: TimeSpan.Zero);
        var source = new ArtistLogoSource(service);

        var logo = await source.GetLogoAsync("J. Cole", CancellationToken.None);

        Assert.NotNull(logo);
        Assert.Equal((608, 128), (logo!.Ink.Width, logo.Ink.Height));
        Assert.True(File.Exists(logo.Path + ".ink"));
        // A new source (next launch) has it at once, ink and all, without the network.
        var again = new ArtistLogoSource(new ArtistLogoService(new System.Net.Http.HttpClient(new LogoHandler(null)), Path.Combine(_dir, "artist_logos"),
            new ArtistLogoKeys())).CachedLogo("J. Cole");
        Assert.Equal(logo.Path, again!.Path);
        Assert.Equal(logo.Ink.Luminance, again.Ink.Luminance);
    }

    private sealed class LogoHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly byte[]? _png;
        public LogoHandler(byte[]? png) => _png = png;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            if (_png == null) throw new System.Net.Http.HttpRequestException("offline");
            var url = request.RequestUri!.AbsoluteUri;
            System.Net.Http.HttpContent content = url.StartsWith(ArtistLogoService.MusicBrainzUrl, StringComparison.Ordinal)
                ? new System.Net.Http.StringContent("""{ "artists": [ { "id": "875203e1-8e58-4b86-8dcb-7190faf411c5", "name": "J. Cole", "score": 100 } ] }""")
                : url.StartsWith(ArtistLogoService.FanartUrl, StringComparison.Ordinal)
                    ? new System.Net.Http.StringContent("""{ "hdmusiclogo": [ { "url": "https://assets.fanart.tv/l.png", "likes": "1" } ] }""")
                    : new System.Net.Http.ByteArrayContent(_png);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    // ---- Sizing, halo, Play ------------------------------------------------------------------

    [Fact]
    public void LogoSize_FollowsTheShape_WithinTheLimits()
    {
        // A J. Cole-like wordmark (≈1.5:1) stands at the logo height.
        var cole = ArtistPage.LogoSize(1.5, 412);
        Assert.Equal(ArtistPage.LogoHeight, cole.Height);
        Assert.Equal(126, cole.Width);
        // A long one (BLACKPINK's ≈7.6:1) runs to the width limit and thins out.
        var long_ = ArtistPage.LogoSize(7.6, 412);
        Assert.Equal(Math.Round(412 * ArtistPage.LogoWidthShare), long_.Width);
        Assert.Equal(Math.Round(long_.Width / 7.6), long_.Height);
        // A monogram taller than wide stands no taller than the cap.
        var tall = ArtistPage.LogoSize(0.7, 412);
        Assert.Equal(ArtistPage.LogoMaxHeight, tall.Height);
        // A wide screen (landscape, tablet) does not blow it up past the fixed cap.
        Assert.Equal(ArtistPage.LogoMaxWidth, ArtistPage.LogoSize(10, 1200).Width);
        // A small logo (400 px) on a 2.6x screen is drawn no wider than its pixels allow.
        Assert.Equal(Math.Round(400 * ArtistPage.LogoMaxUpscale / 2.625), ArtistPage.LogoSize(3.3, 412, 400 * ArtistPage.LogoMaxUpscale / 2.625).Width);
    }

    [Fact]
    public void Halo_OnlyWhereTheInkWouldNotRead_InTheOppositeTone()
    {
        var white = Ink(15);
        var black = Ink(0);
        var page = Color.FromRgb(0x4A, 0x0E, 0x12);      // a deep red page

        var lift = ArtistPage.LogoHalo(white, page)!;    // white on deep red: Apple's faint lift
        Assert.Equal(Colors.Black, lift.Color);
        Assert.True(lift.Opacity < 0.4);
        var glow = ArtistPage.LogoHalo(black, page)!;    // black on deep red: a light halo
        Assert.Equal(Colors.White, glow.Color);
        Assert.True(glow.Opacity >= 0.6);
        var shade = ArtistPage.LogoHalo(white, Colors.White)!;   // white on a light page: a dark halo
        Assert.Equal(Colors.Black, shade.Color);
        Assert.True(shade.Opacity >= 0.6);
        Assert.Null(ArtistPage.LogoHalo(black, Colors.White));   // black on white needs nothing
    }

    [Fact]
    public void Play_TakesTheLogosAccent_WhenItStandsOut()
    {
        var pinkPage = Color.FromRgb(0xC9, 0x7A, 0x8E);
        var blue = Color.FromRgb(0x7B, 0x8C, 0xF2);
        var (fill, glyph) = ArtistPage.PlayColours(Color.FromRgb(0x2A, 0x0A, 0x30), Colors.White, blue);
        Assert.Equal(blue, fill);
        Assert.Equal(Colors.Black, glyph);
        // Too close to the page: the page's text colour, as without a logo.
        Assert.Equal(Colors.Black, ArtistPage.PlayColours(pinkPage, Colors.Black, Color.FromRgb(0xD0, 0x80, 0x95)).Fill);
        Assert.Equal((Colors.White, Colors.Black), ArtistPage.PlayColours(Colors.Black, Colors.White, null));
    }

    // ---- The page ----------------------------------------------------------------------------

    private sealed class FakeLogos : IArtistLogoSource
    {
        public Dictionary<string, ArtistLogo> Cached { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ArtistLogo> Online { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Cancelled { get; } = new();
        private readonly List<(string Name, TaskCompletionSource<ArtistLogo?> Answer)> _waiting = new();

        public ArtistLogo? CachedLogo(string artistName) => Cached.GetValueOrDefault(artistName);

        public Task<ArtistLogo?> GetLogoAsync(string artistName, CancellationToken ct)
        {
            if (Cached.TryGetValue(artistName, out var cached)) return Task.FromResult<ArtistLogo?>(cached);
            var answer = new TaskCompletionSource<ArtistLogo?>();
            ct.Register(() => { Cancelled.Add(artistName); answer.TrySetCanceled(ct); });
            _waiting.Add((artistName, answer));
            return answer.Task;
        }

        public void Complete()
        {
            foreach (var (name, answer) in _waiting.ToList()) answer.TrySetResult(Online.GetValueOrDefault(name));
            _waiting.Clear();
        }
    }

    private static (Track[] Tracks, Album[] Albums) Band()
    {
        var tracks = Enumerable.Range(1, 8).Select(i => MobileFixtures.Song($"Song {i}", artist: "Band")).ToArray();
        return (tracks, new[] { MobileFixtures.MakeAlbum("Record", "Band", tracks) });
    }

    private ArtistLogo Logo(SKColor ink, string name = "logo.png")
    {
        var path = Save(ArtistLogoInk.Trim(LogoPng(ink))!, name);
        return new ArtistLogo(path, ArtistLogoInk.Analyse(path)!);
    }

    [AvaloniaFact]
    public void ACachedLogo_TakesTheNamesPlace_FromTheFirstFrame()
    {
        var logos = new FakeLogos();
        logos.Cached["Band"] = Logo(SKColors.White);
        var (tracks, albums) = Band();
        using var rig = MobileFixtures.MakeRig(tracks, albums, logos: logos,
            tint: () => new PageTint(_ => Color.FromRgb(0xC8, 0x10, 0x2E), work => Task.FromResult(work())));
        albums[0].ArtworkPath = Save(new byte[] { 1, 2, 3 }, "cover.jpg");
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Band");
        MobileFixtures.Land(window);
        window.UpdateLayout();

        var page = MobileFixtures.Find<ArtistPage>(view);
        var image = MobileFixtures.Named<CachedImage>(page, "ArtistLogo");
        var name = MobileFixtures.Named<TextBlock>(page, "ArtistName");
        Assert.True(image.IsVisible);
        Assert.False(name.IsVisible);
        Assert.Equal(logos.Cached["Band"].Path, image.SourcePath);
        Assert.Equal("Band", Avalonia.Automation.AutomationProperties.GetName(image));   // read out as the name
        var expected = ArtistPage.LogoSize(logos.Cached["Band"].Ink.Aspect, page.Bounds.Width);
        // (Its size as laid out: headless, the bitmap is still loading, so the Image arranges empty.)
        Assert.Equal(expected.Width, image.Width, 1);
        Assert.Equal(expected.Height, image.Height, 1);
        // Centred, above the buttons, over the photo's lower part: the title block is the logo's height.
        var art = MobileFixtures.Named<Panel>(page, "TitleArt");
        Assert.Equal(expected.Height, art.Bounds.Height, 1);
        var box = new Rect(art.TranslatePoint(new Point(0, 0), view)!.Value, art.Bounds.Size);
        Assert.Equal(view.Bounds.Width / 2, box.Center.X, 1);
        Assert.Equal(HorizontalAlignment.Center, image.HorizontalAlignment);
        var play = MobileFixtures.Named<Button>(page, "PlayButton");
        Assert.True(box.Bottom <= play.TranslatePoint(new Point(0, 0), view)!.Value.Y);
        Assert.True(box.Top > MobileFixtures.Named<Panel>(page, "Hero").Bounds.Height * (1 - ArtistPage.HeroFadeShare) - 1);
        window.Close();
    }

    [AvaloniaFact]
    public void TheName_StandsUntilALogoArrives_AndStaysWithoutOne()
    {
        var logos = new FakeLogos();
        logos.Online["Band"] = Logo(new SKColor(0x7B, 0x8C, 0xF2));
        var (tracks, albums) = Band();
        using var rig = MobileFixtures.MakeRig(tracks, albums, logos: logos);
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();
        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        var page = MobileFixtures.Find<ArtistPage>(view);
        Assert.True(MobileFixtures.Named<TextBlock>(page, "ArtistName").IsVisible);
        Assert.False(vm.HasLogo);

        logos.Complete();
        window.UpdateLayout();
        Assert.True(vm.HasLogo);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "ArtistName").IsVisible);
        // The logo's blue is the Play button's (it stands out from the theme's page).
        Assert.Equal(Color.FromRgb(0x7B, 0x8C, 0xF2), ((ISolidColorBrush)MobileFixtures.Named<Button>(page, "PlayButton").Background!).Color);
        window.Close();

        // An artist with no logo keeps the name.
        var (others, otherAlbums) = Band();
        foreach (var t in others) t.Artist = "Other";
        using var rig2 = MobileFixtures.MakeRig(others, otherAlbums, logos: new FakeLogos());
        rig2.Shell.OpenArtistCommand.Execute("Other");
        Assert.False(((ArtistPageViewModel)rig2.Shell.CurrentPage!).HasLogo);
    }

    [Fact]
    public void ClosingThePage_CancelsItsLogoLookup()
    {
        var logos = new FakeLogos();
        var (tracks, albums) = Band();
        using var rig = MobileFixtures.MakeRig(tracks, albums, logos: logos);
        rig.Shell.OpenArtistCommand.Execute("Band");
        rig.Shell.NavigateBackCommand.Execute(null);
        Assert.Equal(new[] { "Band" }, logos.Cancelled);
    }

    [Fact]
    public void WithoutALogoSource_ThePageIsAsBefore()
    {
        var (tracks, albums) = Band();
        using var rig = MobileFixtures.MakeRig(tracks, albums);
        rig.Shell.OpenArtistCommand.Execute("Band");
        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.False(vm.HasLogo);
        Assert.True(vm.LogoLoad.IsCompleted);
    }
}
