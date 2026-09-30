using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
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
    public async Task StoppingTranscribeInstead_KeepsTheReviewsDraft_ForTheNextOpen()
    {
        var engine = new Engine(_root);
        var drafts = new LyricsStudioDraftStore(Path.Combine(_root, "drafts"));
        var song = SongWithLrc("song");
        var vm = new LyricsStudioViewModel(new[] { song }, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null,
            () => new AppSettings(), _ => { }, drafts);
        vm.Confirm = _ => Task.FromResult(true);
        vm.Selected = vm.Queue[0];
        await vm.UpgradeToWordTimingsCommand.ExecuteAsync(null); // a model result: Ready, with a draft
        Assert.True(drafts.TryLoad(song.Id, out _));

        engine.WaitForCancel = true;
        var redo = vm.RedoAsTranscriptionCommand.ExecuteAsync(null);
        await Until(() => engine.Started > 1);
        vm.StopCommand.Execute(null);
        await redo;

        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, vm.Queue[0].Status);
        Assert.True(drafts.TryLoad(song.Id, out _), "the review must still come back after a restart");
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

    // ── A saved song shows what was saved ─────────────────────────────────

    [AvaloniaFact]
    public async Task ASavedSong_ReopensWithWhatWasSaved_NotTheUneditedResult()
    {
        var engine = new Engine(_root);
        var song = SongWithLrc("song");
        var other = new Track { Title = "other", Artist = "A", FilePath = Path.Combine(_root, "other.mp3") };
        var writer = new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json")), Path.Combine(_root, "cache"))
        {
            TrashFile = _ => true,
        };
        var vm = new LyricsStudioViewModel(new[] { song, other }, engine, writer, new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.Selected = vm.Queue[0];
        await vm.UpgradeToWordTimingsCommand.ExecuteAsync(null); // a model result, Ready
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, vm.Queue[0].Status);
        vm.ReviewLines[0].Text = "First line fixed";
        vm.NudgeLaterCommand.Execute(null);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);
        Assert.Contains("First line fixed", File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".lrc")));

        vm.Selected = vm.Queue[1];
        vm.Selected = vm.Queue[0];

        Assert.True(vm.HasReview);
        Assert.Equal("First line fixed", vm.ReviewLines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(20.1), vm.ReviewLines[0].Start);
    }

    // ── Leaving the page keeps the lyrics box ─────────────────────────────

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComingBackToThePage_KeepsLyricsTypedOrTranscribedIntoTheBox(bool transcript)
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Title = "one", Artist = "A", FilePath = Path.Combine(_root, "one.mp3") });
        library.TrackList.Add(new Track { Title = "two", Artist = "A", FilePath = Path.Combine(_root, "two.mp3") });
        var page = new LyricsStudioPageViewModel(library, () => true, tracks => Studio(new Engine(_root), tracks.ToArray()));
        await page.RefreshAsync();
        var studio = page.Studio!;
        studio.Selected = studio.Queue[0]; // no lyrics: the lyrics box
        Assert.True(studio.IsComposing);
        studio.Queue[0].DraftText = "lyrics the user pasted";
        studio.Queue[0].IsTranscriptDraft = transcript;

        library.RaiseLibraryUpdated(); // a scan ran while the user was on another page
        await page.RefreshAsync();     // back on the Lyrics Studio page

        Assert.Same(studio, page.Studio);
        Assert.Equal("lyrics the user pasted", page.Studio!.Queue[0].DraftText);
    }

    [AvaloniaFact]
    public async Task ComingBackToThePage_StillRefreshes_WhenTheBoxOnlyHoldsTheSongsOwnLyrics()
    {
        var library = new FakeLibraryService();
        var withText = new Track { Title = "one", Artist = "A", FilePath = Path.Combine(_root, "one.mp3"), Lyrics = "plain words" };
        library.TrackList.Add(withText);
        var page = new LyricsStudioPageViewModel(library, () => true, tracks => Studio(new Engine(_root), tracks.ToArray()));
        await page.RefreshAsync();
        var studio = page.Studio!;
        studio.Selected = studio.Queue[0]; // prefilled from the plain lyrics tag, not typed
        Assert.Equal("plain words", studio.Queue[0].DraftText);

        library.RaiseLibraryUpdated();
        await page.RefreshAsync();

        Assert.NotSame(studio, page.Studio);
    }

    // ── Loading the model does not freeze the window ──────────────────────

    [AvaloniaFact]
    public async Task TheSpeechModel_LoadsOffTheUiThread_WithTheCardSayingSo()
    {
        // Loading ggml-medium.bin measured 6-46 s here (09-29); on the UI thread the whole app froze.
        var gate = new ManualResetEventSlim();
        var engine = new Engine(_root) { OpenGate = gate };
        var vm = Studio(engine, SongWithLrc("song"));

        var run = vm.StartCommand.ExecuteAsync(null);
        await Until(() => engine.OpenCalls > 0);
        Assert.False(engine.OpenedOnUiThread);
        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Loading, vm.ModelBanner);
        Assert.True(vm.ShowModelBannerBar);
        Assert.False(vm.ShowModelDownload);
        gate.Set();
        await run;

        Assert.Equal(LyricsStudioViewModel.ModelBannerState.Hidden, vm.ModelBanner);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, vm.Queue[0].Status);
    }

    private sealed class Engine : ILyricsStudioEngine
    {
        private int _started;
        private int _openCalls;
        public ManualResetEventSlim? OpenGate { get; init; }
        public int OpenCalls => Volatile.Read(ref _openCalls);
        public bool OpenedOnUiThread { get; private set; }
        public Engine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public bool WaitForCancel { get; set; }
        public bool FailToOpen { get; init; }
        public int Started => Volatile.Read(ref _started);

        public IDisposable OpenSession(WhisperModelSize model)
        {
            OpenedOnUiThread = Dispatcher.UIThread.CheckAccess();
            Interlocked.Increment(ref _openCalls);
            if (FailToOpen) throw new InvalidOperationException("model could not be loaded");
            if (OpenGate is { } gate && !OpenedOnUiThread) gate.Wait(TimeSpan.FromSeconds(10));
            return new Handle();
        }

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
