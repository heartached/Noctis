using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
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
/// Lyrics Studio polish (10-01): the song title on the run progress view, the review header's
/// action hierarchy, the E badge in Choose songs, and the per-line word strip toggle.
/// </summary>
public class LyricsStudioPolishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-polish-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioPolishTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string TwoLines = "[00:05.00]First line here\n[00:12.50]Second line here\n";

    private Track SongWithLrc(string title, string artist = "A Boogie wit da Hoodie", bool isExplicit = true)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), TwoLines);
        return new Track { Title = title, Artist = artist, FilePath = path, Duration = TimeSpan.FromMinutes(3), IsExplicit = isExplicit };
    }

    private static LyricsStudioViewModel Studio(ILyricsStudioEngine engine, params Track[] tracks)
    {
        var vm = new LyricsStudioViewModel(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.PickLyricsFile = () => Task.FromResult<string?>(null);
        return vm;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("ChevronDownThinIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
    }

    private static (Window win, LyricsStudioPanel panel) Mount(LyricsStudioViewModel vm, double width, bool showHeader = true)
    {
        EnsureAppResources();
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = showHeader };
        var win = new Window { Width = width, Height = 800, Content = panel };
        win.Show();
        Settle(win);
        return (win, panel);
    }

    /// <summary>Layout passes plus the LayoutUpdated round trips the title cells take.</summary>
    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
        }
    }

    private static async Task Wait(SemaphoreSlim semaphore)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!semaphore.Wait(0))
        {
            if (DateTime.UtcNow > end) throw new TimeoutException();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    /// <summary>What the title would measure with no cap at all: the width it needs to show whole.</summary>
    private static double NaturalWidth(TextBlock shown)
    {
        var probe = new TextBlock { Text = shown.Text, FontSize = shown.FontSize, FontWeight = shown.FontWeight, FontFamily = shown.FontFamily };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    private static TextBlock WorkingTitle(LyricsStudioPanel panel) =>
        (TextBlock)panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WorkingTitleBox").Child!;

    // ── A: the title on the run progress view ─────────────────────────────

    /// <summary>
    /// "Time every word" from an open review: the progress view showed the E badge and the
    /// artist but no title. Its title cell is centred (sized to its content) and the shared
    /// title-cap handler sized the title from that same cell's width: while the view was hidden
    /// the cell measured 0, the cap went to 0, and a 0-wide title kept the cell 0 for good.
    /// </summary>
    [AvaloniaFact]
    public async Task TimingView_ShowsTheWholeTitle_AfterTimeEveryWordFromAnOpenReview()
    {
        var engine = new GateEngine(_root);
        var vm = Studio(engine, SongWithLrc("Ballin"));
        vm.Selected = vm.Queue[0];
        var (win, panel) = Mount(vm, 1000);
        try
        {
            Assert.True(vm.HasReview);
            var run = vm.UpgradeToWordTimingsCommand.ExecuteAsync(null);
            await Wait(engine.Started);
            Settle(win);

            Assert.True(vm.IsSelectedWorking);
            var title = WorkingTitle(panel);
            Assert.Equal("Ballin", title.Text);
            Assert.True(title.IsEffectivelyVisible);
            Assert.True(title.Bounds.Width >= NaturalWidth(title) - 0.5,
                $"the title is {title.Bounds.Width:F1}px wide (box MaxWidth {((Control)title.Parent!).MaxWidth:F1}, cell {((Control)title.Parent!.Parent!).Bounds.Width:F1}) but needs {NaturalWidth(title):F1}px");
            var badge = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WorkingExplicitBadge");
            Assert.True(badge.IsEffectivelyVisible);
            Assert.True(badge.Bounds.Width > 0);

            engine.Finish();
            await run;
        }
        finally { win.Close(); }
    }

    /// <summary>The next song's longer title gets its own width, not the last song's.</summary>
    [AvaloniaFact]
    public async Task TimingView_GivesALongerTitleItsWidth_WhenTheRunMovesOn()
    {
        var engine = new GateEngine(_root);
        var vm = Studio(engine, SongWithLrc("Ballin"), SongWithLrc("Look Back at It (Remix)", isExplicit: false));
        vm.Selected = null;
        var (win, panel) = Mount(vm, 1000);
        try
        {
            var run = vm.StartCommand.ExecuteAsync(null);
            await Wait(engine.Started);
            vm.Selected = vm.Queue[0];
            Settle(win);
            var title = WorkingTitle(panel);
            Assert.Equal("Ballin", title.Text);
            Assert.True(title.Bounds.Width >= NaturalWidth(title) - 0.5, $"first title {title.Bounds.Width:F1}px of {NaturalWidth(title):F1}px");

            engine.Finish();
            await Wait(engine.Started);
            vm.Selected = vm.Queue[1];
            Settle(win);
            Assert.Equal("Look Back at It (Remix)", title.Text);
            Assert.True(title.Bounds.Width >= NaturalWidth(title) - 0.5, $"second title {title.Bounds.Width:F1}px of {NaturalWidth(title):F1}px");

            engine.Finish();
            await run;
        }
        finally { win.Close(); }
    }

    // ── B: the review header's actions ───────────────────────────────────

    /// <summary>A review open on a line-level song (so "Time every word" shows), Preview on as on the page.</summary>
    private (Window win, LyricsStudioPanel panel, LyricsStudioViewModel vm) MountReview(double width, bool preview = true, bool showHeader = false)
    {
        var vm = Studio(new GateEngine(_root), SongWithLrc("Ballin"));
        if (preview) vm.ShowOnLyricsPage = (_, _) => Task.CompletedTask;
        var (win, panel) = Mount(vm, width, showHeader);
        vm.Selected = vm.Queue[0];
        Settle(win);
        Assert.True(vm.HasReview);
        Assert.True(vm.ReviewCanUpgrade);
        return (win, panel, vm);
    }

    /// <summary>The review's top block: title, actions, source and timing tools, tap bar.</summary>
    private static StackPanel ReviewTop(LyricsStudioPanel panel) =>
        (StackPanel)panel.GetVisualDescendants().OfType<WrapPanel>().Single(w => w.Classes.Contains("review-actions"))
            .GetVisualAncestors().OfType<StackPanel>().First();

    /// <summary>The rounded review pane that clips everything in it.</summary>
    private static Border ReviewPane(LyricsStudioPanel panel) =>
        ReviewTop(panel).GetVisualAncestors().OfType<Border>().First(b => b.ClipToBounds);

    /// <summary>
    /// Every action and tool in the review header stays whole inside the review pane, at the
    /// dialog's width and every page width down to the main window's minimum with the sidebar
    /// pinned, whether the window opened at that width or was narrowed to it.
    /// </summary>
    [AvaloniaFact]
    public void ReviewHeader_NeverClipsAButton_AtAnyWidth()
    {
        var (win, panel, _) = MountReview(1700);
        try
        {
            // Real fonts: down to the page at the main window's minimum with the sidebar pinned (a
            // 182px pane). The stub fonts are about twice as wide, so their floor sits higher.
            for (var width = 1700; width >= (HeadlessTestApp.RealRendering ? 500 : 580); width -= 40)
            {
                win.Width = width;
                Settle(win);
                var pane = ReviewPane(panel);
                foreach (var c in ReviewTop(panel).GetVisualDescendants().OfType<Control>()
                             .Where(c => c is Button or TextBlock && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                {
                    var right = c.TranslatePoint(new Point(c.Bounds.Width, 0), pane)!.Value.X;
                    Assert.True(right <= pane.Bounds.Width + 0.5,
                        $"{c.GetType().Name} '{(c as TextBlock)?.Text ?? (c as Button)?.Content?.ToString()}' ends at {right:F1} of a {pane.Bounds.Width:F1}px pane (window {width})");
                }
            }
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// One accent action: Save. Time every word and Preview are plain pills, and Skip and
    /// Transcribe instead are in the "…" menu with their commands and tooltip, not on the row.
    /// </summary>
    [AvaloniaFact]
    public void ReviewHeader_SaveIsTheOnlyAccentAction_SkipAndTranscribeAreInTheMoreMenu()
    {
        var (win, panel, vm) = MountReview(1700);
        try
        {
            var top = ReviewTop(panel);
            var buttons = top.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            var accent = Assert.Single(buttons, b => b.Classes.Contains("accent-btn"));
            Assert.Same(vm.SaveCommand, accent.Command);
            var upgrade = Assert.Single(buttons, b => ReferenceEquals(b.Command, vm.UpgradeToWordTimingsCommand));
            Assert.Contains("pill-outline", upgrade.Classes);
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.UpgradeTip"), ToolTip.GetTip(upgrade));
            Assert.Single(buttons, b => ReferenceEquals(b.Command, vm.PreviewOnLyricsPageCommand));
            Assert.DoesNotContain(buttons, b => ReferenceEquals(b.Command, vm.SkipCommand) || ReferenceEquals(b.Command, vm.RedoAsTranscriptionCommand));

            var more = Assert.Single(buttons, b => b.Classes.Contains("studio-more"));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.MoreTip"), ToolTip.GetTip(more));
            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            menu.ShowAt(more);
            Settle(win);
            var items = menu.Items.OfType<MenuItem>().ToList();
            var skip = Assert.Single(items, i => ReferenceEquals(i.Command, vm.SkipCommand));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.Skip"), skip.Header);
            var redo = Assert.Single(items, i => ReferenceEquals(i.Command, vm.RedoAsTranscriptionCommand));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.Redo"), redo.Header);
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.RedoTip"), ToolTip.GetTip(redo));
            Assert.True(redo.IsVisible); // the review is not a transcription
            menu.Hide();
        }
        finally { win.Close(); }
    }

    /// <summary>Shift all lines is one [− 0.1s +] pill: each step keeps its tooltip and moves every line.</summary>
    [AvaloniaFact]
    public void ShiftAllLines_IsOneSegmentedPill_WhoseStepsMoveEveryLine()
    {
        var (win, panel, vm) = MountReview(1700);
        try
        {
            var pill = ReviewTop(panel).GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("nudge-pill"));
            var steps = pill.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(2, steps.Count);
            Assert.Same(vm.NudgeEarlierCommand, steps[0].Command);
            Assert.Same(vm.NudgeLaterCommand, steps[1].Command);
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeEarlier"), ToolTip.GetTip(steps[0]));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeLater"), ToolTip.GetTip(steps[1]));

            var start = vm.ReviewLines.Select(l => l.Start).ToList();
            steps[1].Command!.Execute(null);
            Assert.Equal(start.Select(s => s + TimeSpan.FromMilliseconds(100)), vm.ReviewLines.Select(l => l.Start));
            steps[0].Command!.Execute(null);
            Assert.Equal(start, vm.ReviewLines.Select(l => l.Start));
        }
        finally { win.Close(); }
    }

    // ── D: the per-line words toggle and the word strip ──────────────────

    private static Border Row(LyricsStudioPanel panel, int index) =>
        panel.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("review-line")).ElementAt(index);

    private static Button WordToggle(Border row) => row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("word-toggle"));

    private static Noctis.Controls.CollapsibleContent Strip(Border row) =>
        row.GetVisualDescendants().OfType<Noctis.Controls.CollapsibleContent>().Single();

    private static int RealizedChips(Control root) =>
        root.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("word-chip"));

    /// <summary>Transitions run off the wall clock: frames have to be spaced in real time.</summary>
    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }

    /// <summary>
    /// The toggle sits right after the time pill, before the line's text, as a pill-sized target
    /// (it was a 10px chevron at the far end of a wide row), and still marks a word-timed line.
    /// </summary>
    [AvaloniaFact]
    public void WordToggle_SitsBetweenTheTimeAndTheText_AsAPillSizedTarget()
    {
        var (win, panel, vm) = MountReview(1300);
        try
        {
            vm.ReviewLines[1].HasWordTimings = true;
            Settle(win);
            var row = Row(panel, 0);
            var time = row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("time-pill"));
            var text = row.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("line-edit"));
            var toggle = WordToggle(row);
            Point Left(Control c) => c.TranslatePoint(new Point(0, 0), row)!.Value;

            Assert.True(Left(toggle).X >= Left(time).X + time.Bounds.Width, "the toggle follows the time pill");
            Assert.True(Left(toggle).X + toggle.Bounds.Width <= Left(text).X, "the toggle comes before the line's text");
            Assert.True(Left(text).X - (Left(time).X + time.Bounds.Width) < 60, "the text stays close to the time");
            Assert.True(toggle.Bounds.Width >= 32 && toggle.Bounds.Height >= 20, $"toggle is {toggle.Bounds.Size}");
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.WordsTip"), ToolTip.GetTip(toggle));

            Ellipse Dot(int i) => WordToggle(Row(panel, i)).GetVisualDescendants().OfType<Ellipse>().Single(e => e.Classes.Contains("words-dot"));
            Assert.False(Dot(0).IsVisible); // line-level timings only
            Assert.True(Dot(1).IsVisible);  // this line has its words timed
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// The word strip folds open and shut (CollapsibleContent, not a snap), the chevron turns
    /// 180° with it and back, and the row ends at its shut height with no leftover gap.
    /// </summary>
    [AvaloniaFact]
    public void WordStrip_FoldsOpenAndShut_AndTheChevronTurns()
    {
        var (win, panel, vm) = MountReview(1300);
        try
        {
            Pump(4); // the fold arms its transition once loaded
            var row = Row(panel, 0);
            var shutHeight = row.Bounds.Height;
            var toggle = WordToggle(row);
            var chevron = toggle.GetVisualDescendants().OfType<PathIcon>().Single(p => p.Classes.Contains("words-chevron"));
            var strip = Strip(row);
            double Turn() => chevron.RenderTransform!.Value.M11; // cos(angle): 1 at rest, −1 turned
            Assert.False(strip.IsVisible);
            Assert.Equal(1, Turn(), 3);

            toggle.Command!.Execute(toggle.CommandParameter);
            Assert.True(vm.ReviewLines[0].IsExpanded);
            Assert.Contains("open", toggle.Classes);
            Pump(3);
            Assert.InRange(strip.Reveal, 0.001, 0.999); // gliding, not snapped open
            Pump(30);
            Settle(win);
            Assert.Equal(1, strip.Reveal, 2);
            Assert.Equal(-1, Turn(), 2);
            Assert.True(row.Bounds.Height > shutHeight + 20, "the strip opened under the line");
            Assert.True(RealizedChips(row) > 0);

            toggle.Command!.Execute(toggle.CommandParameter);
            Pump(3);
            Assert.InRange(strip.Reveal, 0.001, 0.999);
            Pump(30);
            Settle(win);
            Assert.Equal(0, strip.Reveal, 2);
            Assert.False(strip.IsVisible);
            Assert.Equal(1, Turn(), 2);
            Assert.Equal(shutHeight, row.Bounds.Height, 1);
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// The fold's last frame must not jump: the strip's gap lives inside its folding body, so the
    /// row is the same height one hair before shut as when shut (a panel Spacing gap snapped).
    /// </summary>
    [AvaloniaFact]
    public void WordStrip_LastFoldFrame_DoesNotJump()
    {
        var (win, panel, vm) = MountReview(1300);
        try
        {
            var row = Row(panel, 0);
            var strip = Strip(row);
            var shut = row.Bounds.Height;
            vm.ReviewLines[0].IsExpanded = true;
            Settle(win);
            strip.Reveal = 0.001; // the frame before the fold lands
            Settle(win);
            Assert.True(row.Bounds.Height - shut <= 1.0, $"the row jumps {row.Bounds.Height - shut:F1}px as the fold lands");
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// A long song's review builds no word chips until a line is opened, and then only that
    /// line's: a shut strip is never measured.
    /// </summary>
    [AvaloniaFact]
    public void ShutWordStrips_BuildNoChips_UntilALineOpens()
    {
        var path = Path.Combine(_root, "long.mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"),
            string.Concat(Enumerable.Range(0, 80).Select(i => $"[{i / 60:00}:{i % 60:00}.00]line number {i} goes here\n")));
        var vm = Studio(new GateEngine(_root), new Track { Title = "Long", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) });
        var (win, panel) = Mount(vm, 1300, showHeader: false);
        try
        {
            vm.Selected = vm.Queue[0];
            Settle(win);
            Assert.Equal(80, vm.ReviewLines.Count);
            Assert.Equal(0, RealizedChips(panel));

            vm.ToggleWordsCommand.Execute(vm.ReviewLines[3]);
            Settle(win);
            Assert.Equal(vm.ReviewLines[3].Words.Count, RealizedChips(panel));
        }
        finally { win.Close(); }
    }

    // ── C: the E badge in Choose songs ───────────────────────────────────

    /// <summary>
    /// Choose songs shows the E badge right after an explicit song's title and an album with an
    /// explicit song, none on a clean song, and a long title trims so its badge stays in the row.
    /// </summary>
    [AvaloniaFact]
    public async Task Picker_ShowsTheEBadge_AfterExplicitTitles_AndKeepsItOnLongOnes()
    {
        EnsureAppResources();
        var lib = new FakeLibraryService();
        var albumId = Guid.NewGuid();
        Track T(string title, bool isExplicit) => new()
        {
            Title = title, Artist = "A Boogie wit da Hoodie", Album = "Hoodie SZN", AlbumId = albumId,
            IsExplicit = isExplicit, FilePath = $@"C:\m\{Guid.NewGuid():N}.mp3",
        };
        var ballin = T("Ballin", true);
        var longOne = T("Look Back at It (A Very Long Title That Has To Trim Before The Badge)", true);
        var clean = T("Clean", false);
        lib.TrackList.AddRange(new[] { ballin, longOne, clean });
        ((List<Album>)lib.Albums).Add(new Album { Id = albumId, Name = "Hoodie SZN", Artist = "A Boogie wit da Hoodie", Tracks = new List<Track> { ballin, longOne, clean } });
        var vm = new LyricsStudioPickerViewModel(lib, wordTimings: true, detectFormats: t => t.Select(_ => LyricsFormat.Lrc).ToList());
        vm.SearchText = "boogie";
        await vm.SearchRefresh;
        vm.FormatScan.Wait(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        var dialog = new LyricsStudioPickerDialog { DataContext = vm, Width = 640, Height = 720 };
        dialog.Show();
        try
        {
            Settle(dialog);
            Grid TitleCell(string title) => dialog.GetVisualDescendants().OfType<Grid>()
                .Single(g => g.Children.OfType<TextBlock>().FirstOrDefault()?.Text == title && g.Children.OfType<Border>().Any(b => b.Classes.Contains("explicit-badge")));
            Border Badge(string title) => TitleCell(title).Children.OfType<Border>().Single();

            Assert.True(Badge("Hoodie SZN").IsEffectivelyVisible);   // the album holds explicit songs
            Assert.True(Badge("Ballin").IsEffectivelyVisible);
            Assert.False(Badge("Clean").IsVisible);
            var ballinTitle = TitleCell("Ballin").Children.OfType<TextBlock>().First();
            Assert.Equal(ballinTitle.Bounds.Right + Badge("Ballin").Margin.Left, Badge("Ballin").Bounds.Left, 1); // right after the title

            // The long title trims and its badge stays inside the row's title column.
            var longCell = TitleCell(longOne.Title);
            var longBadge = Badge(longOne.Title);
            Assert.True(longBadge.IsEffectivelyVisible);
            Assert.True(longBadge.Bounds.Width > 0);
            Assert.True(longBadge.Bounds.Right <= longCell.Bounds.Width + 0.5,
                $"badge ends at {longBadge.Bounds.Right:F1} of a {longCell.Bounds.Width:F1}px title cell");
            var column = (Control)longCell.GetVisualParent()!;
            Assert.True(longCell.Bounds.Width <= column.Bounds.Width + 0.5);
        }
        finally { dialog.Close(); }
    }

    /// <summary>Holds each song until the test says Finish; the lines are the song's own.</summary>
    private sealed class GateEngine(string root) : ILyricsStudioEngine
    {
        private TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = StudioTestModel.Installed(root);
        public SemaphoreSlim Started { get; } = new(0);
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();
        public void Finish() => _finish.TrySetResult();

        public async Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            _finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.Release();
            progress?.Report(new LyricsStudioProgress(LyricsStudioStage.Listening, 0.11));
            await _finish.Task.WaitAsync(ct);
            var lines = (options.SourceLines ?? new[] { "la la la" })
                .Select((t, i) => new AlignedLine(t, TimeSpan.FromSeconds(5 + i * 7), TimeSpan.FromSeconds(9 + i * 7),
                    t.Split(' ').Select((w, j) => new AlignedWord(w, TimeSpan.FromSeconds(5 + i * 7 + j * 0.5), TimeSpan.FromSeconds(5.4 + i * 7 + j * 0.5))).ToList(),
                    0.9, false))
                .ToList();
            return new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.9, "en", lines.Count);
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
