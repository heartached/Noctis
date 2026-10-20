using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;
using Status = Noctis.ViewModels.LyricsStudioViewModel.StudioStatus;

namespace Noctis.Tests;

/// <summary>
/// The experimental transcription paths of the Studio (10-19 bug hunt): "Listen instead" and
/// "Transcribe instead" put what Lullaby heard in the lyrics box, Align re-times the corrected
/// text against it, and "Transcribe instead of using lyrics" does it for a whole batch.
/// </summary>
public class LyricsStudioTranscribeFlowTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "noctis-studio-transcribe-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioTranscribeFlowTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private Track T(string title) => new() { Title = title, Artist = "A", FilePath = Path.Combine(_tmp, title + ".mp3") };

    private LyricsStudioViewModel Studio(FakeEngine engine, params Track[] tracks) =>
        new(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });

    private static RecognizedWord W(string text, double start, double end) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), 0.9f);

    /// <summary>Transcribes when asked to (what it heard, grouped as the engine does); else times the lines it is given.</summary>
    private static FakeEngine Transcriber(string root, IReadOnlyList<RecognizedWord> heard) => new(root)
    {
        Handler = (track, o) => o.ForceTranscription
            ? new LyricsStudioResult(track, TranscriptLines.Group(heard), LyricsStudioSource.Transcription, 0.5, "en", heard.Count, heard)
            : new LyricsStudioResult(track,
                (o.SourceLines ?? new[] { "lrclib match" }).Select((l, i) => new AlignedLine(l, TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds(i * 3 + 2), Array.Empty<AlignedWord>(), 0.8, false)).ToList(),
                LyricsStudioSource.ExistingLyrics, 0.8, "en", 2),
    };

    [AvaloniaFact]
    public async Task AlignDraft_AnotherSongSelectedWhileAligning_KeepsThatSongsReviewOnScreen()
    {
        // A long transcript, so the align (off the UI thread) is still running when the user moves on.
        var heard = Enumerable.Range(0, 1500).Select(i => W("w" + i, i * 0.3, i * 0.3 + 0.25)).ToArray();
        var transcribed = T("transcribed");
        var other = T("other");
        File.WriteAllText(Path.ChangeExtension(other.FilePath!, ".lrc"), "[00:01.00]other song first\n[00:04.00]other song second");
        var vm = Studio(Transcriber(_tmp, heard), transcribed, other);
        vm.Selected = vm.Queue[0];
        await vm.TranscribeDraftCommand.ExecuteAsync(null);
        var item = vm.Queue[0];
        Assert.True(item.IsTranscriptDraft);
        item.DraftText = string.Join('\n', Enumerable.Range(0, 300).Select(l => string.Join(' ', Enumerable.Range(l * 5, 5).Select(i => "w" + i))));

        var aligning = vm.AlignDraftCommand.ExecuteAsync(null);
        vm.Selected = vm.Queue[1];
        await aligning;

        Assert.Same(vm.Queue[1], vm.Selected);
        Assert.Equal(new[] { "other song first", "other song second" }, vm.ReviewLines.Select(l => l.Text));
        Assert.Equal(Status.Ready, item.Status);
        Assert.Equal(300, item.Result!.Lines.Count);
        // Save writes the song on screen: it wrote the transcribed song's lines into this one's .lrc.
        await vm.SaveCommand.ExecuteAsync(null);
        var saved = File.ReadAllText(Path.ChangeExtension(other.FilePath!, ".lrc"));
        Assert.Contains("other song first", saved);
        Assert.DoesNotContain("w0", saved);
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
            Models = StudioTestModel.Installed(root);
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
