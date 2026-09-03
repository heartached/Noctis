using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The flowing-artwork background must be the SAME construction on the lyrics page
/// and the lyrics side panel (the mini player carries a third copy, see its XAML):
/// a pre-blurred cover (no live BlurEffect anywhere in the backdrop), two drifting
/// artwork copies and a beat glow, all driven by the shared frame-clock animator
/// that starts and stops with the Settings toggle.
/// </summary>
public class LyricsFlowingBackdropTests
{
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

    private static LyricsViewModel MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
    }

    private static async Task Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private static double RotationOf(Control layer)
    {
        var group = Assert.IsType<TransformGroup>(layer.RenderTransform);
        return group.Children.OfType<RotateTransform>().Single().Angle;
    }

    private static void AssertBackdropStructure(Control view, string prefix)
    {
        var backdrop = view.FindControl<Panel>($"{prefix}FlowBackdrop");
        var layer1 = view.FindControl<Image>($"{prefix}FlowLayer1");
        var layer2 = view.FindControl<Image>($"{prefix}FlowLayer2");
        var glow = view.FindControl<Avalonia.Controls.Shapes.Rectangle>($"{prefix}BeatGlow");
        Assert.NotNull(backdrop);
        Assert.NotNull(layer1);
        Assert.NotNull(layer2);
        Assert.NotNull(glow);

        // The backdrop is a plain bitmap draw: no live blur on any image, and the old
        // hi-res CachedImage layer (which carried a second live blur) is gone.
        foreach (var image in backdrop!.GetVisualDescendants().OfType<Image>())
            Assert.Null(image.Effect);
        Assert.Empty(backdrop.GetVisualDescendants().OfType<CachedImage>());

        // The animator owns the transforms: scale on the backdrop, a
        // scale+rotate+translate group on each drifting copy.
        Assert.IsType<ScaleTransform>(backdrop.RenderTransform);
        Assert.IsType<TransformGroup>(layer1!.RenderTransform);
        Assert.IsType<TransformGroup>(layer2!.RenderTransform);
    }

    [AvaloniaFact]
    public async Task Panel_HasThePageBackdrop_AndFlowsWithTheToggle()
    {
        var vm = MakeViewModel();
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 360, Height = 720, Content = view };
        win.Show();
        await Pump(60);

        AssertBackdropStructure(view, "Panel");
        var layer1 = view.FindControl<Image>("PanelFlowLayer1")!;
        var backdrop = view.FindControl<Panel>("PanelFlowBackdrop")!;

        // Toggle on (artwork mode is the default): the copies start turning.
        vm.IsColorModeArtwork = true;
        vm.Player.LyricsFlowingLightEnabled = true;
        await Pump(60);
        var a0 = RotationOf(layer1);
        await Pump(120);
        var a1 = RotationOf(layer1);
        Assert.NotEqual(a0, a1);

        // Toggle off: the backdrop snaps to rest and stays there.
        vm.Player.LyricsFlowingLightEnabled = false;
        await Pump(40);
        var scale = Assert.IsType<ScaleTransform>(backdrop.RenderTransform);
        Assert.Equal(1.0, scale.ScaleX, 9);
        Assert.Equal(0.0, view.FindControl<Avalonia.Controls.Shapes.Rectangle>("PanelBeatGlow")!.Opacity, 9);
        var frozen = RotationOf(layer1);
        await Pump(120);
        Assert.Equal(frozen, RotationOf(layer1));

        win.Close();
    }

    [AvaloniaFact]
    public async Task Page_HasTheSameBackdrop_AndStopsWhenDetached()
    {
        var vm = MakeViewModel();
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show();
        await Pump(60);

        AssertBackdropStructure(view, "");
        var layer1 = view.FindControl<Image>("FlowLayer1")!;

        vm.IsColorModeArtwork = true;
        vm.Player.LyricsFlowingLightEnabled = true;
        await Pump(60);
        var a0 = RotationOf(layer1);
        await Pump(120);
        Assert.NotEqual(a0, RotationOf(layer1));

        // Leaving the page parks the flow: no frames burnt for a surface nobody sees.
        win.Content = null;
        await Pump(40);
        var parked = RotationOf(layer1);
        await Pump(120);
        Assert.Equal(parked, RotationOf(layer1));

        win.Close();
    }

    [AvaloniaFact]
    public async Task Panel_SolidBackgroundMode_DoesNotFlow()
    {
        var vm = MakeViewModel();
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 360, Height = 720, Content = view };
        win.Show();
        await Pump(60);

        vm.Player.LyricsFlowingLightEnabled = true;
        vm.IsColorModeArtwork = false;
        await Pump(60);
        var layer1 = view.FindControl<Image>("PanelFlowLayer1")!;
        var a0 = RotationOf(layer1);
        await Pump(120);
        Assert.Equal(a0, RotationOf(layer1));

        win.Close();
    }
}
