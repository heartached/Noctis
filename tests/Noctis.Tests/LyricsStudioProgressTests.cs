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

/// <summary>
/// Per-song progress (09-29): steps weighted into one bar that never steps back, moves between
/// Whisper's once-a-window reports, and reaches the screen only on the Studio's ~15 Hz tick.
/// </summary>
public class LyricsStudioProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-progress-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioProgressTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // ── Stage weights ──────────────────────────────────────────────────────

    [Fact]
    public void Stages_TileTheBar_InOrder_WithListeningTheBulk()
    {
        var stages = Enum.GetValues<LyricsStudioStage>();
        Assert.Equal(0, LyricsStudioStages.Overall(LyricsStudioStage.FindingLyrics, 0));
        for (var i = 0; i + 1 < stages.Length; i++)
            Assert.Equal(LyricsStudioStages.Overall(stages[i], 1), LyricsStudioStages.Overall(stages[i + 1], 0), 9);
        Assert.Equal(1, LyricsStudioStages.Overall(LyricsStudioStage.Done, 0));
        var listening = LyricsStudioStages.Overall(LyricsStudioStage.Listening, 1) - LyricsStudioStages.Overall(LyricsStudioStage.Listening, 0);
        Assert.True(listening >= 0.8);
        Assert.Equal(0.525, new LyricsStudioProgress(LyricsStudioStage.Listening, 0.5).Fraction, 9);
        Assert.Equal(LyricsStudioStages.Overall(LyricsStudioStage.Decoding, 1), LyricsStudioStages.Overall(LyricsStudioStage.Decoding, 7)); // clamped
    }

    // ── Song meter ─────────────────────────────────────────────────────────

    [Fact]
    public void Meter_IgnoresLateAndLowerReports_SoTheBarNeverStepsBack()
    {
        var m = new SongProgressMeter(S(180));
        m.Report(LyricsStudioStage.Decoding, 0.5, S(1));
        var a = m.Read(S(1)).Overall;
        m.Report(LyricsStudioStage.Decoding, 0.2, S(1.1));        // out of order
        Assert.Equal(a, m.Read(S(1.1)).Overall);
        m.Report(LyricsStudioStage.Listening, 0.0, S(2));
        m.Report(LyricsStudioStage.Decoding, 1.0, S(2.1));        // a late report from the step before
        var (stage, overall) = m.Read(S(2.1));
        Assert.Equal(LyricsStudioStage.Listening, stage);
        Assert.True(overall >= a);
    }

    [Fact]
    public void Meter_MovesBetweenWhisperWindows_ButNeverPassesTheNextReport()
    {
        // A 3-minute song: six 30 s windows, each 1/6 of Listening, each taking 10 s.
        var m = new SongProgressMeter(S(180));
        m.Report(LyricsStudioStage.Listening, 0, S(0));
        var step = 1 / 6.0;
        var first = new List<double>();
        for (var t = 0.0; t <= 10; t += 0.5) first.Add(m.Read(S(t)).Overall);
        Assert.True(first.Zip(first.Skip(1)).All(p => p.Second >= p.First));
        Assert.True(first[^1] > first[0] + 0.05, "the first window eases forward before the first report");
        Assert.True(first[^1] < LyricsStudioStages.Overall(LyricsStudioStage.Listening, step), "but never past it");

        m.Report(LyricsStudioStage.Listening, step, S(10));
        m.Report(LyricsStudioStage.Listening, 2 * step, S(20));
        // Paced by the windows so far (10 s each): halfway through the third window, about half a window on.
        var mid = m.Read(S(25)).Overall;
        Assert.Equal(LyricsStudioStages.Overall(LyricsStudioStage.Listening, 2.5 * step), mid, 2);
        // A slow window: held at 90% of it, not beyond.
        var late = m.Read(S(60)).Overall;
        Assert.Equal(LyricsStudioStages.Overall(LyricsStudioStage.Listening, 2.9 * step), late, 3);
        m.Report(LyricsStudioStage.Listening, 3 * step, S(61));
        Assert.True(m.Read(S(61)).Overall >= late);
    }

    [Fact]
    public void Meter_StaysBelow100_UntilDone()
    {
        var m = new SongProgressMeter(S(60));
        m.Report(LyricsStudioStage.Aligning, 0, S(0));
        Assert.True(m.Read(S(100)).Overall < 1);
        m.Report(LyricsStudioStage.Done, 1, S(101));
        Assert.Equal(1, m.Read(S(101)).Overall);
    }

    // ── Decoder progress ───────────────────────────────────────────────────

    [Fact]
    public async Task DecoderProgress_ClimbsInHalfPercentSteps_AndIsCapped()
    {
        var source = new MemoryStream(new byte[1_000_000]);
        var reports = new List<double>();
        await PcmDecoder16k.CopyWithProgressAsync(source, new MemoryStream(), 800_000, new InlineProgress<double>(reports.Add), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second - p.First >= 0.005));
        Assert.Equal(1, reports[^1]);
        Assert.True(reports.Count <= 201);
    }

    // ── Studio ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task ARun_ShowsTheStepPercentAndBatch_OnlyOnTheTick()
    {
        var engine = new SteppedEngine(_root);
        Track T(string title) => new() { Title = title, Artist = "A", FilePath = Path.Combine(_root, title + ".mp3"), Duration = S(180) };
        var tracks = new[] { T("one"), T("two") };
        foreach (var t in tracks) File.WriteAllText(Path.ChangeExtension(t.FilePath!, ".lrc"), "[00:01.00]a line\n");
        var vm = new LyricsStudioViewModel(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        var now = S(0);
        vm.Clock = () => now;
        vm.AutoTick = false; // the test ticks

        var run = vm.StartCommand.ExecuteAsync(null);
        await Wait(engine.Started);
        var item = vm.Queue[0];
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Working, item.Status);
        Assert.True(vm.ShowBatchProgress);
        Assert.Equal("Song 1 of 2", vm.BatchText);

        engine.Report(new LyricsStudioProgress(LyricsStudioStage.Decoding, 0.5));
        Assert.Equal(0, item.Progress); // nothing on screen between ticks
        vm.Tick();
        Assert.Equal("Decoding · 6%", item.StatusText);
        Assert.Equal("Decoding the audio…", item.StageText);
        Assert.True(item.Steps[0].IsActive);
        Assert.Equal(0.03, vm.BatchProgress, 3);

        engine.Report(new LyricsStudioProgress(LyricsStudioStage.Listening, 0.5));
        vm.Tick();
        Assert.Equal("Listening · 52%", item.StatusText);
        Assert.True(item.Steps[0].IsDone);
        Assert.True(item.Steps[1].IsActive);
        Assert.False(item.Steps[2].IsActive);
        Assert.Equal(0.2625, vm.BatchProgress, 3);

        engine.Report(new LyricsStudioProgress(LyricsStudioStage.Aligning, 0));
        vm.Tick();
        Assert.Equal("Timing · 95%", item.StatusText);
        Assert.True(item.Steps[2].IsActive);

        engine.Finish();
        await Wait(engine.Started); // song two
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, item.Status);
        Assert.Equal("Song 2 of 2", vm.BatchText);
        Assert.True(vm.BatchProgress >= 0.5);
        engine.Report(new LyricsStudioProgress(LyricsStudioStage.Decoding, 1));
        vm.Tick();
        Assert.Equal(0.55, vm.BatchProgress, 3);

        engine.Finish();
        await run;
        Assert.False(vm.ShowBatchProgress);
        Assert.All(vm.Queue, i => Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, i.Status));
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

    /// <summary>Runs each song only as far as the test says: reports on demand, finishes on Finish().</summary>
    private sealed class SteppedEngine : ILyricsStudioEngine
    {
        private TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<LyricsStudioProgress>? _progress;

        public SteppedEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();

        public SemaphoreSlim Started { get; } = new(0);
        public void Report(LyricsStudioProgress p) => _progress!.Report(p);
        public void Finish() => _finish.TrySetResult();

        public async Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            _progress = progress;
            _finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.Release();
            await _finish.Task.WaitAsync(ct);
            var lines = new List<AlignedLine> { new("a line", S(1), S(2), Array.Empty<AlignedWord>(), 0.9, false) };
            return new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.9, "en", 2);
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
