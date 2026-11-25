using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio on a song that already has word-timed lyrics with a background vocal: select
/// it (the song's own lyrics load), shift it, save, and read back what the lyrics page shows.
/// Made-up lyrics.
/// </summary>
public class LyricsStudioRoundTripTests : IDisposable
{
    // Line 1 word-timed with a "[bg: …]" vocal under it, line 2 word-timed, line 3 plain.
    private const string Elrc =
        "[00:01.00]<00:01.00>Paper <00:01.50>lanterns<00:02.00>\n" +
        "[bg: <00:02.00>(oh <00:02.40>oh)<00:03.00>]\n" +
        "[00:04.00]<00:04.00>Silver <00:04.60>moon<00:05.00>\n" +
        "[00:06.00]Quiet harbor";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-rt-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioRoundTripTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Engine(string root) : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = StudioTestModel.Installed(root);
        public IDisposable OpenSession(WhisperModelSize model) => throw new InvalidOperationException("no model run in these tests");
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
            => throw new InvalidOperationException("no model run in these tests");
    }

    private (LyricsStudioViewModel Vm, Track Song) StudioOn(string lrc, Func<string, bool>? trash = null)
    {
        var path = Path.Combine(_root, "song.mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), lrc);
        var song = new Track { Title = "song", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
        var writer = new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json")), Path.Combine(_root, "cache"))
        {
            TrashFile = trash ?? (_ => true),
        };
        var vm = new LyricsStudioViewModel(new[] { song }, new Engine(_root), writer, new FakeLibraryService(), null, () => new AppSettings(), _ => { })
        {
            Confirm = _ => Task.FromResult(true),
        };
        return (vm, song);
    }

    [AvaloniaFact]
    public async Task ShiftedAndSaved_KeepsTheBackgroundVocal_MovedWithTheSong()
    {
        // The Studio read its lines with ExistingLyricsLoader.ParseTimed, which takes a
        // "[bg: …]" line for a header tag and drops it: a save wrote the song without it.
        var (vm, song) = StudioOn(Elrc);
        vm.WordTimings = true;
        vm.SaveTtml = true;
        vm.Selected = vm.Queue[0];
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Loaded, vm.Queue[0].Status);

        vm.NudgeLaterCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);

        // The word-timed .elrc: the vocal's own word times, moved with the song.
        var lines = LrcParser.Parse(File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".elrc")));
        Assert.Equal(new[] { "Paper lanterns", "Silver moon", "Quiet harbor" }, lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(1.1), lines[0].Timestamp);
        Assert.Equal(new[] { "(oh", "oh)" }, lines[0].BackgroundWords?.Select(w => w.Text.Trim()));
        Assert.Equal(new[] { 2.1, 2.5 }, lines[0].BackgroundWords!.Select(w => w.Start.TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(3.1), lines[0].BackgroundWords![^1].End);

        // The line-level .lrc written beside it keeps the vocal as text under its line.
        var lrc = LrcParser.Parse(File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".lrc")));
        Assert.Equal("(oh oh)", Assert.Single(lrc[0].BackgroundWords!).Text.Trim());

        // The .ttml beside it carries the same vocal as Apple's x-bg span.
        var (ttml, _) = TtmlParser.Parse(File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".ttml")));
        Assert.Equal(new[] { "(oh", "oh)" }, ttml![0].BackgroundWords?.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromSeconds(2.1), ttml[0].BackgroundWords![0].Start);
    }

    /// <summary>Owner 11-24: a word-timed save always wrote both an .elrc and an .lrc; Settings ›
    /// Lyrics files now picks ELRC and LRC (default), ELRC only or LRC only, and the other file goes.</summary>
    [AvaloniaTheory]
    [InlineData(LyricsStudioFiles.ElrcAndLrc, true, true)]
    [InlineData(LyricsStudioFiles.ElrcOnly, true, false)]
    [InlineData(LyricsStudioFiles.LrcOnly, false, true)]
    public async Task LyricsFiles_WritesOnlyTheChosenFiles(LyricsStudioFiles choice, bool elrc, bool lrc)
    {
        var (vm, song) = StudioOn(Elrc, trash: f => { File.Delete(f); return true; });
        // The song already has both, as in the owner's folder.
        File.WriteAllText(Path.ChangeExtension(song.FilePath!, ".elrc"), Elrc);
        vm.WordTimings = true;
        vm.LyricsFilesIndex = (int)choice;
        vm.Selected = vm.Queue[0];
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);

        var elrcPath = Path.ChangeExtension(song.FilePath!, ".elrc");
        var lrcPath = Path.ChangeExtension(song.FilePath!, ".lrc");
        Assert.Equal(elrc, File.Exists(elrcPath));
        Assert.Equal(lrc, File.Exists(lrcPath));
        if (elrc) Assert.Contains("<00:01.50>lanterns", File.ReadAllText(elrcPath));
        // LRC only: line timings, no word timings, in the file and on the song.
        if (choice == LyricsStudioFiles.LrcOnly)
        {
            Assert.DoesNotContain("<00:", File.ReadAllText(lrcPath));
            Assert.DoesNotContain("<00:", song.SyncedLyrics);
        }
    }

    [AvaloniaFact]
    public void LyricsFiles_ChoiceIsSaved_AndReadBack()
    {
        var settings = new AppSettings { LyricsStudioFiles = nameof(LyricsStudioFiles.ElrcOnly) };
        var path = Path.Combine(_root, "song.mp3");
        var song = new Track { Title = "song", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
        var writer = new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json")), Path.Combine(_root, "cache"));
        LyricsStudioPrefs? prefs = null;
        var vm = new LyricsStudioViewModel(new[] { song }, new Engine(_root), writer, new FakeLibraryService(), null, () => settings, p => prefs = p);
        Assert.Equal((int)LyricsStudioFiles.ElrcOnly, vm.LyricsFilesIndex);

        vm.LyricsFilesIndex = (int)LyricsStudioFiles.LrcOnly;
        Assert.Equal(LyricsStudioFiles.LrcOnly, prefs?.Files);
    }

    [AvaloniaFact]
    public async Task Saved_KeepsTheSecondSinger_AndTheirTranslation()
    {
        // The review rows dropped the line's voice and the ELRC/LRC writers never wrote one,
        // so a duet saved from the Studio came back with every line in the first singer's place.
        const string duet =
            "[00:01.00]<00:01.00>Paper <00:01.50>lanterns<00:02.00>\n" +
            "[00:04.00]v2:<00:04.00>Silver <00:04.60>moon<00:05.00>\n" +
            "[00:04.00]v2:Luna de plata\n" +
            "[00:06.00]Quiet harbor";
        var (vm, song) = StudioOn(duet);
        vm.WordTimings = true;
        vm.SaveTtml = true;
        vm.Selected = vm.Queue[0];

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);

        var voices = new[] { LyricVoice.Default, LyricVoice.Voice2, LyricVoice.Default };
        foreach (var ext in new[] { ".elrc", ".lrc" })
        {
            var lines = LrcParser.Parse(File.ReadAllText(Path.ChangeExtension(song.FilePath!, ext)));
            Assert.Equal(new[] { "Paper lanterns", "Silver moon", "Quiet harbor" }, lines.Select(l => l.Text));
            Assert.Equal(voices, lines.Select(l => l.Voice));
            Assert.Equal("Luna de plata", lines[1].Translation);
        }
        var (ttml, _) = TtmlParser.Parse(File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".ttml")));
        Assert.Equal(voices, ttml!.Select(l => l.Voice));
    }

    [AvaloniaFact]
    public async Task CjkCharacters_ShiftedAndSaved_StayTogether()
    {
        // A CJK .elrc timed a character at a time: the review rows joined every word with a
        // space and handed the writers words without JoinsNext, so a save wrote "偶 尔".
        const string cjk =
            "[00:20.51]<00:20.51>偶<00:20.94>尔<00:23.53>\n" +
            "[00:24.44]<00:24.44>我<00:24.63>会 <00:24.79>怀<00:24.99>疑<00:25.14>";
        var (vm, song) = StudioOn(cjk);
        vm.WordTimings = true;
        vm.SaveTtml = true;
        vm.Selected = vm.Queue[0];

        vm.NudgeLaterCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);

        var expected = new[] { "偶尔", "我会 怀疑" };
        foreach (var ext in new[] { ".elrc", ".lrc" })
            Assert.Equal(expected, LrcParser.Parse(File.ReadAllText(Path.ChangeExtension(song.FilePath!, ext))).Select(l => l.Text));
        var (ttml, _) = TtmlParser.Parse(File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".ttml")));
        Assert.Equal(expected, ttml!.Select(l => l.Text));
    }

    [Fact]
    public void LyricsPageTtmlExport_KeepsTheBackgroundVocal()
    {
        // "Save as TTML" on the lyrics page reads the synced text through the same loader.
        var (ttml, _) = TtmlParser.Parse(LyricsViewModel.BuildTtmlFromSynced(Elrc));

        Assert.Equal(new[] { "(oh", "oh)" }, ttml![0].BackgroundWords?.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromSeconds(2.4), ttml[0].BackgroundWords![1].Start);
    }

    [Fact]
    public void ReTimedBySpeech_KeepsTheSecondSinger_AndTheBackgroundVocal_MovedWithTheLine()
    {
        // The speech model times only the sung text, and CarryCompanions gave back only the
        // romaji / translation: a re-timed duet lost its "v2:" lines and its background vocals.
        const string duet =
            "[00:01.00]<00:01.00>Paper <00:01.50>lanterns<00:02.00>\n" +
            "[bg: <00:02.00>(oh <00:02.40>oh)<00:03.00>]\n" +
            "[00:04.00]v2:<00:04.00>Silver <00:04.60>moon<00:05.00>\n" +
            "[00:06.00]Quiet harbor";
        var source = ExistingLyricsLoader.ParseTimed(duet);
        var deltas = new[] { 0.5, -0.2, 0.3 };
        var retimed = source.Select((l, i) =>
        {
            var d = TimeSpan.FromSeconds(deltas[i]);
            return new AlignedLine(l.Text, l.Start + d, l.End + d,
                l.Words.Select(w => new AlignedWord(w.Text, w.Start + d, w.End + d)).ToList(), 0.9, false);
        }).ToList();

        var carried = ExistingLyricsLoader.CarryCompanions(source, retimed);

        Assert.Equal(new[] { LyricVoice.Default, LyricVoice.Voice2, LyricVoice.Default }, carried.Select(l => l.Voice));
        Assert.Equal(new[] { "(oh", "oh)" }, carried[0].Background?.Select(w => w.Text.Trim()));
        Assert.Equal(new[] { 2.5, 2.9 }, carried[0].Background!.Select(w => w.Start.TotalSeconds));
        Assert.Equal(3.5, carried[0].Background![^1].End.TotalSeconds);
        Assert.Null(carried[1].Background);
        Assert.Same(retimed[2], carried[2]);
    }
}
