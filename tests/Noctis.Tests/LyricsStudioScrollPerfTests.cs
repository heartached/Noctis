using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
            // Scrolled at once, before any idle time: every row is built as it comes into view.
            var noIdle = ScrollThrough(win, Scroller(panel, vm), panel, record: false);
            Report("first scroll with no idle time before it (rows built in the scroll)", noIdle);
            vm.Selected = vm.Queue[0];
            var scroller = Scroller(panel, vm);
            Settle(win);
            Assert.Equal(0, scroller.Offset.Y);

            // Left alone a moment (reading the song): the rest are built ahead, a few per idle tick.
            var list = (Noctis.Controls.KeptRowsItemsControl)scroller.GetVisualDescendants().OfType<ItemsControl>().First();
            list.PrebuildQuietMs = 0; // the test's ticks come back to back; the app waits 200 ms after a scroll
            var ticks = 0;
            var longestTick = 0.0;
            var tickTimes = new List<double>();
            var idleMs = 0.0;
            while (list.IsPrebuilding && ticks < 500)
            {
                var tick = Stopwatch.StartNew();
                list.PrebuildTick(); // the timer's tick, run here instead of waiting on the headless timer
                win.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                // UI-thread work only: in the app nothing changes on screen while rows are built off it
                // (the test's software render of the whole window is drawn after the loop).
                longestTick = Math.Max(longestTick, tick.Elapsed.TotalMilliseconds);
                tickTimes.Add(tick.Elapsed.TotalMilliseconds);
                idleMs += tick.Elapsed.TotalMilliseconds;
                ticks++;
            }
            if (HeadlessTestApp.RealRendering) win.CaptureRenderedFrame();
            _out.WriteLine("idle ticks (ms): " + string.Join(" ", tickTimes.Select(t => t.ToString("0"))));
            _out.WriteLine($"built ahead in {ticks} idle ticks, {idleMs:0} ms in all, longest tick {longestTick:0.0} ms, median {tickTimes.OrderBy(t => t).ElementAt(tickTimes.Count / 2):0.0} ms");
            var vsp = (VirtualizingStackPanel)list.ItemsPanelRoot!;
            // Only the rows in view are shown, and the first song's rows left the panel with it.
            Assert.Equal(Lines, vsp.Children.Count);
            Assert.True(vsp.Children.Count(c => c.IsVisible) < Lines / 2);
            Assert.False(list.IsPrebuilding);
            Assert.Equal(0, scroller.Offset.Y); // building ahead never scrolls
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

            // 11-26 before: every line coming into view was torn down and built again, ~45 ms a step
            // in every pass. 11-26 after (rows kept): back and forth ~1 ms, the first pass still
            // built each row in the scroll (24 ms a step stub, 31 ms Skia). Now (11-27) the rows are
            // built ahead while the list is left alone, so the first pass costs what a later one does.
            foreach (var (name, pass) in new[] { ("first", first), ("back up", up), ("down again", again) })
            {
                Assert.True(pass.Avg < 16, $"{name}: average step {pass.Avg:0.0} ms");
                Assert.True(pass.Worst < 33, $"{name}: worst step {pass.Worst:0.0} ms");
            }
            Assert.Equal(0, up.RowsBuilt);
            Assert.Equal(0, again.RowsBuilt);
            // A tick builds one row or so: about a frame each, so input waits at most that long.
            var median = tickTimes.OrderBy(t => t).ElementAt(tickTimes.Count / 2);
            Assert.True(median < 25, $"median idle tick {median:0.0} ms");
        }
        finally { win.Close(); }
    }

    /// <summary>A row's text is plain text until clicked or tabbed into; then a text box takes its
    /// place with the caret where it was clicked, at the same size, and typing edits the line.</summary>
    [AvaloniaFact]
    public void RowText_BecomesATextBoxInPlace_OnClickOrFocus_WithoutMovingAnything()
    {
        EnsureAppResources();
        var vm = StudioWithRestoredReviews(1);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1400, Height = 900, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            vm.Selected = vm.Queue[0];
            Settle(win);
            var texts = panel.GetVisualDescendants().OfType<Noctis.Controls.EditOnDemandText>().Where(t => t.IsEffectivelyVisible).ToList();
            Assert.All(texts, t => Assert.Null(t.Editor));
            Assert.Empty(panel.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("line-edit")));

            // Click in the middle of the first row's text.
            var first = texts[0];
            var row = first.FindAncestorOfType<Border>(includeSelf: false)!;
            while (!row.Classes.Contains("review-line")) row = row.FindAncestorOfType<Border>()!;
            var rowHeight = row.Bounds.Height;
            var textHeight = first.Bounds.Height;
            var textTop = first.TranslatePoint(new Point(0, 0), win)!.Value;
            var click = textTop + new Point(first.Bounds.Width * 0.3, first.Bounds.Height / 2);
            win.MouseDown(click, MouseButton.Left);
            win.MouseUp(click, MouseButton.Left);
            Settle(win);
            var editor = Assert.IsType<TextBox>(first.Editor);
            Assert.True(editor.IsFocused);
            Assert.InRange(editor.CaretIndex, 1, (editor.Text?.Length ?? 0) - 1);
            Assert.Equal(textHeight, first.Bounds.Height);
            Assert.Equal(rowHeight, row.Bounds.Height);
            Assert.Same(vm.ReviewLines[0], vm.SelectedLine); // the row is picked, as a click in its text box did

            // Tabbed into: the next row's text box takes the focus, caret at the end.
            var second = texts[1];
            second.Focus(NavigationMethod.Tab);
            Settle(win);
            Assert.NotNull(second.Editor);
            Assert.True(second.Editor!.IsFocused);
            Assert.Equal(second.Editor.Text?.Length, second.Editor.CaretIndex);
        }
        finally { win.Close(); }
    }

    /// <summary>Owner 11-26: a song picked after scrolling another opened at the old offset (often its end).</summary>
    [AvaloniaFact]
    public void ANewlyPickedSong_OpensAtItsFirstLine_EvenOneSeenBefore()
    {
        EnsureAppResources();
        var vm = StudioWithRestoredReviews(2);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1400, Height = 900, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            vm.Selected = vm.Queue[0];
            Settle(win);
            var scroller = Scroller(panel, vm);
            scroller.Offset = new Vector(0, 2000);
            Settle(win);
            Assert.True(scroller.Offset.Y > 1000);

            vm.Selected = vm.Queue[1];
            Settle(win);
            Assert.Equal(0, scroller.Offset.Y);
            Assert.Contains(Rows(panel), r => ReferenceEquals(r.DataContext, vm.ReviewLines[0]));

            scroller.Offset = new Vector(0, 1500);
            Settle(win);
            vm.Selected = vm.Queue[0]; // back to the first song
            Settle(win);
            Assert.Equal(0, scroller.Offset.Y);
        }
        finally { win.Close(); }
    }

    /// <summary>The same song's lines refreshed (a shift, a save) leave the view where it was.</summary>
    [AvaloniaFact]
    public async Task TheSameSong_RefreshedOrSaved_KeepsItsScrollPosition()
    {
        EnsureAppResources();
        var vm = StudioWithRestoredReviews(1);
        vm.Confirm = _ => Task.FromResult(true);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1400, Height = 900, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            vm.Selected = vm.Queue[0];
            Settle(win);
            var scroller = Scroller(panel, vm);
            scroller.Offset = new Vector(0, 1200);
            Settle(win);
            var kept = scroller.Offset.Y;
            Assert.True(kept > 1000);

            vm.NudgeLaterCommand.Execute(null); // shift every line
            Settle(win);
            Assert.Equal(kept, scroller.Offset.Y);

            await vm.SaveCommand.ExecuteAsync(null);
            Settle(win);
            Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);
            Assert.Equal(kept, scroller.Offset.Y);
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
