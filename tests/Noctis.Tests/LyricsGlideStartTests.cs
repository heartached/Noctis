using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// Per-frame probe of the first frames of a line change: when does the incoming
/// line's scale transition start moving, and when does the scroll glide? Both are
/// meant to be ONE motion (LineMotion), so the glide must not sit still for frames
/// while the line has already begun to grow — that dead zone, followed by the curve's
/// early acceleration, reads as a hitch at the start of every flow.
/// </summary>
public class LyricsGlideStartTests
{
    private readonly ITestOutputHelper _output;
    public LyricsGlideStartTests(ITestOutputHelper output) => _output = output;

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

    private static List<LyricLine> MakeLines(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new LyricLine
            {
                Timestamp = TimeSpan.FromSeconds(i * 3),
                Text = i % 3 == 2 ? $"Line {i} with a longer body that wraps on the page" : $"Line {i} lyric",
            })
            .ToList();

    private static Panel? LinesPanel(LyricsView view) =>
        view.FindControl<ItemsControl>("LyricsItemsControl")?
            .GetVisualDescendants().OfType<ItemsPresenter>().FirstOrDefault()?
            .GetVisualChildren().FirstOrDefault() as Panel;

    private static double ScaleOf(Control lineContainer)
    {
        var button = (lineContainer as ContentPresenter)?.Child ?? lineContainer;
        return button.RenderTransform?.Value.M11 ?? 1.0;
    }

    private static void Tick()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Tick();
            await Task.Delay(8);
        }
    }

    [AvaloniaFact]
    public async Task ScrollGlide_StartsOnTheSameFrameAsTheLineScale()
    {
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(MakeLines(60));
        // Activate through the VM the way playback does (IsActive + ActiveLineIndex).
        void Activate(int index)
        {
            foreach (var l in vm.LyricLines) l.IsActive = false;
            vm.LyricLines[index].IsActive = true;
            vm.ActiveLineIndex = index;
        }

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 900, Content = view };
        try
        {
            win.Show();
            await Pump(150);
            var panel = LinesPanel(view);
            var sv = view.FindControl<ScrollViewer>("LyricsScrollViewer");
            Assert.NotNull(panel);
            Assert.NotNull(sv);

            Activate(10);
            await Pump(1600);
            var offset0 = sv!.Offset.Y;
            var incoming = panel!.Children[11];
            var scale0 = ScaleOf(incoming);
            Assert.Equal(0.96, scale0, 2);

            Activate(11);
            int? scaleFrame = null, scrollFrame = null;
            for (var frame = 1; frame <= 12; frame++)
            {
                Tick();
                await Task.Delay(16);
                var scale = ScaleOf(incoming);
                var offset = sv.Offset.Y;
                _output.WriteLine($"frame {frame}: scale {scale:F4}  offset {offset - offset0:+0.0;-0.0}px");
                if (scaleFrame is null && scale > scale0 + 1e-4) scaleFrame = frame;
                if (scrollFrame is null && Math.Abs(offset - offset0) > 0.5) scrollFrame = frame;
            }

            Assert.NotNull(scaleFrame);
            Assert.NotNull(scrollFrame);
            // Same frame: the glide used to wait on a 10ms DispatcherTimer, which on
            // Windows' ~15.6ms timer granularity meant the list held still for a frame
            // or two after the line had already brightened and begun to grow, then set
            // off on the curve's acceleration — a hesitation at the start of every flow.
            Assert.True(scrollFrame <= scaleFrame,
                $"the glide started {scrollFrame - scaleFrame} frame(s) after the line began to grow (scale at frame {scaleFrame}, scroll at frame {scrollFrame})");
        }
        finally
        {
            win.Close();
        }
    }
}
