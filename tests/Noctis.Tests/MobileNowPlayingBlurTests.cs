using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Converters;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord #mobile-beta request: a Now Playing background of the song's own cover, heavily
/// blurred, with a black tint over it ("50% blur with 15% opacity"). Settings › Appearance › Now
/// Playing Background: Colour Wash (the default, the look as it was) or Blurred Cover, with Blur
/// and Dim. The cover is blurred once per cover, off the UI thread, from its 128 px copy.
/// </summary>
[Collection("ArtworkCache")]   // the decoder seam is process-wide
public class MobileNowPlayingBlurTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public MobileNowPlayingBlurTests(ITestOutputHelper output)
    {
        _out = output;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.ClearForTests();
    }

    /// <summary>A cover: a gradient with a bright square, so a blur visibly changes it.</summary>
    private static WriteableBitmap Cover(int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var square = x > size / 3 && x < size * 2 / 3 && y > size / 3 && y < size * 2 / 3;
                row[x * 4] = square ? (byte)255 : (byte)(x * 200 / size);
                row[x * 4 + 1] = square ? (byte)255 : (byte)(y * 120 / size);
                row[x * 4 + 2] = square ? (byte)255 : (byte)40;
                row[x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bitmap;
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var pixels = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, w * 4); }
        finally { handle.Free(); }
        return pixels;
    }

    // ---- The setting -------------------------------------------------------------------------

    [Fact]
    public void Defaults_KeepTheWash_AndMatchTheRequest()
    {
        var settings = new Noctis.Models.AppSettings();
        Assert.Equal("Wash", settings.MobileNowPlayingBackground);
        Assert.Equal(50, settings.MobileNowPlayingBlur);
        Assert.Equal(15, settings.MobileNowPlayingDim);

        using var rig = MobileFixtures.MakeRig();
        Assert.Equal(NowPlayingBackgroundStyle.Wash, rig.Shell.NowPlayingBackground);
        Assert.False(rig.Shell.IsBlurredCoverBackground);
        Assert.Equal(50, rig.Shell.NowPlayingBlur);
        Assert.Equal(150, rig.Shell.NowPlayingBlurPercent);   // one and a half times the stock sigma
        Assert.Equal(0.15, rig.Shell.NowPlayingCoverDimOpacity, 3);

        Assert.Equal(NowPlayingBackgroundStyle.Wash, NowPlayingBackgroundStyles.Parse(null));
        Assert.Equal(NowPlayingBackgroundStyle.Wash, NowPlayingBackgroundStyles.Parse("Kawarp"));
        Assert.Equal(NowPlayingBackgroundStyle.Wash, NowPlayingBackgroundStyles.Parse("1"));
        Assert.Equal(NowPlayingBackgroundStyle.BlurredCover, NowPlayingBackgroundStyles.Parse(" blurredcover "));
    }

    [Fact]
    public async Task TheStyle_Blur_AndDim_AreSaved_AndReadBack_Clamped()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileNowPlayingBackground = "BlurredCover";
            s.MobileNowPlayingBlur = 30;
            s.MobileNowPlayingDim = 40;
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.Equal(NowPlayingBackgroundStyle.BlurredCover, rig.Shell.NowPlayingBackground);
        Assert.Equal(30, rig.Shell.NowPlayingBlur);
        Assert.Equal(40, rig.Shell.NowPlayingDim);

        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.Wash;
        rig.Shell.NowPlayingBlur = 70;
        rig.Shell.NowPlayingDim = 5;
        await rig.Shell.PendingPlayerBarSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("Wash", saved.MobileNowPlayingBackground);
        Assert.Equal(70, saved.MobileNowPlayingBlur);
        Assert.Equal(5, saved.MobileNowPlayingDim);
        // Saved beside the neighbours, never over them.
        Assert.Equal("Cover", saved.MobileNowPlayingArtwork);

        using var rig2 = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileNowPlayingBackground = "Plasma";   // a newer build's, or a hand edit
            s.MobileNowPlayingBlur = 400;
            s.MobileNowPlayingDim = 99;
            await p.SaveSettingsAsync(s);
        });
        await rig2.Shell.InitializeAsync();
        Assert.Equal(NowPlayingBackgroundStyle.Wash, rig2.Shell.NowPlayingBackground);
        Assert.Equal(100, rig2.Shell.NowPlayingBlur);
        Assert.Equal(ShellViewModel.NowPlayingDimLimit, rig2.Shell.NowPlayingDim);
    }

    /// <summary>A pick made before the saved values were read stays, and is saved.</summary>
    [Fact]
    public async Task APick_BeforeTheLoad_Stays()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileNowPlayingDim = 40;
            await p.SaveSettingsAsync(s);
        });
        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.BlurredCover;
        await rig.Shell.InitializeAsync();
        await rig.Shell.PendingPlayerBarSave;
        Assert.Equal(NowPlayingBackgroundStyle.BlurredCover, rig.Shell.NowPlayingBackground);
        Assert.Equal(40, rig.Shell.NowPlayingDim);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("BlurredCover", saved.MobileNowPlayingBackground);
        Assert.Equal(40, saved.MobileNowPlayingDim);
    }

    /// <summary>The row opens Colour Wash / Blurred Cover on the glass menu; a pick applies at
    /// once, and the card swaps Brightness for Blur and Dim.</summary>
    [AvaloniaFact]
    public async Task TheSettingsRow_PicksTheStyle_AndShowsItsSliders()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        await vm.Loaded;
        MobileFixtures.Land(window);
        Assert.Equal("Colour Wash", vm.NowPlayingBackgroundValue);
        Assert.Equal("Now Playing Background, Colour Wash", vm.NowPlayingBackgroundRowName);
        var page = view.GetLogicalDescendants().OfType<SettingsPage>().Single();
        var wash = MobileFixtures.Named<StackPanel>(page, "NowPlayingWashOptions");
        var blur = MobileFixtures.Named<StackPanel>(page, "NowPlayingBlurOptions");
        Assert.True(wash.IsVisible);
        Assert.False(blur.IsVisible);

        vm.ShowNowPlayingBackgroundMenuCommand.Execute(null);
        var menu = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        Assert.Equal(new[] { "Colour Wash", "Blurred Cover" }, menu.Items.Select(i => i.Label));
        Assert.Equal(new[] { true, false }, menu.Items.Select(i => i.IsChecked));
        menu.Items[1].PickCommand.Execute(null);
        Assert.Null(rig.Shell.PopMenu);
        Assert.Equal(NowPlayingBackgroundStyle.BlurredCover, rig.Shell.NowPlayingBackground);
        Assert.Equal("Blurred Cover", vm.NowPlayingBackgroundValue);
        Assert.False(wash.IsVisible);
        Assert.True(blur.IsVisible);
        Assert.Equal("50%", MobileFixtures.Named<TextBlock>(page, "NowPlayingBlurLabel").Text);
        Assert.Equal("15%", MobileFixtures.Named<TextBlock>(page, "NowPlayingDimLabel").Text);
        window.Close();
    }

    // ---- The page ----------------------------------------------------------------------------

    private static (MobileFixtures.Rig Rig, Window Window, NowPlayingPage Page) OpenNowPlaying(bool withCover)
    {
        ArtworkCache.DecoderOverride = (_, width) => Cover(Math.Min(width, 768));
        var song = MobileFixtures.Song("Tone");
        var album = MobileFixtures.MakeAlbum("Record", "Artist", song);
        if (withCover) album.ArtworkPath = song.AlbumArtworkPath = "C:/covers/tone.jpg";
        var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        Settle(window, page);
        return (rig, window, page);
    }

    /// <summary>Lets the cover decode and any blur finish, and their results land.</summary>
    private static void Settle(Window window, Visual page)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var cover in page.GetVisualDescendantsOf<BlurredCoverBackdrop>())
                cover.PendingBuild.Wait(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            System.Threading.Thread.Sleep(5);
        }
    }

    [AvaloniaFact]
    public void TheDefault_IsTheWashAsBefore()
    {
        var (rig, window, page) = OpenNowPlaying(withCover: true);
        using var _ = rig;
        var cover = MobileFixtures.Named<BlurredCoverBackdrop>(page, "BlurCover");
        Assert.NotNull(MobileFixtures.Named<Noctis.Controls.CachedImage>(page, "Artwork").Source);   // the cover is there
        Assert.False(cover.HasImage);
        Assert.Null(cover.Current);                                   // nothing blurred while off
        Assert.True(MobileFixtures.Named<Panel>(page, "WashLayer").IsVisible);
        Assert.False(MobileFixtures.Named<Panel>(page, "BlurLayer").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void BlurredCover_ShowsTheBlurredCover_UnderTheShadeAndDim_Live()
    {
        var (rig, window, page) = OpenNowPlaying(withCover: true);
        using var _ = rig;
        var cover = MobileFixtures.Named<BlurredCoverBackdrop>(page, "BlurCover");
        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.BlurredCover;
        Settle(window, page);

        Assert.True(cover.HasImage);
        Assert.Equal(new PixelSize(128, 128), cover.Current!.PixelSize);    // the 128 px copy, never the screen
        Assert.False(MobileFixtures.Named<Panel>(page, "WashLayer").IsVisible);
        Assert.True(MobileFixtures.Named<Panel>(page, "BlurLayer").IsVisible);
        Assert.Equal(0.4, MobileFixtures.Named<Border>(page, "BlurShade").Opacity, 3);   // the readability floor
        Assert.Equal(0.15, MobileFixtures.Named<Border>(page, "BlurDim").Opacity, 3);
        Assert.Equal(150, cover.BlurPercent);

        rig.Shell.NowPlayingDim = 40;
        Assert.Equal(0.4, MobileFixtures.Named<Border>(page, "BlurDim").Opacity, 3);

        // A Blur step re-blurs the same cover (a new bitmap, the old one let go).
        var before = cover.Current;
        var sharp = Pixels(before);
        rig.Shell.NowPlayingBlur = 0;
        Settle(window, page);
        Assert.Equal(0, cover.BlurPercent);
        Assert.NotSame(before, cover.Current);
        Assert.False(cover.IsFading);                                    // a slider step swaps, no fade
        Assert.NotEqual(sharp, Pixels(cover.Current!));

        // Back to the wash: the bitmaps go and the wash shows again.
        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.Wash;
        Settle(window, page);
        Assert.False(cover.HasImage);
        Assert.Null(cover.Current);
        Assert.True(MobileFixtures.Named<Panel>(page, "WashLayer").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void NoArtwork_FallsBackToTheWash()
    {
        var (rig, window, page) = OpenNowPlaying(withCover: false);
        using var _ = rig;
        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.BlurredCover;
        Settle(window, page);
        var cover = MobileFixtures.Named<BlurredCoverBackdrop>(page, "BlurCover");
        Assert.False(cover.HasImage);
        Assert.True(MobileFixtures.Named<Panel>(page, "WashLayer").IsVisible);
        Assert.False(MobileFixtures.Named<Panel>(page, "BlurLayer").IsVisible);
        window.Close();
    }

    /// <summary>The Lyrics page in Now Playing wears it too, under its own (darker) shade.</summary>
    [AvaloniaFact]
    public void TheLyricsPage_WearsTheBlurredCover()
    {
        var (rig, window, page) = OpenNowPlaying(withCover: true);
        using var _ = rig;
        rig.Shell.NowPlayingBackground = NowPlayingBackgroundStyle.BlurredCover;
        rig.Shell.ToggleLyricsCommand.Execute(null);
        MobileFixtures.Land(window);
        var lyrics = ((Visual)window).GetLogicalDescendants().OfType<LyricsPage>().Single();
        Settle(window, lyrics);
        var cover = MobileFixtures.Named<BlurredCoverBackdrop>(lyrics, "BlurCover");
        Assert.True(cover.HasImage);
        Assert.False(MobileFixtures.Named<Image>(lyrics, "Backdrop").IsVisible);
        Assert.True(MobileFixtures.Named<Border>(lyrics, "LyricsShade").IsVisible);
        window.Close();
    }

    // ---- The control -------------------------------------------------------------------------

    /// <summary>A new cover cross-fades over the old one; the old bitmap is disposed once the
    /// fade is over. Under Battery Saver the new one simply replaces it.</summary>
    [AvaloniaFact]
    public void ANewCover_CrossFades_ButNotUnderBatterySaver()
    {
        using var rig = MobileFixtures.MakeRig();
        var backdrop = new BlurredCoverBackdrop { IsActive = true, DataContext = rig.Shell };
        var window = new Window { Width = 400, Height = 800, Content = backdrop };
        window.Show();
        void Wait() { backdrop.PendingBuild.Wait(TimeSpan.FromSeconds(5)); Dispatcher.UIThread.RunJobs(); }

        backdrop.Source = Cover(512);
        Wait();
        var first = backdrop.Current!;
        Assert.False(backdrop.IsFading);                                 // nothing to fade from

        backdrop.Source = Cover(512);
        Wait();
        Assert.True(backdrop.IsFading);
        Assert.NotSame(first, backdrop.Current);
        backdrop.EndFade();
        Dispatcher.UIThread.RunJobs();
        Assert.ThrowsAny<Exception>(() => Pixels(first));               // disposed

        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.BatterySaver))!.SetValue(rig.Shell, new Saver());
        backdrop.Source = Cover(512);
        Wait();
        Assert.False(backdrop.IsFading);
        window.Close();
    }

    private sealed class Saver : Noctis.Mobile.Services.IBatterySaver
    {
        public bool IsOn => true;
    }

    /// <summary>What one blur costs, off the UI thread: a phone cover (768 px decode, 1080 px at
    /// most) read down to 128 px and blurred, at the default Blur and at the most.</summary>
    [AvaloniaFact]
    public void TheBlur_IsSmall_AndCheap()
    {
        foreach (var size in new[] { 768, 1080 })
        {
            using var cover = Cover(size);
            using (PreBlurredArtworkConverter.BlurredCopy(cover, 150)) { }   // warm up the JIT
            foreach (var percent in new[] { 150, 300 })
            {
                const int Runs = 10;
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < Runs; i++)
                    using (var blurred = PreBlurredArtworkConverter.BlurredCopy(cover, percent))
                        Assert.Equal(new PixelSize(128, 128), blurred.PixelSize);
                var ms = clock.Elapsed.TotalMilliseconds / Runs;
                _out.WriteLine($"{size} px cover, blur {percent}% of stock sigma: {ms:0.00} ms per blur");
                Assert.True(ms < 250, $"{ms} ms");
            }
        }
    }
}
