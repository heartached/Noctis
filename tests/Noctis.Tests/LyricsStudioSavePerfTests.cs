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
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: "when I click on Save lyrics after a track has been done, it lags and freezes
/// the app for 1-4 seconds". Measured here on the case the owner works in: the song under
/// review is playing and the live preview pane is open. Save dropped the preview and the
/// lyrics view model reloaded the song from disk, replacing every line, so each open lyrics
/// view rebuilt every line's word cells, although the saved file holds exactly the lines the
/// preview was showing.
/// </summary>
public class LyricsStudioSavePerfTests : IDisposable
{
    private const int Lines = 40;
    private const int WordsPerLine = 8;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-saveperf-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public LyricsStudioSavePerfTests(ITestOutputHelper output)
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

    private static double Settle(Window win)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        return sw.Elapsed.TotalMilliseconds;
    }

    private (LyricsStudioViewModel vm, LyricsViewModel lyrics, Track track, Window win) Mount()
    {
        EnsureAppResources();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), new FakeLibraryService());
        player.Duration = TimeSpan.FromMinutes(5);
        var path = Path.Combine(_root, "song.m4a");
        // The song's own word-timed lyrics, written by another app: Save sends it to the "trash" (deleted here).
        File.WriteAllText(Path.ChangeExtension(path, ".elrc"), Elrc());
        var track = new Track { Title = "song", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(5) };
        var registry = new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json"));
        var writer = new LyricsWriter(new StubMetadata(), null, registry, Path.Combine(_root, "cache"))
        {
            TrashFile = p => { File.Delete(p); return true; },
            SaveTarget = () => LyricsSaveTarget.Sidecar,
        };
        var vm = new LyricsStudioViewModel(new[] { track }, new NoRunEngine(_root), writer, new FakeLibraryService(), player,
            () => new AppSettings { LyricsStudioLivePreview = true, LyricsStudioWordTimings = true }, _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false, PreviewLyrics = lyrics };
        var win = new Window { Width = 1600, Height = 900, Content = panel };
        win.Show();
        vm.Selected = vm.Queue[0];
        player.CurrentTrack = track;
        lyrics.EnsureLyricsForCurrentTrack();
        return (vm, lyrics, track, win);
    }

    private static async Task WaitFor(Window win, Func<bool> done)
    {
        var end = Environment.TickCount64 + 5000;
        while (!done() && Environment.TickCount64 < end) { Settle(win); await Task.Delay(10); }
        Assert.True(done());
        Settle(win);
    }

    [AvaloniaFact]
    public async Task Save_WithTheSongPlayingAndThePreviewOpen_KeepsTheLinesOnScreen_InsteadOfRebuildingThemAll()
    {
        var (vm, lyrics, track, win) = Mount();
        try
        {
            await WaitFor(win, () => lyrics.IsPreviewActive);
            // An edit, as after timing a song: one word moved.
            vm.SelectedWord = vm.ReviewLines[10].Words[2];
            Assert.True(vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(100)));
            await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs + 30);
            Settle(win);
            Assert.True(lyrics.IsPreviewActive);
            var before = lyrics.LyricLines.ToList();
            Assert.True(before.Count >= Lines);

            var alloc = GC.GetAllocatedBytesForCurrentThread();
            var total = Stopwatch.StartNew();
            var command = Stopwatch.StartNew();
            await vm.SaveCommand.ExecuteAsync(null);
            var commandMs = command.Elapsed.TotalMilliseconds;
            var ui = Settle(win);
            // The real lyrics come back from the file just written (the probe runs off the UI thread).
            var end = Environment.TickCount64 + 10000;
            var ticks = 0;
            var longest = 0.0;
            // CanSaveTtmlToFile comes on only once the lines of the .elrc on disk are applied (never for a preview).
            while (!lyrics.CanSaveTtmlToFile && Environment.TickCount64 < end)
            {
                // A 10 ms wait that comes back late = the UI thread was busy with posted work.
                var tick = Stopwatch.StartNew();
                await Task.Delay(10);
                ui += Settle(win);
                longest = Math.Max(longest, tick.Elapsed.TotalMilliseconds);
                ticks++;
            }
            var reloadMs = total.Elapsed.TotalMilliseconds;
            await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs + 50);
            ui += Settle(win);
            var mb = (GC.GetAllocatedBytesForCurrentThread() - alloc) / (1024.0 * 1024);
            var replaced = lyrics.LyricLines.Count == before.Count
                ? lyrics.LyricLines.Where((l, k) => !ReferenceEquals(l, before[k])).Count()
                : lyrics.LyricLines.Count;
            _out.WriteLine($"save: command {commandMs:0} ms, settled UI work {ui:0} ms, reload done at {reloadMs:0} ms, longest 10 ms tick {longest:0} ms, total {total.Elapsed.TotalMilliseconds:0} ms, {mb:0} MB, lines replaced {replaced} of {before.Count}");

            Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);
            Assert.False(lyrics.IsPreviewActive);
            Assert.True(lyrics.CanSaveTtmlToFile);
            Assert.True(File.Exists(Path.ChangeExtension(track.FilePath, ".elrc")));
            // The saved file holds the edit, and the lines on screen are the saved ones.
            var nudged = vm.ReviewLines[10].Words[2].Start;
            Assert.Contains(lyrics.LyricLines, l => l.Words?.Any(w => w.Start == nudged) == true);
            Assert.Equal(before.Count, lyrics.LyricLines.Count);
            // Only the line lit at the time may be rebuilt (it keeps sweep state of its own).
            Assert.InRange(replaced, 0, 2);
        }
        finally { win.Close(); lyrics.Dispose(); }
    }

    /// <summary>
    /// The prompt's probe for lyrics files beside the song ran on the UI thread before the
    /// confirmation: four listings of the song's folder. Measured in a big flat folder.
    /// </summary>
    [Fact]
    public void ExistingLyricsFiles_InAFolderOfThousandsOfSongs_IsTimed()
    {
        const int songs = 3000;
        for (var i = 0; i < songs; i++)
        {
            var p = Path.Combine(_root, $"Artist - Song {i:0000}.mp3");
            File.WriteAllBytes(p, Array.Empty<byte>());
            File.WriteAllText(Path.ChangeExtension(p, ".lrc"), "[00:01.00]x");
            if (i % 3 == 0) File.WriteAllText(Path.ChangeExtension(p, ".elrc"), "[00:01.00]<00:01.00>x <00:01.50>");
        }
        var track = new Track { Title = "Song 1500", FilePath = Path.Combine(_root, "Artist - Song 1500.mp3") };
        var sw = Stopwatch.StartNew();
        List<string> found = null!;
        for (var k = 0; k < 10; k++) found = LyricsStudioViewModel.ExistingLyricsFiles(track);
        _out.WriteLine($"ExistingLyricsFiles over {songs * 2 + songs / 3} files: {sw.Elapsed.TotalMilliseconds / 10:0.0} ms per call");
        Assert.Equal(2, found.Count);
    }

    /// <summary>
    /// A save in the page's Studio used to mark the page's whole format scan stale: the next visit
    /// re-checked every song's lyrics files. Now the saved song's entry and the counts are updated
    /// from that song alone, and the next visit re-picks from the cached scan.
    /// </summary>
    [AvaloniaFact]
    public async Task SaveOnThePage_UpdatesThatSongsFormatAndTheCounts_WithoutRescanningTheLibrary()
    {
        const int songs = 3000;
        var library = new FakeLibraryService();
        for (var i = 0; i < songs; i++)
        {
            var dir = Path.Combine(_root, "album" + (i % 300));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"song{i}.flac");
            if (i % 2 == 0) File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:05.00]First line\n[00:12.50]Second line\n");
            else File.WriteAllText(Path.ChangeExtension(path, ".elrc"), "[00:05.00]<00:05.00>First <00:05.40>line\n");
            library.TrackList.Add(new Track { Title = "song" + i, Artist = "A", FilePath = path });
        }
        var scans = new List<int>();
        var scanMs = new List<double>();
        LyricsStudioPageViewModel.DetectFormatsFunc detect = (tracks, ct, detected) =>
        {
            var sw = Stopwatch.StartNew();
            var formats = ExistingLyricsLoader.DetectFormats(tracks, ct, detected);
            lock (scans) { scans.Add(tracks.Count); scanMs.Add(sw.Elapsed.TotalMilliseconds); }
            return formats;
        };
        var page = new LyricsStudioPageViewModel(library, () => true, tracks =>
            new LyricsStudioViewModel(tracks, new NoRunEngine(_root),
                new LyricsWriter(new StubMetadata(), null, new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json")), Path.Combine(_root, "cache"))
                { TrashFile = p => { File.Delete(p); return true; }, SaveTarget = () => LyricsSaveTarget.Sidecar },
                library, null, () => new AppSettings { LyricsStudioWordTimings = true }, _ => { }) { Confirm = _ => Task.FromResult(true) },
            detect);
        await page.RefreshAsync();
        Assert.Equal(new[] { songs }, scans);
        var elrcBefore = page.Counts.First(c => c.Tag == "ELRC").Count;

        // Word timings made for the first queued song, and saved.
        var studio = page.Studio!;
        var item = studio.Queue[0];
        var lines = new[] { "First line", "Second line" }.Select((text, i) => new AlignedLine(text, TimeSpan.FromSeconds(5 + i * 7), TimeSpan.FromSeconds(6.5 + i * 7),
            text.Split(' ').Select((w, k) => new AlignedWord(w, TimeSpan.FromSeconds(5 + i * 7 + k * 0.5), TimeSpan.FromSeconds(5.4 + i * 7 + k * 0.5))).ToList(), 0.9, false)).ToList();
        item.Result = new LyricsStudioResult(item.Track, lines, LyricsStudioSource.ExistingLyrics, 0.9, "en", 4);
        item.Status = LyricsStudioViewModel.StudioStatus.Ready;
        studio.Selected = item;
        await studio.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, item.Status);
        var end = Environment.TickCount64 + 5000;
        while (page.Counts.First(c => c.Tag == "ELRC").Count == elrcBefore && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.Equal(elrcBefore + 1, page.Counts.First(c => c.Tag == "ELRC").Count);
        Assert.Equal(new[] { songs, 1 }, scans);

        // The next visit (no review open any more) re-picks from the cache: no library-wide pass.
        item.Result = null;
        studio.Selected = null;
        await page.RefreshAsync();
        Assert.Equal(new[] { songs, 1 }, scans);
        Assert.DoesNotContain(page.Studio!.Queue, q => q.Track.Id == item.Track.Id);
        var formats = ExistingLyricsLoader.DetectFormats(library.TrackList);
        Assert.Equal(SettingsViewModel.BuildLyricsStudioCounts(formats).Select(c => (c.Count, c.Tag)), page.Counts.Select(c => (c.Count, c.Tag)));
        _out.WriteLine($"library pass over {songs} songs: {scanMs[0]:0} ms (warm cache); saved song alone: {scanMs[1]:0.0} ms");
    }

    [Fact]
    public void AtomicSidecarWrite_ReplacesUnderTheOnDiskName_AndLeavesNoTempFile()
    {
        var upper = Path.Combine(_root, "Song.LRC");
        File.WriteAllText(upper, "old");
        LyricsWriter.WriteAllTextAtomic(Path.Combine(_root, "Song.lrc"), "[00:01.00]new");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(new[] { "Song.LRC" }, Directory.GetFiles(_root, "Song.*").Select(Path.GetFileName));
            Assert.Equal("[00:01.00]new", File.ReadAllText(upper));
        }
        var fresh = Path.Combine(_root, "Other.elrc");
        LyricsWriter.WriteAllTextAtomic(fresh, "x");
        Assert.Equal("x", File.ReadAllText(fresh));
        Assert.Empty(Directory.GetFiles(_root, "*.noctis-tmp"));
        // No BOM, as before.
        Assert.Equal((byte)'x', File.ReadAllBytes(fresh)[0]);
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
