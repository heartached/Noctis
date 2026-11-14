using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// The review's nudge bar after the 11-14 clean-up (owner: "looks awful"): one row, the label
/// trimming on the left, the controls right-aligned and still as the label changes, one
/// [−0.1 −0.01 +0.01 +0.1] stepper, only the hint while nothing is picked (in a row that keeps
/// its height), and the speed pill in the header beside Preview at its neighbours' height.
/// With NOCTIS_TEST_SKIA=1 the probe also saves PNGs (NOCTIS_RENDER_OUT or D:\NoctisWT\art-1114\studiobar).
/// </summary>
public class LyricsStudioNudgeBarLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-nudgelayout-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioNudgeBarLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (Window win, LyricsStudioPanel panel, LyricsStudioViewModel vm, PlayerViewModel player) Mount(double width, bool livePreview = false, bool appStyles = false, bool elrc = true)
    {
        EnsureAppResources(appStyles);
        var path = Path.Combine(_root, "song.mp3");
        // Word timings (ELRC) like the owner's screenshot: no "Time every word" in the header.
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), elrc
            ? "[00:02.00]<00:02.00>one <00:02.80>two <00:03.92>three <00:04.40>four <00:05.20>five <00:06.00>six\n"
              + "[00:08.00]<00:08.00>A <00:08.50>much <00:09.00>longer <00:09.50>line <00:10.00>of <00:10.50>lyrics <00:11.00>that <00:11.50>keeps <00:12.00>on <00:12.50>going <00:13.00>and <00:13.20>going <00:13.50>for <00:13.70>the <00:13.90>label\n"
              + "[00:14.00]<00:14.00>seven <00:14.80>eight <00:15.60>nine\n"
            : "[00:02.00]one two three four five six\n[00:08.00]A much longer line of lyrics that keeps on going and going for the label\n[00:14.00]seven eight nine\n");
        var track = new Track { Title = "Studio Test", Artist = "T1114", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsStudioViewModel(new[] { track }, new NoRunEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), player,
            () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.ShowOnLyricsPage = (_, _) => Task.CompletedTask;
        vm.UpdateLivePreview = (_, _) => { };
        vm.ShowLivePreview = livePreview;
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        Control content = panel;
        var win = new Window { Width = width, Height = 720 };
        if (appStyles)
        {
            content = new Border { CornerRadius = new CornerRadius(20), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#252525")), Child = panel };
            win.RequestedThemeVariant = ThemeVariant.Dark;
            win.Background = new SolidColorBrush(Color.Parse("#161616"));
            var accent = new SolidColorBrush(Color.Parse("#E74856"));
            win.Resources["AccentColorBrush"] = accent;
            win.Resources["AccentButtonBackground"] = accent;
        }
        win.Content = content;
        win.Show();
        vm.Selected = vm.Queue[0];
        Settle(win);
        return (win, panel, vm, player);
    }

    private static Grid Bar(Control root) => root.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("review-nudge"));

    private static WrapPanel Controls(Grid bar) => bar.GetVisualDescendants().OfType<WrapPanel>().Single(s => s.Classes.Contains("nudge-controls"));

    private static Rect BoundsIn(Visual v, Visual root) =>
        new(v.TranslatePoint(new Point(0, 0), root)!.Value, v.Bounds.Size);

    [AvaloniaTheory]
    [InlineData(1746)] // the Studio page at 1920 wide
    [InlineData(1192)] // at 1366 wide
    [InlineData(786)]  // 1920 at Interface Size 200%
    public void ThePickedLine_ShowsOneRow_RightAligned_ThatHoldsStillAndKeepsItsHeight(double width)
    {
        // The stub renderer's glyphs are wider than Inter's: the tight width only means something with real text.
        if (width < 1000 && !HeadlessTestApp.RealRendering) return;
        var (win, panel, vm, _) = Mount(width);
        try
        {
            var bar = Bar(panel);
            var controls = Controls(bar);
            var label = bar.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("nudge-label"));
            Assert.False(controls.IsVisible); // nothing picked: only the hint
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeNoTarget"), label.Text);
            var idleHeight = bar.Bounds.Height;

            vm.PlayFromLineCommand.Execute(vm.ReviewLines[0]);
            Settle(win);
            Assert.True(controls.IsEffectivelyVisible);
            Assert.Equal(idleHeight, bar.Bounds.Height, 1);
            var at = BoundsIn(controls, bar);
            Assert.Equal(bar.Bounds.Width, at.Right, 1); // right-aligned
            var lab = BoundsIn(label, bar);
            Assert.False(lab.Intersects(at.Deflate(0.5)), $"label {lab} runs under the controls {at}");

            // Every control on one line, one height, in the bar.
            var parts = controls.Children.Cast<Control>().ToList();
            Assert.Equal(3, parts.Count);
            Assert.All(parts, p => Assert.Equal(30, p.Bounds.Height, 1));
            Assert.All(parts, p => Assert.Equal(parts[0].Bounds.Top, p.Bounds.Top, 1));
            for (var i = 1; i < parts.Count; i++) Assert.Equal(8, parts[i].Bounds.Left - parts[i - 1].Bounds.Right, 1);

            // The stepper: four equal steps, earlier to later.
            var steps = controls.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nudge-step")).ToList();
            Assert.Equal(new[] { "-100", "-10", "10", "100" }, steps.Select(b => (string)b.CommandParameter!));
            Assert.All(steps, s => Assert.Equal(steps[0].Bounds.Width, s.Bounds.Width, 1));

            // A long line, then a word: the label changes, the controls do not move.
            vm.PlayFromLineCommand.Execute(vm.ReviewLines[1]);
            Settle(win);
            Assert.Equal(at, BoundsIn(controls, bar));
            vm.ToggleWordsCommand.Execute(vm.ReviewLines[0]);
            vm.SelectedWord = vm.ReviewLines[0].Words[2];
            Settle(win);
            Assert.Equal(at, BoundsIn(controls, bar));
            Assert.Equal(idleHeight, bar.Bounds.Height, 1);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void AtTheWindowsMinimum_TheLabelGoesAbove_AndNoControlRunsOffTheBar()
    {
        var (win, panel, vm, _) = Mount(606); // the 780 px window minimum, sidebar and margins off
        try
        {
            var bar = Bar(panel);
            var controls = Controls(bar);
            vm.PlayFromLineCommand.Execute(vm.ReviewLines[1]);
            Settle(win);
            var label = BoundsIn(bar.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("nudge-label")), bar);
            var parts = controls.Children.Cast<Control>().Select(p => BoundsIn(p, bar)).ToList();
            Assert.All(parts, p => Assert.True(p.Top >= label.Bottom, $"{p} is not below the label {label}"));
            Assert.All(parts, p => Assert.True(p.Right <= bar.Bounds.Width + 0.5, $"{p} runs off the bar ({bar.Bounds.Width})"));
        }
        finally { win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1746)]
    [InlineData(1192)]
    public void TheSpeedPill_SitsBetweenPreviewAndSave_AtTheirHeight_AndItsValueGoesBackTo1x(double width)
    {
        var (win, panel, vm, player) = Mount(width);
        try
        {
            var actions = panel.GetVisualDescendants().OfType<WrapPanel>().Single(w => w.Classes.Contains("review-actions"));
            var pill = actions.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("studio-speed"));
            Assert.DoesNotContain(Bar(panel).GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("studio-speed"));
            var preview = actions.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Command, vm.PreviewOnLyricsPageCommand));
            var save = actions.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Command, vm.SaveCommand));
            var p = BoundsIn(preview, actions); var s = BoundsIn(pill, actions); var v = BoundsIn(save, actions);
            Assert.True(p.Right < s.Left && s.Right < v.Left, $"order: preview {p}, speed {s}, save {v}");
            Assert.Equal(p.Center.Y, s.Center.Y, 1);
            Assert.Equal(save.Bounds.Height, s.Height, 1);

            var value = pill.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("speed-value"));
            vm.SlowerCommand.Execute(null);
            vm.SlowerCommand.Execute(null);
            Settle(win);
            Assert.Contains(value.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "0.9×");
            value.Command!.Execute(value.CommandParameter);
            Assert.Equal(100, player.PlaybackRatePercent);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Probe_RenderTheBarToPng()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering
        var env = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        var outDir = string.IsNullOrWhiteSpace(env) ? @"D:\NoctisWT\art-1114\studiobar" : env;
        Directory.CreateDirectory(outDir);
        foreach (var (width, preview, elrc) in new[] { (1746.0, true, true), (1192.0, true, true), (1192.0, false, true), (1192.0, true, false), (786.0, false, true), (606.0, false, true) })
        {
            var (win, _, vm, _) = Mount(width, preview, appStyles: true, elrc: elrc);
            try
            {
                var tag = $"{width:0}{(preview ? "-preview" : "")}{(elrc ? "" : "-lrc")}";
                Pump(500);
                win.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{tag}-idle.png"));
                vm.ToggleWordsCommand.Execute(vm.ReviewLines[0]);
                vm.SelectedWord = vm.ReviewLines[0].Words[2];
                vm.NudgeStart = true;
                vm.SlowerCommand.Execute(null);
                Pump(500);
                win.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{tag}-word.png"));
            }
            finally { win.Close(); }
        }
    }

    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(8);
        }
    }

    private static void EnsureAppResources(bool appStyles)
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
            });
        }
        if (appStyles && !app.Resources.TryGetResource("PillFieldBackground", ThemeVariant.Dark, out _))
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
        }
    }

    private sealed class NoRunEngine : ILyricsStudioEngine
    {
        public NoRunEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => throw new InvalidOperationException("no runs here");
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, System.Threading.CancellationToken ct) =>
            throw new InvalidOperationException("no runs here");
    }
}
