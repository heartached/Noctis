using Avalonia;
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
/// Headless regression harness for the lyrics side panel: mounts the real
/// <see cref="LyricsPanelView"/> over a real <see cref="LyricsViewModel"/>, drives
/// active-line changes the way playback does, runs the frame-clock scroll animation,
/// and measures the on-screen geometry of every line (layout bounds + cascade
/// translate). Lines must never render on top of each other.
/// </summary>
public class LyricsPanelOverlapTests
{
    private readonly ITestOutputHelper _output;

    public LyricsPanelOverlapTests(ITestOutputHelper output) => _output = output;

    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds)
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
                Text = i % 3 == 2
                    ? $"Line {i} with a longer body that wraps in the panel"
                    : $"Line {i} lyric",
            })
            .ToList();

    /// <summary>Real-time pump: advances the headless render timer (frame-clock
    /// animations) and dispatcher jobs while wall-clock time passes, sampling
    /// line geometry after every tick via <paramref name="onTick"/>. Async so the
    /// awaits yield to the test dispatcher loop — DispatcherTimers (ScrollToLine's
    /// 10ms settle delay, the follow-resume timer) only fire between frames.</summary>
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

    private static Panel? GetLinesPanel(LyricsPanelView view)
    {
        var items = view.FindControl<ItemsControl>("PanelItemsControl");
        var presenter = items?.GetVisualDescendants().OfType<ItemsPresenter>().FirstOrDefault();
        return presenter?.GetVisualChildren().FirstOrDefault() as Panel;
    }

    private sealed record LineRect(int Index, double Top, double Bottom, double TranslateY);

    /// <summary>Effective vertical extent of each line's visible box: the Button inside
    /// the item container (its margin is the breathing room between lines), offset by
    /// any cascade TranslateTransform on the container.</summary>
    private static List<LineRect> MeasureLines(Panel panel)
    {
        var rects = new List<LineRect>(panel.Children.Count);
        for (var i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            var ty = child.RenderTransform is TranslateTransform tt ? tt.Y : 0;
            var button = (child as Visual)?.GetVisualChildren().OfType<Button>().FirstOrDefault();
            var top = child.Bounds.Y + (button?.Bounds.Y ?? 0) + ty;
            var height = button?.Bounds.Height ?? child.Bounds.Height;
            rects.Add(new LineRect(i, top, top + height, ty));
        }
        return rects;
    }

    /// <summary>Worst pairwise vertical overlap between any two distinct lines, in px.</summary>
    private static (double Overlap, int A, int B) MaxOverlap(List<LineRect> rects)
    {
        double worst = 0; int wa = -1, wb = -1;
        for (var a = 0; a < rects.Count; a++)
            for (var b = a + 1; b < rects.Count; b++)
            {
                var overlap = Math.Min(rects[a].Bottom, rects[b].Bottom)
                              - Math.Max(rects[a].Top, rects[b].Top);
                if (overlap > worst) { worst = overlap; wa = a; wb = b; }
            }
        return (worst, wa, wb);
    }

    private async Task<(LyricsViewModel Vm, LyricsPanelView View, Window Win)> Mount(int lineCount)
    {
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(MakeLines(lineCount));

        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 360, Height = 720, Content = view };
        win.Show();
        await Pump(120);
        return (vm, view, win);
    }

    // ── Tests ──

    [AvaloniaFact]
    public async Task SequentialLineAdvance_NeverOverlapsLines()
    {
        var (vm, view, win) = await Mount(30);
        try
        {
            var panel = GetLinesPanel(view);
            Assert.NotNull(panel);
            var sv = view.FindControl<Avalonia.Controls.ScrollViewer>("PanelScrollViewer");
            _output.WriteLine($"children={panel!.Children.Count} " +
                              $"line0H={panel.Children.FirstOrDefault()?.Bounds.Height:F1} " +
                              $"viewport={sv?.Viewport.Height:F1} extent={sv?.Extent.Height:F1}");

            double worst = 0; string worstAt = ""; double maxOffsetSeen = 0;
            for (var line = 0; line < 24; line++)
            {
                vm.ActiveLineIndex = line;
                var l = line;
                await Pump(300, () =>
                {
                    maxOffsetSeen = Math.Max(maxOffsetSeen, sv?.Offset.Y ?? 0);
                    var (overlap, a, b) = MaxOverlap(MeasureLines(panel!));
                    if (overlap > worst)
                    {
                        worst = overlap;
                        worstAt = $"advancing to line {l}: lines {a}~{b} overlap {overlap:F1}px";
                    }
                });
            }

            _output.WriteLine($"worst transient overlap: {worst:F1}px ({worstAt}); maxOffset={maxOffsetSeen:F1}");
            Assert.True(maxOffsetSeen > 100, "scroll never moved — the harness did not exercise the animation");
            Assert.True(worst < 2, $"lines rendered on top of each other mid-scroll: {worstAt}");
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task LargeJump_NeverOverlapsLines_AndSettlesClean()
    {
        var (vm, view, win) = await Mount(40);
        try
        {
            var panel = GetLinesPanel(view);
            Assert.NotNull(panel);

            vm.ActiveLineIndex = 2;
            await Pump(900);

            // Seek-style jump: maximal scroll delta → maximal cascade displacement.
            double worst = 0; string worstAt = "";
            vm.ActiveLineIndex = 30;
            await Pump(2500, () =>
            {
                var (overlap, a, b) = MaxOverlap(MeasureLines(panel!));
                if (overlap > worst)
                {
                    worst = overlap;
                    worstAt = $"lines {a}~{b} overlap {overlap:F1}px";
                }
            });

            // After the animation has fully settled, no translate may linger.
            var settled = MeasureLines(panel!);
            var stuck = settled.Where(r => Math.Abs(r.TranslateY) > 0.5).ToList();
            _output.WriteLine($"worst transient overlap: {worst:F1}px ({worstAt}); " +
                              $"stuck transforms after settle: {stuck.Count}");

            Assert.True(worst < 2, $"lines rendered on top of each other during the jump: {worstAt}");
            Assert.True(stuck.Count == 0,
                "cascade transforms left applied after the animation finished: " +
                string.Join(", ", stuck.Select(r => $"line {r.Index} y={r.TranslateY:F1}")));
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task BackwardJump_NeverOverlapsLines()
    {
        var (vm, view, win) = await Mount(40);
        try
        {
            var panel = GetLinesPanel(view);
            Assert.NotNull(panel);

            vm.ActiveLineIndex = 30;
            await Pump(2000);

            // Seek-back: negative scroll delta pulls cascade lines upward instead.
            double worst = 0; string worstAt = "";
            vm.ActiveLineIndex = 2;
            await Pump(2500, () =>
            {
                var (overlap, a, b) = MaxOverlap(MeasureLines(panel!));
                if (overlap > worst)
                {
                    worst = overlap;
                    worstAt = $"lines {a}~{b} overlap {overlap:F1}px";
                }
            });

            _output.WriteLine($"worst transient overlap: {worst:F1}px ({worstAt})");
            Assert.True(worst < 2, $"lines rendered on top of each other seeking back: {worstAt}");
        }
        finally
        {
            win.Close();
        }
    }
}
