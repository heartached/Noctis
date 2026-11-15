using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #237: in the Studio, on a word-timed .ttml song playing with the live preview pane
/// open, every tap and nudge froze the window for seconds ("by the time it loads up the second
/// word, the sentence is already over"; the log showed UI.Stall 2-5 s and constant GCs).
/// </summary>
public class LyricsStudioTapLagTests : IDisposable
{
    private const int Lines = 40;
    private const int WordsPerLine = 8;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-taplag-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public LyricsStudioTapLagTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static string Elrc()
    {
        var sb = new System.Text.StringBuilder();
        for (var l = 0; l < Lines; l++)
        {
            var start = TimeSpan.FromSeconds(5 + l * 6);
            sb.Append('[').Append(TimedLyricsBuilder.FormatTimestamp(start)).Append(']');
            for (var w = 0; w < WordsPerLine; w++)
            {
                var t = start + TimeSpan.FromMilliseconds(w * 600);
                sb.Append('<').Append(TimedLyricsBuilder.FormatTimestamp(t)).Append('>').Append("word").Append(w).Append(' ');
            }
            sb.Append('<').Append(TimedLyricsBuilder.FormatTimestamp(start + TimeSpan.FromMilliseconds(WordsPerLine * 600))).Append(">\n");
        }
        return sb.ToString();
    }

    private Track TtmlSong()
    {
        var path = Path.Combine(_root, "song.m4a");
        File.WriteAllText(Path.ChangeExtension(path, ".ttml"), LyricsViewModel.BuildTtmlFromSynced(Elrc()));
        return new Track { Title = "song", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(5) };
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

    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private (LyricsStudioViewModel vm, LyricsViewModel lyrics, FakeAudioPlayer audio, Window win) Mount()
    {
        EnsureAppResources();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), new FakeLibraryService());
        player.Duration = TimeSpan.FromMinutes(5);
        var track = TtmlSong();
        var vm = new LyricsStudioViewModel(new[] { track }, new NoRunEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), player,
            () => new AppSettings { LyricsStudioLivePreview = true, LyricsStudioWordTimings = true }, _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false, PreviewLyrics = lyrics };
        var win = new Window { Width = 1600, Height = 900, Content = panel };
        win.Show();
        vm.Selected = vm.Queue[0];
        player.CurrentTrack = track;
        lyrics.EnsureLyricsForCurrentTrack();
        return (vm, lyrics, audio, win);
    }

    private static async Task WaitForPreview(Window win, LyricsViewModel lyrics)
    {
        var end = Environment.TickCount64 + 5000;
        while (!lyrics.IsPreviewActive && Environment.TickCount64 < end) { Settle(win); await Task.Delay(10); }
        Assert.True(lyrics.IsPreviewActive);
        Settle(win);
    }

    // Before: every tap replaced all 40 preview lines and rebuilt their word cells: ~1 GB
    // allocated and ~1 s of UI work per tap here (2-5 s UI.Stall in the reporter's log).
    [AvaloniaFact]
    public async Task Tap_WithTheLivePreviewOpen_RebuildsOnlyTheTappedLine_NotTheWholePreview()
    {
        var (vm, lyrics, audio, win) = Mount();
        try
        {
            await WaitForPreview(win, lyrics);
            var line = vm.ReviewLines[3];
            await vm.StartTapCommand.ExecuteAsync(line);
            Settle(win);
            await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs + 30);
            Settle(win);
            for (var i = 0; i < WordsPerLine; i++)
            {
                var before = lyrics.LyricLines.ToList();
                audio.Position = line.Start + TimeSpan.FromMilliseconds(i * 500 + 5);
                var alloc = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                vm.TapCommand.Execute(null);
                Settle(win);
                await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs + 30);
                Settle(win);
                var mb = (GC.GetAllocatedBytesForCurrentThread() - alloc) / (1024.0 * 1024);
                var replaced = lyrics.LyricLines.Where((l, k) => !ReferenceEquals(l, before[k])).Count();
                _out.WriteLine($"tap {i}: {sw.Elapsed.TotalMilliseconds:0} ms, {mb:0} MB, lines replaced {replaced}");
                Assert.Equal(before.Count, lyrics.LyricLines.Count);
                Assert.InRange(replaced, 1, 2);
                Assert.True(mb < 250, $"tap {i} allocated {mb:0} MB");
            }
            // The preview shows the tapped times.
            var shown = lyrics.LyricLines.First(l => l.Text.StartsWith("word0") && l.Timestamp == line.Start);
            Assert.Equal(line.Words.Select(w => w.Start), shown.Words!.Select(w => w.Start));
        }
        finally { win.Close(); lyrics.Dispose(); }
    }

    [AvaloniaFact]
    public async Task UpdatePreview_KeepsTheUnchangedLines_AndRebuildsAllWhenMostChanged()
    {
        var (vm, lyrics, _, win) = Mount();
        try
        {
            await WaitForPreview(win, lyrics);
            var before = lyrics.LyricLines.ToList();
            var line = vm.ReviewLines[10];
            vm.SelectedWord = line.Words[2];
            Assert.True(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(100)));
            await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs + 30);
            Settle(win);
            var replaced = Enumerable.Range(0, before.Count).Where(k => !ReferenceEquals(before[k], lyrics.LyricLines[k])).ToList();
            // The nudged line, and the line lit at the time (it is always rebuilt).
            Assert.InRange(replaced.Count, 1, 2);
            var nudged = lyrics.LyricLines.Single(l => l.Timestamp == line.Start && l.HasWords);
            Assert.Contains(lyrics.LyricLines.IndexOf(nudged), replaced);
            Assert.Equal(line.Words[2].Start, nudged.Words![2].Start);

            // Shift all lines: every line changed, so the list is rebuilt as before.
            before = lyrics.LyricLines.ToList();
            vm.NudgeLaterCommand.Execute(null);
            await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs + 30);
            Settle(win);
            Assert.All(Enumerable.Range(0, before.Count), k => Assert.NotSame(before[k], lyrics.LyricLines[k]));
        }
        finally { win.Close(); lyrics.Dispose(); }
    }

    // The reporter's other half: the word step buttons "do not work at all". On a .ttml song
    // with the preview open the buttons themselves move the word (each press used to cost a
    // full preview rebuild). A word squeezed 10 ms from both neighbours (taps that queued up
    // during a freeze all read one position) has no room, and the nudge rules clamp, never push.
    [AvaloniaFact]
    public async Task WordStepButtons_MoveTheSelectedWord_OnATtmlSong_WithThePreviewOpen()
    {
        var (vm, lyrics, _, win) = Mount();
        try
        {
            await WaitForPreview(win, lyrics);
            var line = vm.ReviewLines[5];
            line.IsExpanded = true;
            await vm.SelectWordCommand.ExecuteAsync(line.Words[3]);
            vm.NudgeStart = true;
            Settle(win);
            var bar = win.GetVisualDescendants().OfType<Grid>().Single(w => w.Classes.Contains("review-nudge"));
            var steps = bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nudge-step")).ToList();
            Assert.All(steps, b => Assert.True(b.IsEffectivelyEnabled));
            var start = line.Words[3].Start;
            foreach (var (button, ms) in new[] { (steps[2], 10), (steps[3], 110), (steps[1], 100), (steps[0], 0) })
            {
                button.Command!.Execute(button.CommandParameter);
                Settle(win);
                Assert.Equal(start + TimeSpan.FromMilliseconds(ms), line.Words[3].Start);
            }
        }
        finally { win.Close(); lyrics.Dispose(); }
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
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
