using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

/// <summary>The review's nudge bar on screen: its steps, tooltips with their keys, and the keys themselves.</summary>
public class LyricsStudioNudgeBarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-nudgebar-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioNudgeBarTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

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

    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
        }
    }

    private (Window win, LyricsStudioPanel panel, LyricsStudioViewModel vm) Mount()
    {
        EnsureAppResources();
        var path = Path.Combine(_root, "song.mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:05.00]First line here\n[00:12.50]Second line here\n");
        var track = new Track { Title = "song", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
        var vm = new LyricsStudioViewModel(new[] { track }, new NoRunEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), null,
            () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1200, Height = 800, Content = panel };
        win.Show();
        vm.Selected = vm.Queue[0];
        Settle(win);
        return (win, panel, vm);
    }

    private static Grid Bar(LyricsStudioPanel panel) =>
        panel.GetVisualDescendants().OfType<Grid>().Single(w => w.Classes.Contains("review-nudge"));

    private static void Press(Window win, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        win.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = win });

    [AvaloniaFact]
    public void TheBar_HasFourStepsWithTheirKeys_AndIsOffUntilALineIsPicked()
    {
        var (win, panel, vm) = Mount();
        try
        {
            var bar = Bar(panel);
            Assert.True(bar.IsEffectivelyVisible);
            var steps = bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nudge-step")).ToList();
            Assert.Equal(new[] { "-100", "-10", "10", "100" }, steps.Select(b => (string)b.CommandParameter!));
            Assert.All(steps, b => Assert.Same(vm.NudgeSelectionCommand, b.Command));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeCoarseEarlier"), ToolTip.GetTip(steps[0]));
            Assert.Contains(",", (string)ToolTip.GetTip(steps[0])!);
            // Screen readers read the glyph ("−"/"+") without a name; each step says what it does.
            Assert.All(steps, b => Assert.Equal(ToolTip.GetTip(b), Avalonia.Automation.AutomationProperties.GetName(b)));
            Assert.All(steps, b => Assert.False(b.IsEffectivelyEnabled));
            Assert.All(steps, b => Assert.False(b.IsEffectivelyVisible)); // only the hint shows
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeNoTarget"), vm.NudgeTargetText);

            vm.PlayFromLineCommand.Execute(vm.ReviewLines[1]);
            Settle(win);
            Assert.All(steps, b => Assert.True(b.IsEffectivelyEnabled));
            Assert.All(steps, b => Assert.True(b.IsEffectivelyVisible));
            var row = panel.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("review-line")).ElementAt(1);
            Assert.Contains("selected", row.Classes);

            steps[3].Command!.Execute(steps[3].CommandParameter);
            Assert.Equal(TimeSpan.FromSeconds(12.6), vm.ReviewLines[1].Start);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void CommaAndPeriod_NudgeThePickedLine_ShiftForTheLargeStep_ButNotWhileTyping()
    {
        var (win, panel, vm) = Mount();
        try
        {
            Press(win, Key.OemPeriod);
            Assert.Equal(TimeSpan.FromSeconds(5), vm.ReviewLines[0].Start); // nothing picked: nothing moves

            vm.PlayFromLineCommand.Execute(vm.ReviewLines[0]);
            Press(win, Key.OemPeriod);
            Press(win, Key.OemPeriod, KeyModifiers.Shift);
            Press(win, Key.OemComma);
            Assert.Equal(TimeSpan.FromSeconds(5.1), vm.ReviewLines[0].Start);

            var box = panel.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("line-edit"));
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemComma, Source = box });
            Assert.Equal(TimeSpan.FromSeconds(5.1), vm.ReviewLines[0].Start);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void WordChips_AreNamedByTheirWordAndTime_ForScreenReaders()
    {
        var (win, panel, vm) = Mount();
        try
        {
            vm.ToggleWordsCommand.Execute(vm.ReviewLines[0]);
            Settle(win);
            var chips = panel.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("word-chip")).ToList();
            Assert.Equal(vm.ReviewLines[0].Words.Count, chips.Count);
            // A chip is a button of two text blocks: without a name UIA read it as a blank button.
            var first = vm.ReviewLines[0].Words[0];
            Assert.Equal($"First, {first.TimeText}", UiaName(chips[0]));

            // The name follows a nudge of the word.
            vm.SelectWordCommand.Execute(first);
            vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(100));
            Settle(win);
            Assert.Equal($"First, {first.TimeText}", UiaName(chips[0]));
            Assert.Contains("05.10", first.TimeText);
        }
        finally { win.Close(); }
    }

    // ── GitHub #237: a nudge stopped by a neighbour says why ────────────────

    /// <summary>Two words 10 ms apart (35.38 / 35.39), the squeeze a lagging tap left behind.</summary>
    private const string SqueezedElrc = "[00:20.00]<00:20.00>Before <00:21.00>line<00:22.00>\n[00:35.38]<00:35.38>Hello <00:35.39>world<00:36.00>\n";

    private (Window win, LyricsStudioPanel panel, LyricsStudioViewModel vm) MountSqueezed()
    {
        File.WriteAllText(Path.Combine(_root, "song.elrc"), SqueezedElrc);
        var mounted = Mount();
        Assert.Equal(TimeSpan.FromMilliseconds(35390), mounted.vm.ReviewLines[1].Words[1].Start);
        return mounted;
    }

    private static string NoRoomNextWord => Noctis.Localization.Loc.T("LyricsStudio.NudgeNoRoom",
        Noctis.Localization.Loc.T("LyricsStudio.NudgeLimitNextWord", "00:35.39")) + " " + Noctis.Localization.Loc.T("LyricsStudio.NudgeAdviceWord");

    private static TextBlock Hint(LyricsStudioPanel panel) =>
        panel.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("nudge-hint"));

    [AvaloniaFact]
    public void BlockedNudge_SaysWhatIsInTheWay_AndANewPickOrAFullNudgeClearsIt()
    {
        var (win, _, vm) = MountSqueezed();
        try
        {
            var line = vm.ReviewLines[1];
            vm.SelectWordCommand.Execute(line.Words[0]);
            vm.NudgeStart = true;
            Assert.Equal("", vm.NudgeHintText);

            Assert.False(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(10)));
            Assert.Equal(NoRoomNextWord, vm.NudgeHintText);
            Assert.Contains("00:35.39", vm.NudgeHintText);

            // Another edge is a new question.
            vm.NudgeWhole = true;
            Assert.Equal("", vm.NudgeHintText);

            // Blocked again, then a nudge that moves the full step clears it.
            vm.NudgeStart = true;
            vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(10));
            Assert.NotEqual("", vm.NudgeHintText);
            Assert.True(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(-10)));
            Assert.Equal("", vm.NudgeHintText);

            // Stopped short again (35.37 → 35.38, not 35.47), then another word picked.
            Assert.True(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeMovedAsFar",
                Noctis.Localization.Loc.T("LyricsStudio.NudgeLimitNextWord", "00:35.39")), vm.NudgeHintText);
            vm.SelectWordCommand.Execute(line.Words[1]);
            Assert.Equal("", vm.NudgeHintText);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void PartialNudge_SaysItMovedAsFarAsItCould()
    {
        var (win, _, vm) = MountSqueezed();
        try
        {
            var line = vm.ReviewLines[1];
            vm.SelectWordCommand.Execute(line.Words[1]);
            vm.NudgeStart = true;
            // Hand-squeezed against the line end: 35.95 → +0.1 stops at 35.99 (10 ms before 36.00).
            Assert.True(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(560)));
            Assert.Equal("", vm.NudgeHintText);
            Assert.True(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(TimeSpan.FromMilliseconds(35990), line.Words[1].Start);
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeMovedAsFar",
                Noctis.Localization.Loc.T("LyricsStudio.NudgeLimitLineEnd", "00:36.00")), vm.NudgeHintText);

            // A line held by the line before, at the line level.
            vm.SelectedWord = null;
            Assert.Equal("", vm.NudgeHintText);
            vm.NudgeWhole = true;
            Assert.True(vm.NudgeSelectionBy(TimeSpan.FromSeconds(-15.37)));
            Assert.False(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(-10)));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeNoRoom",
                Noctis.Localization.Loc.T("LyricsStudio.NudgeLimitPreviousLine", "00:20.00")) + " " + Noctis.Localization.Loc.T("LyricsStudio.NudgeAdviceLine"), vm.NudgeHintText);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void PressingAStepOnASqueezedWord_ShowsTheHint_WithoutMovingTheRow()
    {
        var (win, panel, vm) = MountSqueezed();
        try
        {
            vm.ReviewLines[1].IsExpanded = true;
            vm.SelectWordCommand.Execute(vm.ReviewLines[1].Words[0]);
            vm.NudgeStart = true;
            Settle(win);
            var bar = Bar(panel);
            var steps = bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nudge-step")).ToList();
            var hint = Hint(panel);
            Assert.True(hint.IsEffectivelyVisible);
            Assert.Equal("", hint.Text);
            Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(hint));
            var height = bar.Bounds.Height;
            var places = steps.Select(b => b.TranslatePoint(default, win)!.Value).ToList();

            // A real click on +0.01. The step's hover wash (Styles.axaml) is its hit area once the
            // pointer is over it; without the app's brush the press would fall through to the pill.
            if (!Application.Current!.Resources.ContainsKey("PillFieldBackgroundPointerOver"))
                Application.Current.Resources["PillFieldBackgroundPointerOver"] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#26FFFFFF"));
            var step = steps[2];
            var p = step.TranslatePoint(new Point(step.Bounds.Width / 2, step.Bounds.Height / 2), win)!.Value;
            win.MouseMove(p);
            win.MouseDown(p, MouseButton.Left);
            win.MouseUp(p, MouseButton.Left);
            Settle(win);

            Assert.Equal(TimeSpan.FromMilliseconds(35380), vm.ReviewLines[1].Words[0].Start);
            Assert.Equal(NoRoomNextWord, vm.NudgeHintText);
            Assert.Equal(NoRoomNextWord, hint.Text);
            Assert.Equal(height, bar.Bounds.Height);
            Assert.Equal(places, steps.Select(b => b.TranslatePoint(default, win)!.Value).ToList());
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void TheKeys_ShowTheSameHint()
    {
        var (win, panel, vm) = MountSqueezed();
        try
        {
            vm.SelectWordCommand.Execute(vm.ReviewLines[1].Words[0]);
            vm.NudgeStart = true;
            Press(win, Key.OemPeriod);
            Assert.Equal(NoRoomNextWord, vm.NudgeHintText);
            vm.NudgeWhole = true;
            vm.SelectWordCommand.Execute(vm.ReviewLines[1].Words[1]);
            vm.NudgeStart = true;
            Press(win, Key.OemComma, KeyModifiers.Shift);
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeNoRoom",
                Noctis.Localization.Loc.T("LyricsStudio.NudgeLimitPreviousWord", "00:35.38")) + " " + Noctis.Localization.Loc.T("LyricsStudio.NudgeAdviceWord"), vm.NudgeHintText);
        }
        finally { win.Close(); }
    }

    /// <summary>What UIA reads: a button with panel content and no name reads as the panel's type name.</summary>
    private static string? UiaName(Control c) => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName();

    [AvaloniaFact]
    public void NoButtonInTheReview_ReadsAsATypeName()
    {
        var (win, panel, vm) = Mount();
        try
        {
            // Preview, the live pane's toggle and its idle Play show only when wired: wire them.
            vm.ShowOnLyricsPage = (_, _) => Task.CompletedTask;
            vm.UpdateLivePreview = (_, _) => { };
            vm.ShowLivePreview = true;
            vm.ToggleWordsCommand.Execute(vm.ReviewLines[0]);
            Settle(win);
            var bad = panel.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)
                .Select(b => (b, name: UiaName(b) ?? ""))
                .Where(x => x.name.StartsWith("Avalonia.", StringComparison.Ordinal) || x.name.StartsWith("Noctis.", StringComparison.Ordinal))
                .Select(x => string.Join(".", x.b.Classes) + "=" + x.name).ToList();
            Assert.True(bad.Count == 0, "Buttons read as a type name: " + string.Join("; ", bad));
        }
        finally { win.Close(); }
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
