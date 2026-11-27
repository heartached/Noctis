using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: "when I scroll fast on the lyrics (the review's lines), it's very laggy and it
/// stutters a lot". An 80-line word-timed song in the review, scrolled from top to bottom in 30
/// wheel-sized steps; each step's UI-thread work (layout, and with NOCTIS_TEST_SKIA=1 the render)
/// is timed, with the rows built for it.
/// </summary>
public class LyricsStudioScrollPerfTests : IDisposable
{
    private const int Lines = 80;
    private const int Words = 8;
    private const int Steps = 30;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-scroll-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _out;

    public LyricsStudioScrollPerfTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [AvaloniaFact]
    public void ScrollingTheReview_TopToBottom_StaysUnderAFrameAStep()
    {
        EnsureAppResources();
        var vm = StudioWithRestoredReviews(2);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1400, Height = 900, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            // Another song first, scrolled through: the row template and styles have run once, as
            // after any use of the Studio. The timed song's rows are then new.
            vm.Selected = vm.Queue[1];
            Settle(win);
            ScrollThrough(win, Scroller(panel, vm), panel, record: false);
            vm.Selected = vm.Queue[0];
            var scroller = Scroller(panel, vm);
            scroller.Offset = new Vector(0, 0);
            Settle(win);
            Assert.Equal(Lines, vm.ReviewLines.Count);
            Assert.True(scroller.Viewport.Height is > 100 and < 2000, $"viewport {scroller.Viewport.Height}");
            var row = Rows(panel)[0];
            _out.WriteLine($"one row: {row.GetVisualDescendants().Count() + 1} visuals, {Rows(panel).Count} rows built at the pick");

            var first = ScrollThrough(win, scroller, panel, record: true);
            Report("first scroll through the song", first);
            Assert.Contains(Rows(panel), r => ReferenceEquals(r.DataContext, vm.ReviewLines[^1]));
            var up = ScrollThrough(win, scroller, panel, record: false, up: true);
            Report("back up", up);
            var again = ScrollThrough(win, scroller, panel, record: false);
            Report("down again", again);

            // Before (11-26): every line coming into view was torn down and built again, ~45 ms a
            // step in every pass (stub) and the same with Skia. Kept rows: up and down again cost
            // the scroll itself. The first pass still builds each row once (lighter rows now).
            foreach (var (name, pass) in new[] { ("back up", up), ("down again", again) })
            {
                Assert.Equal(0, pass.RowsBuilt);
                Assert.True(pass.Avg < 16, $"{name}: average step {pass.Avg:0.0} ms");
                Assert.True(pass.Worst < 33, $"{name}: worst step {pass.Worst:0.0} ms");
            }
            Assert.True(first.Avg < 40, $"first pass: average step {first.Avg:0.0} ms");
        }
        finally { win.Close(); }
    }

    /// <summary>The body's narrow layout is a class set from its width now (it was a container query
    /// on the body, which made every control inside it watch the body's size): same widths as before.</summary>
    [AvaloniaTheory]
    [InlineData(1400, 320)]
    [InlineData(900, 250)]
    public void TheQueueNarrows_AtTheSameBodyWidth_AsTheContainerQueryDid(double windowWidth, double queueWidth)
    {
        EnsureAppResources();
        var vm = StudioWithRestoredReviews(1);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = windowWidth, Height = 800, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            Settle(win);
            var body = panel.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "StudioBody");
            var queue = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("studio-queue"));
            Assert.Equal(body.Bounds.Width <= LyricsStudioPanel.NarrowBodyWidth, body.Classes.Contains("studio-narrow"));
            Assert.Equal(queueWidth, queue.Bounds.Width);
        }
        finally { win.Close(); }
    }

    private void Report(string what, (double Worst, double Avg, int WorstStep, int RowsBuilt, double Mb) pass) =>
        _out.WriteLine($"{what}, {Steps} steps over {Lines} lines (render={(HeadlessTestApp.RealRendering ? "skia" : "stub")}): worst {pass.Worst:0.0} ms (step {pass.WorstStep}), average {pass.Avg:0.0} ms, {pass.RowsBuilt} rows built, {pass.Mb:0.0} MB");

    private static ScrollViewer Scroller(Control panel, LyricsStudioViewModel vm) =>
        panel.GetVisualDescendants().OfType<ItemsControl>()
            .Single(c => ReferenceEquals((c as Noctis.Controls.KeptRowsItemsControl)?.Rows ?? c.ItemsSource, vm.ReviewLines))
            .FindAncestorOfType<ScrollViewer>()!;

    private (double Worst, double Avg, int WorstStep, int RowsBuilt, double Mb) ScrollThrough(Window win, ScrollViewer scroller, Control panel, bool record, bool up = false)
    {
        // Rows are counted as built the first time their control is seen in this panel.
        var built = 0;
        var times = new List<double>();
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        foreach (var r in Rows(panel)) _seen.Add(r);
        for (var i = 1; i <= Steps; i++)
        {
            var sw = Stopwatch.StartNew();
            // A wheel step each frame, aimed at the end as the extent firms up while rows are measured.
            var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = up
                ? new Vector(0, Math.Max(0, max * (Steps - i) / Steps))
                : new Vector(0, Math.Min(max, Math.Max(scroller.Offset.Y + 1, max * i / Steps)));
            win.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (HeadlessTestApp.RealRendering) win.CaptureRenderedFrame();
            else AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            times.Add(sw.Elapsed.TotalMilliseconds);
            var newRows = Rows(panel).Count(r => _seen.Add(r));
            built += newRows;
            if (record) _out.WriteLine($"  step {i}: {sw.Elapsed.TotalMilliseconds:0.0} ms, offset {scroller.Offset.Y:0}/{scroller.Extent.Height - scroller.Viewport.Height:0}, rows built {newRows}");
        }
        var mb = (GC.GetAllocatedBytesForCurrentThread() - alloc) / (1024.0 * 1024);
        var worst = times.Max();
        return (worst, times.Average(), times.IndexOf(worst) + 1, built, mb);
    }

    private readonly HashSet<Control> _seen = new(ReferenceEqualityComparer.Instance);

    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static List<Border> Rows(Control panel) =>
        panel.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("review-line") && b.IsEffectivelyVisible)
            .ToList();

    private LyricsStudioViewModel StudioWithRestoredReviews(int songs)
    {
        var drafts = new LyricsStudioDraftStore(Path.Combine(_root, "drafts"));
        var tracks = new List<Track>();
        for (var n = 0; n < songs; n++)
        {
            var t = new Track { Title = "Song " + n, Artist = "Artist", FilePath = Path.Combine(_root, $"song{n}.mp3"), Duration = TimeSpan.FromMinutes(5) };
            tracks.Add(t);
            var lines = Enumerable.Range(0, Lines).Select(l =>
            {
                var start = TimeSpan.FromSeconds(5 + l * 3.5);
                var words = Enumerable.Range(0, Words)
                    .Select(w => new AlignedWord(w % 3 == 0 ? $"longerword{w}" : $"word{w}", start + TimeSpan.FromMilliseconds(w * 300), start + TimeSpan.FromMilliseconds(w * 300 + 280)))
                    .ToList();
                return new AlignedLine(string.Join(' ', words.Select(w => w.Text)), start, start + TimeSpan.FromSeconds(2.5), words, 0.9, false);
            }).ToList();
            drafts.Save(t.Id, LyricsStudioDraft.From(new LyricsStudioResult(t, lines, LyricsStudioSource.ExistingFile, 0.9, "en", 600)));
        }
        return new LyricsStudioViewModel(tracks, new NullEngine(), new LyricsWriter(null!, null), new FakeLibraryService(), null,
            () => new AppSettings { LyricsStudioWordTimings = true }, _ => { }, drafts);
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.ToString().Contains("Noctis.UI/Assets/Styles.axaml") == true))
        {
            if (!app.Resources.ContainsKey("InterSemiBold"))
                app.Resources["InterSemiBold"] = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml"),
            });
        }
    }

    private sealed class NullEngine : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = new(Path.Combine(Path.GetTempPath(), "noctis-null-models"));
        public IDisposable OpenSession(WhisperModelSize model) => new MemoryStream();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
