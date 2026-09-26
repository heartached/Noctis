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
using Status = Noctis.ViewModels.LyricsStudioViewModel.StudioStatus;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio create-from-scratch (09-23): songs with no lyrics stop at "Needs lyrics"
/// instead of being transcribed, pasted lyrics are aligned, an experimental transcript lands
/// in the lyrics box for correcting, and Save asks before replacing a lyrics file.
/// </summary>
public class LyricsStudioComposeTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "noctis-studio-compose-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioComposeTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private Track T(string title) => new() { Title = title, Artist = "A", FilePath = Path.Combine(_tmp, title + ".mp3") };

    private LyricsStudioViewModel Studio(FakeEngine engine, params Track[] tracks) =>
        new(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });

    private static readonly RecognizedWord[] Heard =
    {
        new("hello", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.4), 0.9f),
        new("world", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2), 0.9f),
        new("good", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.3), 0.9f),
        new("night", TimeSpan.FromSeconds(4.4), TimeSpan.FromSeconds(5), 0.9f),
    };

    [AvaloniaFact]
    public async Task BatchRun_SongWithNoLyrics_StopsAtNeedsLyrics_AndShowsTheLyricsBox()
    {
        var engine = new FakeEngine(_tmp) { Handler = (_, _) => throw new LyricsStudioNeedsLyricsException() };
        var vm = Studio(engine, T("silent"));

        await vm.StartCommand.ExecuteAsync(null);

        var item = vm.Queue[0];
        Assert.Equal(Status.NeedsLyrics, item.Status);
        Assert.Equal("Needs lyrics", item.StateText);
        Assert.False(engine.Options[0].AllowTranscription, "a batch run must not guess lyrics by ear");
        vm.Selected = item;
        Assert.True(vm.IsComposing);
        Assert.False(vm.HasReview);
    }

    [AvaloniaFact]
    public async Task AlignDraft_TimesThePastedLyrics_AndOpensTheReview()
    {
        var engine = new FakeEngine(_tmp)
        {
            Handler = (track, o) => new LyricsStudioResult(track,
                o.SourceLines!.Select((l, i) => new AlignedLine(l, TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds(i * 3 + 2), Array.Empty<AlignedWord>(), 0.8, false)).ToList(),
                LyricsStudioSource.ExistingLyrics, 0.8, "en", 4, Heard),
        };
        var vm = Studio(engine, T("song"));
        vm.Selected = vm.Queue[0];
        Assert.True(vm.IsComposing);
        Assert.False(vm.CanAlignDraft);

        vm.Selected!.DraftText = "[00:01.00]hello world\n\n[ar:Someone]\ngood night";
        Assert.True(vm.CanAlignDraft);
        await vm.AlignDraftCommand.ExecuteAsync(null);

        var options = Assert.Single(engine.Options);
        Assert.Equal(new[] { "hello world", "good night" }, options.SourceLines); // timestamps and tags dropped
        Assert.Null(options.SourceLineStarts);
        Assert.False(options.ForceTranscription);
        Assert.Equal(Status.Ready, vm.Queue[0].Status);
        Assert.Equal(LyricsStudioSource.PastedLyrics, vm.Queue[0].Result!.Source);
        Assert.True(vm.HasReview);
        Assert.Equal(2, vm.ReviewLines.Count);
    }

    [AvaloniaFact]
    public async Task Transcribe_PutsTheTranscriptInTheBox_ThenAlignReusesWhatWasHeard()
    {
        var engine = new FakeEngine(_tmp)
        {
            Handler = (track, o) =>
            {
                Assert.True(o.ForceTranscription);
                return new LyricsStudioResult(track, TranscriptLines.Group(Heard), LyricsStudioSource.Transcription, 0.5, "en", Heard.Length, Heard);
            },
        };
        var vm = Studio(engine, T("song"));
        vm.Selected = vm.Queue[0];

        await vm.TranscribeDraftCommand.ExecuteAsync(null);

        var item = vm.Queue[0];
        Assert.Equal(Status.NeedsLyrics, item.Status);
        Assert.True(item.IsTranscriptDraft);
        Assert.Equal("Transcript", item.StateText);
        Assert.Contains("hello", item.DraftText);
        Assert.True(vm.IsComposing);

        // The user fixes the words; Align times them against the heard words, no second listen.
        item.DraftText = "hello world\ngood night";
        await vm.AlignDraftCommand.ExecuteAsync(null);

        Assert.Single(engine.Options);
        Assert.Equal(Status.Ready, item.Status);
        Assert.False(item.IsTranscriptDraft);
        Assert.Equal(LyricsStudioSource.PastedLyrics, item.Result!.Source);
        Assert.Equal(new[] { "hello world", "good night" }, vm.ReviewLines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(1), vm.ReviewLines[0].Start);
        Assert.False(vm.TranscribeOnly, "a one-song transcription must not flip the Studio-wide option");
    }

    [AvaloniaFact]
    public async Task Save_AsksBeforeReplacingAnExistingLyricsFile_AndDecliningWritesNothing()
    {
        var track = T("song");
        var lrc = Path.ChangeExtension(track.FilePath!, ".lrc");
        File.WriteAllText(lrc, "[00:01.00]mine");
        var engine = new FakeEngine(_tmp)
        {
            Handler = (t, _) => new LyricsStudioResult(t,
                new[] { new AlignedLine("mine", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), Array.Empty<AlignedWord>(), 0.9, false) },
                LyricsStudioSource.ExistingLyrics, 0.9, "en", 1),
        };
        var vm = Studio(engine, track);
        var asked = new List<string>();
        vm.Confirm = message => { asked.Add(message); return Task.FromResult(false); };
        vm.Selected = vm.Queue[0];
        Assert.True(vm.HasReview, "the .lrc loads for review");

        await vm.SaveCommand.ExecuteAsync(null);

        var prompt = Assert.Single(asked);
        Assert.Contains("song.lrc", prompt);
        Assert.Equal("[00:01.00]mine", File.ReadAllText(lrc));
        Assert.Equal(Status.Loaded, vm.Queue[0].Status);
    }

    [AvaloniaFact]
    public async Task Save_TrashRefused_KeepsTheUsersLrc_SaysSo_AndTrashesOffTheUiThread()
    {
        var track = T("song");
        var lrc = Path.ChangeExtension(track.FilePath!, ".lrc");
        File.WriteAllText(lrc, "[00:01.00]mine");
        bool? trashedOnUiThread = null;
        var writer = new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_tmp, "registry.json")), Path.Combine(_tmp, "cache"))
        {
            TrashFile = _ => { trashedOnUiThread = Dispatcher.UIThread.CheckAccess(); return false; },
        };
        var vm = new LyricsStudioViewModel(new[] { track }, new FakeEngine(_tmp), writer, new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.Selected = vm.Queue[0];
        Assert.True(vm.HasReview, "the .lrc loads for review");
        vm.NudgeEarlierCommand.Execute(null);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(trashedOnUiThread ?? true, "the trash must run off the UI thread");
        Assert.Equal("[00:01.00]mine", File.ReadAllText(lrc));
        Assert.Contains("old .lrc kept", vm.Queue[0].StatusText);
        Assert.DoesNotContain("moved to the recycle bin", vm.Queue[0].StatusText);
    }

    [AvaloniaFact]
    public async Task Save_WithEmbedTagsOn_AsksBeforeReplacingTheLyricsInTheTags()
    {
        var track = T("tagged");
        track.Lyrics = "[00:01.00]in the tags";
        var engine = new FakeEngine(_tmp);
        var vm = Studio(engine, track);
        vm.EmbedTags = true;
        var asked = new List<string>();
        vm.Confirm = message => { asked.Add(message); return Task.FromResult(false); };
        vm.Selected = vm.Queue[0];
        Assert.True(vm.HasReview, "timed lyrics in the tags load for review");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("audio file's tags", Assert.Single(asked));
        Assert.Equal("[00:01.00]in the tags", track.Lyrics);
    }

    [AvaloniaFact]
    public void ShiftAllLines_Earlier_StopsAtZero_WithoutSqueezingTheGaps()
    {
        var track = T("early");
        File.WriteAllText(Path.ChangeExtension(track.FilePath!, ".lrc"), "[00:00.05]one\n[00:01.00]two");
        var vm = Studio(new FakeEngine(_tmp), track);
        vm.Selected = vm.Queue[0];

        vm.NudgeEarlierCommand.Execute(null);

        Assert.Equal(TimeSpan.Zero, vm.ReviewLines[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(950), vm.ReviewLines[1].Start);
    }

    [AvaloniaFact]
    public void FormatPill_HidesWhenEveryUnrunSongSharesIt()
    {
        var engine = new FakeEngine(_tmp);
        var vm = Studio(engine, T("one"), T("two"));

        Assert.Equal("· all No lyrics", vm.QueueFormatText);
        Assert.All(vm.Queue, i => Assert.False(i.ShowStatePill));

        vm.Queue[0].Status = Status.NeedsLyrics;
        Assert.True(vm.Queue[0].ShowStatePill, "work state always shows");
    }

    [AvaloniaFact]
    public void FormatPill_OnlyMarksTheSongsThatDifferFromTheCommonFormat()
    {
        var withLrc = T("lrc");
        File.WriteAllText(Path.ChangeExtension(withLrc.FilePath!, ".lrc"), "[00:01.00]line");
        var vm = Studio(new FakeEngine(_tmp), T("one"), T("two"), withLrc);

        Assert.Equal("· No lyrics unless marked", vm.QueueFormatText);
        Assert.False(vm.Queue[0].ShowStatePill);
        Assert.False(vm.Queue[1].ShowStatePill);
        Assert.True(vm.Queue[2].ShowStatePill);
        Assert.Equal("LRC", vm.Queue[2].StateText);
    }

    private sealed class FakeEngine : ILyricsStudioEngine
    {
        public List<LyricsStudioOptions> Options { get; } = new();
        public Func<Track, LyricsStudioOptions, LyricsStudioResult> Handler { get; set; } =
            (t, _) => new LyricsStudioResult(t, Array.Empty<AlignedLine>(), LyricsStudioSource.ExistingLyrics, 0.9, "en", 0);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }

        public FakeEngine(string root)
        {
            Models = new WhisperModelManager(root);
            // IsInstalled wants a file at least 90% of the published size: a sparse file will do.
            var path = Models.PathFor(WhisperModelSize.Base);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var f = new FileStream(path, FileMode.Create);
            f.SetLength(WhisperModelManager.Info(WhisperModelSize.Base).ApproxBytes);
        }

        public IDisposable OpenSession(WhisperModelSize model) => new Handle();

        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            Options.Add(options);
            return Task.FromResult(Handler(track, options));
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
