using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Studio flow bugs found reading the flows end to end (09-29).</summary>
public class LyricsStudioFlowBugTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-flow-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioFlowBugTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string TwoLines = "[00:05.00]First line here\n[00:12.50]Second line here\n";

    private Track SongWithLrc(string name)
    {
        var path = Path.Combine(_root, name + ".mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), TwoLines);
        return new Track { Title = name, Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private static LyricsStudioViewModel Studio(ILyricsStudioEngine engine, params Track[] tracks)
    {
        var vm = new LyricsStudioViewModel(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        return vm;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    // ── Stop during a re-run keeps the review ─────────────────────────────

    [AvaloniaFact]
    public async Task StoppingTimeEveryWord_BringsBackTheReview_WithTheUsersEdits()
    {
        var engine = new Engine(_root) { WaitForCancel = true };
        var vm = Studio(engine, SongWithLrc("song"));
        vm.Selected = vm.Queue[0];
        vm.NudgeLaterCommand.Execute(null);
        vm.ReviewLines[0].Text = "First line fixed here";

        var run = vm.UpgradeToWordTimingsCommand.ExecuteAsync(null);
        await Until(() => engine.Started > 0);
        vm.StopCommand.Execute(null);
        await run;

        var item = vm.Queue[0];
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Loaded, item.Status);
        Assert.True(vm.HasReview);
        Assert.False(vm.IsComposing);
        Assert.Equal("First line fixed here", vm.ReviewLines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(5.1), vm.ReviewLines[0].Start);

        // …and it survives leaving the song and coming back (it used to re-read the file).
        vm.Selected = null;
        vm.Selected = item;
        Assert.Equal("First line fixed here", vm.ReviewLines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(5.1), vm.ReviewLines[0].Start);
    }

    [AvaloniaFact]
    public async Task StoppingAReSync_KeepsTheModelResultOnScreen()
    {
        var engine = new Engine(_root);
        var vm = Studio(engine, SongWithLrc("song"));
        await vm.StartCommand.ExecuteAsync(null); // a first run: Ready
        var item = vm.Queue[0];
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, item.Status);
        vm.Selected = item;
        vm.ReviewLines[1].Text = "Second line fixed";

        engine.WaitForCancel = true;
        var resync = vm.StartCommand.ExecuteAsync(null); // "Re-sync"
        await Until(() => engine.Started > 1);
        vm.StopCommand.Execute(null);
        await resync;

        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, item.Status);
        Assert.True(vm.HasReview);
        Assert.Equal("Second line fixed", vm.ReviewLines[1].Text);
    }

    [AvaloniaFact]
    public async Task ARunThatCannotStart_KeepsTheReview()
    {
        var engine = new Engine(_root) { FailToOpen = true };
        var vm = Studio(engine, SongWithLrc("song"));
        vm.Selected = vm.Queue[0];

        await vm.UpgradeToWordTimingsCommand.ExecuteAsync(null);

        Assert.StartsWith("Couldn't start", vm.RunStatusText);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Loaded, vm.Queue[0].Status);
        Assert.True(vm.HasReview);
        Assert.Equal(2, vm.ReviewLines.Count);
    }

    // ── A finished song does not pull the review away mid-edit ────────────

    [AvaloniaFact]
    public async Task AFinishedSong_DoesNotPullAwayALoadedReviewBeingEdited()
    {
        var engine = new Engine(_root);
        var vm = Studio(engine, SongWithLrc("open"), SongWithLrc("queued"));
        vm.Selected = vm.Queue[0]; // shows its .lrc (Loaded); the other song stays queued
        vm.NudgeLaterCommand.Execute(null);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, vm.Queue[1].Status);
        Assert.Same(vm.Queue[0], vm.Selected);
        Assert.Equal(TimeSpan.FromSeconds(5.1), vm.ReviewLines[0].Start);
    }

    [AvaloniaFact]
    public async Task AFinishedSong_DoesNotEndTapMode()
    {
        var engine = new Engine(_root);
        var vm = Studio(engine, SongWithLrc("open"), SongWithLrc("queued"));
        vm.Selected = vm.Queue[0];
        await vm.StartTapCommand.ExecuteAsync(vm.ReviewLines[0]);
        Assert.True(vm.IsTapping);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Same(vm.Queue[0], vm.Selected);
        Assert.True(vm.IsTapping);
    }

    [AvaloniaFact]
    public async Task AFinishedSong_StillOpens_WhenTheSongOnScreenIsOnlyBeingLookedAt()
    {
        var engine = new Engine(_root);
        var vm = Studio(engine, SongWithLrc("open"), SongWithLrc("queued"));
        vm.Selected = vm.Queue[0]; // looked at, not edited

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Same(vm.Queue[1], vm.Selected);
    }

    private sealed class Engine : ILyricsStudioEngine
    {
        private int _started;
        public Engine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public bool WaitForCancel { get; set; }
        public bool FailToOpen { get; init; }
        public int Started => Volatile.Read(ref _started);

        public IDisposable OpenSession(WhisperModelSize model) =>
            FailToOpen ? throw new InvalidOperationException("model could not be loaded") : new Handle();

        public async Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            if (WaitForCancel) await Task.Delay(Timeout.Infinite, ct);
            var src = options.SourceLines ?? new List<string> { "heard" };
            var lines = src.Select((t, i) => new AlignedLine(t, TimeSpan.FromSeconds(20 + i), TimeSpan.FromSeconds(21 + i),
                Array.Empty<AlignedWord>(), 0.8, false)).ToList();
            return new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.8, "en", lines.Count);
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
