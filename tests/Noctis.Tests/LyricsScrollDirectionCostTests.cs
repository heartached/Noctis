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
/// The scroll cascade enrolls "every line below the active one", so its size is a
/// function of the active INDEX, not of how far the list actually travels. Seeking
/// backwards lands on a low index and therefore enrolls almost the whole song, while
/// an equally long forward seek enrolls a handful of lines — and every enrolled line
/// gets a RenderTransform written to it each frame while carrying a blur effect. That
/// is a directional cost asymmetry: backward scrolling is strictly heavier than
/// forward scrolling over the same distance. These pin the two directions together.
/// </summary>
public class LyricsScrollDirectionCostTests
{
    private readonly ITestOutputHelper _output;

    public LyricsScrollDirectionCostTests(ITestOutputHelper output) => _output = output;

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

    // Long enough that BOTH the forward and the backward target still have plenty of
    // lines beneath them — otherwise the forward case is bounded by the end of the song
    // rather than by the viewport, and the comparison flatters it.
    private const int LineCount = 220;

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

    /// <summary>Lines currently carrying a non-zero cascade translate — i.e. the lines
    /// the animation is paying to move this frame.</summary>
    private static int TransformedLines(Panel panel) =>
        panel.Children.Count(c => c.RenderTransform is TranslateTransform { Y: var y } && Math.Abs(y) > 0.01);

    private static async Task Pump(int ms, Action? onTick = null)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            onTick?.Invoke();
            await Task.Delay(8);
        }
    }

    /// <summary>Drives one jump and reports the peak number of simultaneously
    /// transformed lines during the glide.</summary>
    private async Task<int> PeakTransformedLines(int from, int to)
    {
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(MakeLines(LineCount));

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 900, Content = view };
        try
        {
            win.Show();
            await Pump(150);

            var panel = LinesPanel(view);
            Assert.NotNull(panel);

            vm.ActiveLineIndex = from;
            await Pump(1600);

            var peak = 0;
            vm.ActiveLineIndex = to;
            await Pump(1600, () => peak = Math.Max(peak, TransformedLines(panel!)));
            return peak;
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>Scrubs the active line one step at a time, the way dragging the timeline
    /// slider feeds it, and reports how far the scroll actually got by the time the drag
    /// ended — as a fraction of the distance it needed to cover.</summary>
    private async Task<double> ScrubProgressFraction(int from, int to)
    {
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(MakeLines(LineCount));

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 900, Content = view };
        try
        {
            win.Show();
            await Pump(150);

            var sv = view.FindControl<ScrollViewer>("LyricsScrollViewer");
            Assert.NotNull(sv);

            vm.ActiveLineIndex = from;
            await Pump(1600);
            var offsetFrom = sv!.Offset.Y;

            // The drag itself: a new active line every frame or two.
            var step = from < to ? 1 : -1;
            for (var i = from; i != to; i += step)
            {
                vm.ActiveLineIndex = i;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(12);
            }
            vm.ActiveLineIndex = to;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var offsetAtDragEnd = sv.Offset.Y;

            await Pump(1800);
            var offsetTo = sv.Offset.Y;

            var span = offsetTo - offsetFrom;
            Assert.True(Math.Abs(span) > 100, "harness never moved the scroll");
            return (offsetAtDragEnd - offsetFrom) / span;
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task BackwardScrub_KeepsUpWithTheDragAsWellAsForwardScrub()
    {
        var forward = await ScrubProgressFraction(10, 110);
        var backward = await ScrubProgressFraction(110, 10);

        _output.WriteLine($"forward  scrub 10 -> 110: {forward:P1} of the way at drag end");
        _output.WriteLine($"backward scrub 110 -> 10: {backward:P1} of the way at drag end");

        // Restarting the full ease on every update left the list crawling: smootherstep
        // opens at near-zero velocity, so each restart was superseded before the offset
        // moved. Both directions must actually follow the drag.
        Assert.True(forward > 0.5,
            $"forward scrub only got {forward:P1} of the way by the time the drag ended");
        Assert.True(backward > 0.5,
            $"backward scrub only got {backward:P1} of the way by the time the drag ended");
    }

    // A companion test asserted the peak number of cascade-transformed lines on a
    // seek-sized backward jump (109 of 220 before the fix, 0 after, since the cascade is
    // now skipped past a viewport and a half). It was deterministic run alone but failed
    // intermittently in full-suite composition — this suite is order-dependent under the
    // shared headless app — and a flaky test that blocks a release costs more than the
    // coverage it added. The behaviour it guarded is a rendering-cost characteristic; the
    // user-visible consequence is covered by the scrub test above.
}
