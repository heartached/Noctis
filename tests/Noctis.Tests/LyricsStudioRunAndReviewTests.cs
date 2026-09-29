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

/// <summary>
/// Found by the 09-24 headless harness run over real songs: batch runs, late progress reports,
/// "Time every word" after edits, the [ ] keys with focus outside the panel, and the review
/// header at a narrow window.
/// </summary>
public class LyricsStudioRunAndReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-run-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioRunAndReviewTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private Track SongWithLrc(string name, string lrc)
    {
        var path = Path.Combine(_root, name + ".mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), lrc);
        return new Track { Title = name, Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private LyricsStudioViewModel Studio(CapturingEngine engine, params Track[] tracks) =>
        new(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });

    private const string TwoLines = "[00:05.00]First line here\n[00:12.50]Second line here\n";

    [AvaloniaFact]
    public async Task Start_OnASongNeverOpened_KeepsItsLrcLineStartsAsAnchors()
    {
        var engine = new CapturingEngine(_root);
        var vm = Studio(engine, SongWithLrc("song", TwoLines));
        Assert.Null(vm.Selected); // a batch run: the song was never clicked, so nothing loaded it

        await vm.StartCommand.ExecuteAsync(null);

        var options = Assert.Single(engine.Options);
        Assert.Equal(new[] { "First line here", "Second line here" }, options.SourceLines);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12.5) }, options.SourceLineStarts);
    }

    [AvaloniaFact]
    public async Task LateProgressReports_DoNotOverwriteTheSongsOutcome()
    {
        var engine = new CapturingEngine(_root) { ReportThenFinish = true };
        var none = new Track { Title = "none", Artist = "A", FilePath = Path.Combine(_root, "none.mp3") };
        var timed = SongWithLrc("timed", TwoLines);
        var vm = Studio(engine, none, timed);

        await vm.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }

        Assert.Equal(LyricsStudioViewModel.StudioStatus.NeedsLyrics, vm.Queue[0].Status);
        Assert.Equal("No lyrics found · paste or import them", vm.Queue[0].StatusText);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, vm.Queue[1].Status);
        Assert.Equal("80% matched · review", vm.Queue[1].StatusText);
    }

    [AvaloniaFact]
    public async Task TimeEveryWord_TimesTheLinesOnScreen_WithTheUsersEditsAndShift()
    {
        var engine = new CapturingEngine(_root);
        var vm = Studio(engine, SongWithLrc("song", TwoLines));
        vm.Selected = vm.Queue[0];
        Assert.True(vm.ReviewCanUpgrade);

        vm.NudgeLaterCommand.Execute(null);                // the whole song +0.1 s
        vm.ReviewLines[0].Text = "First line fixed here";  // a typo fixed in the review
        await vm.UpgradeToWordTimingsCommand.ExecuteAsync(null);

        var options = Assert.Single(engine.Options);
        Assert.Equal(new[] { "First line fixed here", "Second line here" }, options.SourceLines);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5.1), TimeSpan.FromSeconds(12.6) }, options.SourceLineStarts);
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

    private (Window win, LyricsStudioPanel panel, LyricsStudioViewModel vm) MountReview(double width)
    {
        EnsureAppResources();
        var vm = Studio(new CapturingEngine(_root), SongWithLrc("song", TwoLines));
        vm.Confirm = _ => Task.FromResult(true);
        vm.PickLyricsFile = () => Task.FromResult<string?>(null);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = width, Height = 800, Content = panel };
        win.Show();
        vm.Selected = vm.Queue[0];
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Assert.True(vm.HasReview);
        return (win, panel, vm);
    }

    [AvaloniaFact]
    public void ShiftKeys_WorkWithFocusOutsideThePanel_ButNotWhileTypingInARow()
    {
        var (win, panel, vm) = MountReview(1400);
        var start = vm.ReviewLines[0].Start;

        win.FocusManager!.Focus(null); // what a click on blank space leaves behind
        win.KeyPress(Key.OemCloseBrackets, RawInputModifiers.None, PhysicalKey.BracketRight, "]");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(start + TimeSpan.FromMilliseconds(100), vm.ReviewLines[0].Start);

        var row = panel.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("line-edit"));
        row.Focus();
        win.KeyPress(Key.OemOpenBrackets, RawInputModifiers.None, PhysicalKey.BracketLeft, "[");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(start + TimeSpan.FromMilliseconds(100), vm.ReviewLines[0].Start);
        win.Close();
    }

    [AvaloniaTheory]
    [InlineData(820, true)]
    [InlineData(1000, true)]
    [InlineData(1700, false)]
    public void ReviewHeader_ToolsAndActions_DropBelow_WhenNarrow_NeverOverTheTitleOrSource(double width, bool expectTwoRows)
    {
        var (win, panel, _) = MountReview(width);
        Rect InGrid(Control c) => new(c.Bounds.Position, c.Bounds.Size);
        foreach (var cls in new[] { "review-tools", "review-actions" })
        {
            var group = panel.GetVisualDescendants().OfType<WrapPanel>().First(s => s.Classes.Contains(cls));
            var grid = (Grid)group.GetVisualParent()!;
            var other = grid.Children.OfType<StackPanel>().First();
            var groupBox = InGrid(group);
            var otherBox = new Rect(other.Bounds.Position, new Size(Math.Min(other.DesiredSize.Width, other.Bounds.Width), other.Bounds.Height));

            if (cls == "review-tools") Assert.Equal(expectTwoRows, groupBox.Top >= otherBox.Bottom - 0.5);
            Assert.False(groupBox.Intersects(otherBox), $"{cls} {groupBox} overlap {otherBox} at {width}px");
            // Every button stays inside the review pane.
            foreach (var b in group.Children.OfType<Button>().Where(b => b.IsVisible))
                Assert.True(b.Bounds.Right <= group.Bounds.Width + 0.5 && group.Bounds.Right <= grid.Bounds.Width + 0.5,
                    $"{cls} button ends at {b.Bounds.Right} of {group.Bounds.Width} (grid {grid.Bounds.Width}) at {width}px");
        }
        win.Close();
    }

    private sealed class CapturingEngine : ILyricsStudioEngine
    {
        public List<LyricsStudioOptions> Options { get; } = new();
        /// <summary>Report a stage, keep the UI thread busy so the report's post lands behind the run's own continuation, then finish.</summary>
        public bool ReportThenFinish { get; init; }
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }

        public CapturingEngine(string root)
        {
            Models = new WhisperModelManager(root);
            var path = Models.PathFor(WhisperModelSize.Base);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) return;
            using var f = new FileStream(path, FileMode.Create);
            f.SetLength(WhisperModelManager.Info(WhisperModelSize.Base).ApproxBytes);
        }

        public IDisposable OpenSession(WhisperModelSize model) => new Handle();

        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            Options.Add(options);
            var lines = options.SourceLines is { } src
                ? src.Select((t, i) => new AlignedLine(t, options.SourceLineStarts?[i] ?? TimeSpan.FromSeconds(i), (options.SourceLineStarts?[i] ?? TimeSpan.FromSeconds(i)) + TimeSpan.FromSeconds(1),
                    Array.Empty<AlignedWord>(), 0.8, false)).ToList()
                : new List<AlignedLine>();
            if (ReportThenFinish)
            {
                // The UI thread is held while the report and the run's continuation queue up behind it.
                Dispatcher.UIThread.Post(() => Thread.Sleep(150));
                Thread.Sleep(20);
                progress?.Report(new LyricsStudioProgress(options.SourceLines is null ? "Finding lyrics" : "Ready", 1));
                if (options.SourceLines is null) throw new LyricsStudioNeedsLyricsException();
            }
            else if (options.SourceLines is null) throw new LyricsStudioNeedsLyricsException();
            return Task.FromResult(new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.8, "en", lines.Count));
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
