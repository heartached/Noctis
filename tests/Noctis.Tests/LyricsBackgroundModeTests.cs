using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Bugs found in the lyrics page background (Artwork / Solid / Gradient), each pinned by the
/// test that failed before its fix.
/// </summary>
public class LyricsBackgroundModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lbgmode-" + Guid.NewGuid().ToString("N"));

    public LyricsBackgroundModeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* plugin contexts may hold files briefly */ }
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    /// <summary>settings.json in memory: every load is a fresh copy, as on disk.</summary>
    private sealed class SettingsStore : TestPersistenceService
    {
        private string _json = JsonSerializer.Serialize(new AppSettings());
        public AppSettings Current { get => JsonSerializer.Deserialize<AppSettings>(_json)!; set => _json = JsonSerializer.Serialize(value); }
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Current);
        public override Task SaveSettingsAsync(AppSettings settings) { Current = settings; return Task.CompletedTask; }
    }

    /// <summary>A one-colour cover on disk with its colours warmed, as a track change leaves them.</summary>
    private string Cover(string hex)
    {
        var path = Path.Combine(_dir, hex.TrimStart('#') + "-" + Guid.NewGuid().ToString("N")[..6] + ".png");
        using (var bmp = new SKBitmap(64, 64))
        {
            bmp.Erase(SKColor.Parse(hex));
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        Assert.True(DominantColorExtractor.WarmFromFile(path));
        return path;
    }

    private static PlayerViewModel NewPlayer(string? coverPath = null)
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = new Track { Title = "Soy Peor", Artist = "Bad Bunny", Album = "Soy Peor - Single" };
        if (coverPath != null)
        {
            player.AlbumArt = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            player.CurrentArtPath = coverPath;
        }
        return player;
    }

    private static LyricsViewModel NewVm(PlayerViewModel player, TestPersistenceService? persistence = null)
        => new(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            persistence ?? new TestPersistenceService(), new FakeLibraryService());

    private static Color ColorOf(IBrush brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>
    /// Bug: the Artwork mode text palette was picked for the cover under a 40% scrim
    /// (ArtworkScrimAlpha = 0x66) while the page has drawn a 60% one (#99000000) since
    /// v1.1.13. A light grey cover then got the "medium" palette (dark button wash, pale
    /// accent) although what shows is a dark grey (200 x 0.4 = 80, luminance 0.31).
    /// </summary>
    [AvaloniaFact]
    public void ArtworkMode_PicksTheTextPalette_UnderThePagesScrim()
    {
        var vm = NewVm(NewPlayer(Cover("#C8C8C8")));
        Assert.True(vm.IsColorModeArtwork);

        Assert.Equal(Color.Parse("#33FFFFFF"), ColorOf(vm.LyricsBtnBg));
        Assert.Equal(Color.Parse("#B0FFFFFF"), ColorOf(vm.LyricsSecondaryFg));
    }

    /// <summary>
    /// Bug: a saved gradient came back at startup without its text palette. Restoring a
    /// solid colour re-picks the palette for it; the gradient branch did not, so the page
    /// kept the one picked for the cover in Artwork mode (here the medium one for a white
    /// cover) over a dark gradient, unlike picking that same gradient by hand.
    /// </summary>
    [AvaloniaFact]
    public void SavedGradient_RestoresWithItsOwnTextPalette()
    {
        const string key = "grad:#0F2027,#2C5364"; // Deep Sea
        var cover = Cover("#FFFFFF");
        var store = new SettingsStore
        {
            Current = new AppSettings { LyricsShowArtworkBackground = false, LyricsBackgroundColorHex = key },
        };
        var restored = NewVm(NewPlayer(cover), store);
        Assert.True(restored.IsColorModeGradient);

        var picked = NewVm(NewPlayer(cover));
        picked.SelectColorModeGradientCommand.Execute(null);
        picked.SetBackgroundColorCommand.Execute(key);

        Assert.Equal(Color.Parse("#33FFFFFF"), ColorOf(picked.LyricsBtnBg));
        Assert.Equal(ColorOf(picked.LyricsBtnBg), ColorOf(restored.LyricsBtnBg));
        Assert.Equal(ColorOf(picked.LyricsSecondaryFg), ColorOf(restored.LyricsSecondaryFg));
    }

    /// <summary>
    /// CI flake: the light, medium and dark palettes are shared by every lyrics page, and were
    /// SolidColorBrushes owned by whichever thread first touched LyricsViewModel. When that was
    /// a plain test on the thread pool (one reading LyricsCacheDir), every lyrics page after it
    /// threw "a different thread owns it" reading or drawing them. Shared brushes are immutable.
    /// </summary>
    [AvaloniaFact]
    public async Task SharedPalettes_ReadFromAnyThread()
    {
        foreach (var (hex, btnBg) in new[] { ("#FFFFFF", "#22000000"), ("#808080", "#44000000"), ("#000000", "#33FFFFFF") })
        {
            var vm = NewVm(NewPlayer(Cover("#C8C8C8")));
            vm.SelectColorModeSolidCommand.Execute(null);
            vm.SetBackgroundColorCommand.Execute(hex);
            Dispatcher.UIThread.RunJobs();
            var palette = new[]
            {
                vm.LyricsSecondaryFg, vm.LyricsSubtleFg, vm.LyricsSliderFilled, vm.LyricsSliderUnfilled,
                vm.LyricsBtnBg, vm.LyricsBtnBgHover, vm.LyricsSliderThumb,
            };

            var colors = await Task.Run(() => palette.Select(ColorOf).ToList());

            Assert.Equal(Color.Parse(btnBg), colors[4]);
        }
    }

    /// <summary>
    /// Bug (seen live 10-12): picking Magenta Fire (#4B134F to #C94B4B) put a green band in
    /// the middle of the lyrics page. The middle stop averaged the two hues straight, (296° +
    /// 0°) / 2 = 148°, which is green, instead of going the short way round through pink.
    /// Every preset whose colours sit either side of red came out wrong the same way.
    /// </summary>
    [Fact]
    public void Gradient_MiddleStop_TakesTheShortWayRoundTheHueWheel()
    {
        var magentaFire = DominantColorExtractor.GenerateGradientBrush(Color.Parse("#4B134F"), Color.Parse("#C94B4B"));
        var mid = magentaFire.GradientStops[2].Color;
        Assert.True(mid.G < mid.R && mid.G < mid.B, $"middle stop {mid} should be pink, not green");

        // Hues on the same side are unchanged: Deep Sea stays blue in the middle.
        var deepSea = DominantColorExtractor.GenerateGradientBrush(Color.Parse("#0F2027"), Color.Parse("#2C5364"));
        var sea = deepSea.GradientStops[2].Color;
        Assert.True(sea.B > sea.R && sea.B >= sea.G, $"middle stop {sea} should stay blue");
    }

    /// <summary>
    /// Bug: Gradient on Auto came back as Solid on Auto after a restart. Auto saves no swatch,
    /// and with no swatch the load fell back to Solid: nothing recorded which mode was chosen.
    /// </summary>
    [AvaloniaFact]
    public void GradientAuto_SurvivesARestart()
    {
        var store = new SettingsStore();
        var cover = Cover("#3366CC");
        var picked = NewVm(NewPlayer(cover), store);
        picked.SelectColorModeGradientCommand.Execute(null);
        Assert.True(picked.IsColorModeGradient);
        Assert.Equal("", store.Current.LyricsBackgroundColorHex); // Auto: no swatch saved

        var restored = NewVm(NewPlayer(cover), store);
        Assert.True(restored.IsColorModeGradient);
        Assert.False(restored.IsColorModeSolid);

        // And Solid on Auto still comes back as Solid.
        restored.SelectColorModeSolidCommand.Execute(null);
        var again = NewVm(NewPlayer(cover), store);
        Assert.True(again.IsColorModeSolid);
        Assert.False(again.IsColorModeGradient);
    }

    /// <summary>
    /// Bug: a saved colour that doesn't parse (a hand-edited settings.json) was kept as the
    /// "custom colour" anyway. Nothing could be drawn for it, and as a custom colour it also
    /// stopped the background from ever following the artwork again: the next song's cover
    /// changed nothing until another colour was picked.
    /// </summary>
    [AvaloniaFact]
    public void UnreadableSavedColor_FallsBackToAuto()
    {
        var store = new SettingsStore
        {
            Current = new AppSettings { LyricsShowArtworkBackground = false, LyricsBackgroundColorHex = "#GGGGGG" },
        };
        var player = NewPlayer();
        var vm = NewVm(player, store);
        Assert.True(vm.IsColorModeSolid);

        // Next song: Solid·Auto shows its cover's colour (warmed, so it applies at once).
        var cover = Cover("#3366CC");
        var vibrant = Color.Parse(ShareCardRenderer.GetVibrantColorHex(cover));
        player.CurrentArtPath = cover;
        player.AlbumArt = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<SolidColorBrush>(vm.FullBackgroundBrush, exactMatch: false);
        Assert.Equal(vibrant, ColorOf(vm.FullBackgroundBrush));
        Assert.Equal("", vm.ActiveSwatchKey);
    }

    /// <summary>
    /// Music video placements on the page (Discord, Cosmo 11-02): the Audio / Video switch shows
    /// with a clip; Video hides the cover and lyrics panels under a full-size layer, Behind
    /// lyrics fills the page behind them, and both park the flowing background they cover.
    /// The clip path does not exist, so no decoder starts (VideoBackdrop checks the file).
    /// </summary>
    [AvaloniaFact]
    public void MusicVideo_AudioVideoSwitch_AndBehindLyrics_SwapThePageLayers()
    {
        var player = NewPlayer();
        player.LyricsFlowingLightEnabled = true;
        player.MusicVideosEnabled = true;
        var vm = NewVm(player);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var switchPill = view.FindControl<Border>("MusicVideoSwitch")!;
            var left = view.FindControl<Border>("LeftPanel")!;
            var right = view.FindControl<Grid>("RightPanel")!;
            var full = view.FindControl<Grid>("MusicVideoFullSizeLayer")!;
            var behind = view.FindControl<Grid>("MusicVideoBackdropLayer")!;
            var flow = view.FindControl<Panel>("FlowLayerHost")!;
            Assert.False(switchPill.IsVisible); // no clip, no switch
            Assert.True(flow.IsVisible);

            player.CurrentMusicVideoPath = Path.Combine(_dir, "missing.mp4");
            Dispatcher.UIThread.RunJobs();
            Assert.True(switchPill.IsVisible);
            Assert.True(left.IsVisible && right.IsVisible);
            Assert.False(full.IsVisible);
            Assert.False(behind.IsVisible);

            player.ShowMusicVideoFullSizeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(full.IsVisible);
            Assert.False(left.IsVisible || right.IsVisible);
            Assert.False(flow.IsVisible);
            Assert.True(switchPill.IsVisible); // the way back stays on screen

            player.ShowMusicVideoAudioCommand.Execute(null);
            player.MusicVideoAsBackground = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(full.IsVisible);
            Assert.True(behind.IsVisible);
            Assert.True(left.IsVisible && right.IsVisible);
            Assert.False(flow.IsVisible);

            player.CurrentMusicVideoPath = null;
            Dispatcher.UIThread.RunJobs();
            Assert.False(behind.IsVisible);
            Assert.False(switchPill.IsVisible);
            Assert.True(flow.IsVisible);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>
    /// Bug: a plugin Flowing style (Settings ▸ Appearance ▸ Flowing Lyrics Background ▸ Style)
    /// kept its layer on the page after switching the background to Solid or Gradient. The
    /// built-in Drift and Kawarp styles stop outside Artwork mode; the plugin layer, which
    /// stands in for them and paints over the page's colour, did not, so the picked colour
    /// never showed.
    /// </summary>
    [AvaloniaFact]
    public void PluginFlowingLayer_ShowsOnlyInArtworkMode()
    {
        var pluginDir = Path.Combine(_dir, "plugins", "PulseRingPlugin");
        Directory.CreateDirectory(pluginDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.dll"),
            Path.Combine(pluginDir, "Noctis.SamplePlugin.dll"));
        var settings = new AppSettings();
        var plugins = new PluginHost(null, _dir, () => settings, () => { }, "test");
        plugins.LoadAll();
        var layer = Assert.Single(plugins.VisualLayers);

        var player = NewPlayer();
        player.LyricsFlowingLightEnabled = true;
        player.LyricsFlowingStyle = layer.Name;
        var vm = NewVm(player);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            // The page takes its plugin host from the app's services, which tests don't have.
            typeof(LyricsView).GetField("_pluginHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, plugins);
            typeof(LyricsView).GetMethod("UpdateFlowAnimationState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object[] { vm });
            var host = view.FindControl<Panel>("PluginLayerHost")!;
            Assert.Single(host.Children);

            vm.SelectColorModeSolidCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(host.Children);

            vm.SelectColorModeGradientCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(host.Children);

            vm.SelectColorModeArtworkCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(host.Children);
        }
        finally
        {
            win.Close();
            plugins.UnloadAll();
        }
    }
}
