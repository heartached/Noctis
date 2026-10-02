using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
