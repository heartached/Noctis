using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;
using Status = Noctis.ViewModels.LyricsStudioViewModel.StudioStatus;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26 follow-up to the Save freeze: the Studio's loose ends. A tag write that failed was
/// only logged; Stop left lrc.red's model running; selecting a song read its lyrics (for lyrics in
/// the tags, the audio file) on the UI thread; a replaced Studio's tick ran on through a download.
/// </summary>
public class LyricsStudioStabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-stability-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public LyricsStudioStabilityTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string Lrc = "[00:05.00]First line here\n[00:12.50]Second line here\n";

    private Track Song(string name, string? lrc = null)
    {
        var track = new Track { Title = name, Artist = "A", Duration = TimeSpan.FromMinutes(3), FilePath = Path.Combine(_root, name + ".flac") };
        if (lrc != null) File.WriteAllText(Path.ChangeExtension(track.FilePath, ".lrc"), lrc);
        return track;
    }

    private LyricsWriter Writer(IMetadataService metadata, IDeferredTagWriter? tags) =>
        new(metadata, tags, new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json")), Path.Combine(_root, "cache"))
        {
            TrashFile = p => { File.Delete(p); return true; },
            SaveTarget = () => LyricsSaveTarget.Sidecar,
        };

    private LyricsStudioViewModel Studio(IReadOnlyList<Track> tracks, LyricsWriter? writer = null, AppSettings? settings = null) =>
        new(tracks, new NoRunEngine(_root), writer ?? Writer(new Metadata(true), null), new FakeLibraryService(), null,
            () => settings ?? new AppSettings { LyricsStudioWordTimings = true }, _ => { })
        {
            AutoTick = false,
            Confirm = _ => Task.FromResult(true),
        };

    private static void ReadyToSave(LyricsStudioViewModel.StudioItem item)
    {
        var lines = new[] { "First line", "Second line" }.Select((text, i) => new AlignedLine(text, TimeSpan.FromSeconds(5 + i * 7), TimeSpan.FromSeconds(6.5 + i * 7),
            text.Split(' ').Select((w, k) => new AlignedWord(w, TimeSpan.FromSeconds(5 + i * 7 + k * 0.5), TimeSpan.FromSeconds(5.4 + i * 7 + k * 0.5))).ToList(), 0.9, false)).ToList();
        item.Result = new LyricsStudioResult(item.Track, lines, LyricsStudioSource.ExistingLyrics, 0.9, "en", 4);
        item.Status = Status.Ready;
    }

    // ── 1. A failed tag write is told, once it finally fails ──

    [AvaloniaFact]
    public async Task ATagWriteThatFails_IsToldOnTheSongAndInANotice_OnlyWhenItRuns_AndTheLyricsFileStays()
    {
        var tags = new CapturingTagWriter();
        var settings = new AppSettings { LyricsStudioWordTimings = true, LyricsStudioEmbedTags = true };
        var vm = Studio(new[] { Song("tagfail") }, Writer(new Metadata(false), tags), settings);
        var notices = new List<string>();
        vm.ShowNotice = notices.Add;
        var item = vm.Queue[0];
        ReadyToSave(item);
        vm.Selected = item;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Status.Saved, item.Status);
        Assert.StartsWith("Saved", item.StatusText);
        // Deferred (the song may be playing): nothing is said until the write has run.
        Assert.Single(tags.Pending);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(notices);

        await Task.Run(() => { try { tags.Pending[0](); } catch { } });
        Dispatcher.UIThread.RunJobs();

        var message = Localization.Loc.T("LyricsStudio.TagWriteFailed", item.Title);
        Assert.Equal(new[] { message }, notices);
        Assert.Equal(message, item.StatusText);
        Assert.Equal(Status.Saved, item.Status);
        Assert.True(File.Exists(Path.ChangeExtension(item.Track.FilePath, ".elrc")));
    }

    [AvaloniaFact]
    public async Task ATagWriteThatThrows_IsToldToo_AndOneThatWorks_SaysNothing()
    {
        foreach (var (metadata, told) in new[] { (new Metadata(true, throws: true), true), (new Metadata(true), false) })
        {
            var tags = new CapturingTagWriter();
            var vm = Studio(new[] { Song("tag" + told) }, Writer(metadata, tags), new AppSettings { LyricsStudioWordTimings = true, LyricsStudioEmbedTags = true });
            var notices = new List<string>();
            vm.ShowNotice = notices.Add;
            ReadyToSave(vm.Queue[0]);
            vm.Selected = vm.Queue[0];
            await vm.SaveCommand.ExecuteAsync(null);
            await Task.Run(() => { try { tags.Pending.Single()(); } catch { } });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(told ? 1 : 0, notices.Count);
        }
    }

    // ── 2. Stop cancels lrc.red's model ──

    [AvaloniaFact]
    public async Task Stop_CancelsARunningLrcRedSync_AndTheSongIsLeftAsItWas()
    {
        var lrcRed = new HangingLrcRed();
        var vm = Studio(new[] { Song("lrcred") }, settings: new AppSettings { LrcRedEnabled = true, LyricsStudioWordTimings = false });
        vm.LrcRed = lrcRed;
        vm.Selected = vm.Queue[0];
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        var item = vm.Queue[0];
        Assert.True(vm.CanLrcRedSync);
        var statusBefore = item.StatusText;

        var sync = vm.SyncWithLrcRedCommand.ExecuteAsync(null);
        await lrcRed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsLrcRedSyncing);
        Assert.True(vm.CanStop);

        vm.StopCommand.Execute(null);
        await sync.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(lrcRed.Token.IsCancellationRequested);
        Assert.False(vm.IsLrcRedSyncing);
        Assert.False(vm.CanStop);
        Assert.Null(item.Result);
        Assert.Equal(Status.Waiting, item.Status);
        Assert.Equal(statusBefore, item.StatusText);
        Assert.False(vm.WordTimings);
        Assert.True(vm.CanLrcRedSync);
    }

    // ── 3. A song's lyrics are read off the UI thread ──

    [AvaloniaFact]
    public async Task SelectingASong_ReadsItsLyricsOffTheUiThread_ShowingThatItIsLoading()
    {
        var vm = Studio(new[] { Song("one", Lrc) });
        vm.LoadLyricsInline = false;
        var item = vm.Queue[0];

        vm.Selected = item;

        Assert.True(item.IsLoadingLyrics);
        Assert.True(vm.IsSelectedLoading);
        Assert.False(vm.IsComposing);
        Assert.False(vm.ShowReviewEmpty);
        Assert.Equal(Status.Waiting, item.Status);
        Assert.Empty(vm.ReviewLines);

        await vm.SelectionLoad;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsSelectedLoading);
        Assert.Equal(Status.Loaded, item.Status);
        Assert.Equal(new[] { "First line here", "Second line here" }, vm.ReviewLines.Select(l => l.Text));
        Assert.True(vm.HasReview);
    }

    [AvaloniaFact]
    public async Task ARead_ThatLandsAfterAnotherSongWasPicked_IsDropped()
    {
        var vm = Studio(new[] { Song("first", Lrc), Song("second", "[00:01.00]Other song\n") });
        vm.LoadLyricsInline = false;
        var (first, second) = (vm.Queue[0], vm.Queue[1]);

        vm.Selected = first;
        var firstLoad = vm.SelectionLoad;
        vm.Selected = second;
        await firstLoad;
        await vm.SelectionLoad;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Status.Waiting, first.Status);
        Assert.Null(first.Result);
        Assert.False(first.IsLoadingLyrics);
        Assert.Equal(Status.Loaded, second.Status);
        Assert.Equal(new[] { "Other song" }, vm.ReviewLines.Select(l => l.Text));

        // Picked again: read again.
        vm.Selected = first;
        await vm.SelectionLoad;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Status.Loaded, first.Status);
    }

    /// <summary>
    /// Lyrics kept in the audio file's tags: the loader reads the file to say where they came from.
    /// Timed here on a FLAC with a big cover, the read inline (as before) and off the UI thread.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectingASongWithLyricsInItsTags_KeepsTheTagReadOffTheUiThread()
    {
        var ffmpeg = Mp3DamageCrunchHarnessTests.FindFfmpeg();
        if (ffmpeg is null) { _out.WriteLine("ffmpeg not found: skipped"); return; }
        var tracks = new List<Track>();
        var cover = Path.Combine(_root, "cover.png");
        Run(ffmpeg, $"-y -f lavfi -i \"nullsrc=s=1600x1600,geq=random(1)*255:128:128\" -frames:v 1 \"{cover}\"");
        for (var i = 0; i < 2; i++)
        {
            var path = Path.Combine(_root, $"tagged{i}.flac");
            Run(ffmpeg, $"-y -f lavfi -i \"sine=frequency=440:duration=20\" -i \"{cover}\" -map 0 -map 1 -c:v png -disposition:v attached_pic -metadata LYRICS=\"[00:05.00]First line here\" \"{path}\"");
            tracks.Add(new Track { Title = "tagged" + i, Artist = "A", Duration = TimeSpan.FromSeconds(20), FilePath = path, SyncedLyrics = "[00:05.00]First line here" });
        }
        var vm = Studio(tracks);

        vm.LoadLyricsInline = true;
        var sw = Stopwatch.StartNew();
        vm.Selected = vm.Queue[0];
        var inlineMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(Status.Loaded, vm.Queue[0].Status);
        Assert.Equal("embedded tags", vm.Queue[0].Existing!.Origin);

        vm.LoadLyricsInline = false;
        sw.Restart();
        vm.Selected = vm.Queue[1];
        var uiMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(Status.Waiting, vm.Queue[1].Status);
        await vm.SelectionLoad;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Status.Loaded, vm.Queue[1].Status);
        Assert.Equal("embedded tags", vm.Queue[1].Existing!.Origin);
        _out.WriteLine($"select a song with lyrics in its tags: read inline {inlineMs:0.0} ms on the UI thread, off-thread {uiMs:0.0} ms");
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    // ── 4. A replaced Studio stops its tick ──

    [AvaloniaFact]
    public void AStudioReplacedOnThePage_StopsItsTick()
    {
        var library = new FakeLibraryService();
        var studios = new List<LyricsStudioViewModel>();
        var page = new LyricsStudioPageViewModel(library, () => true, tracks =>
        {
            var s = new LyricsStudioViewModel(tracks, new NoRunEngine(_root), Writer(new Metadata(true), null), library, null, () => new AppSettings(), _ => { });
            studios.Add(s);
            return s;
        });
        page.UsePicked(new[] { Song("a") }, wordTimings: true);
        var old = studios.Single();
        old.EnsureTicking(); // as while a model download runs
        Assert.True(old.IsTicking);

        page.UsePicked(new[] { Song("b") }, wordTimings: true);

        Assert.Equal(2, studios.Count);
        Assert.NotSame(old, page.Studio);
        Assert.False(old.IsTicking);
        old.EnsureTicking(); // a late download report
        Assert.False(old.IsTicking);
    }

    // ── fakes ──

    private sealed class CapturingTagWriter : IDeferredTagWriter
    {
        public readonly List<Action> Pending = new();
        public void Enqueue(string path, string key, Action write) => Pending.Add(write);
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Func<string?>? InUsePath { get; set; }
        public int PendingCount => Pending.Count;
    }

    private sealed class Metadata(bool ok, bool throws = false) : IMetadataService
    {
        private bool Write() => throws ? throw new IOException("locked") : ok;
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => Write();
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => Write();
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteLyrics(string filePath, string? plain, string? synced, Func<EmbeddedLyricsText, bool>? onlyIf = null) => Write();
    }

    private sealed class HangingLrcRed : ILrcRedLyricsService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;

        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => throw new InvalidOperationException("the Studio only looks up");

        public Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcRedMatch?>(new LrcRedMatch("JPP302200613", "x", "A", null, 180, "line"));

        public async Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default)
        {
            Token = ct;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class NoRunEngine : ILyricsStudioEngine
    {
        public NoRunEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => throw new InvalidOperationException("no runs here");
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("no runs here");
    }
}
