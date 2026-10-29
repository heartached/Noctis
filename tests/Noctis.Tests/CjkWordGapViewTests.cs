using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Live 10-19 (YOASOBI 祝福, lrc.red): a word-synced Japanese line drew as
/// "想 い 眠 り に つく 君 の", a wide gap after every timed token, where Apple Music shows
/// 想い眠りにつく君の. CJK tokens carry no spaces (the parser keeps them as separate cells on
/// purpose), and every cell whose text did not end in a space got the 24 px right pad meant
/// only for the line's last word. Each mid-line cell must be exactly as wide as its text, so
/// spaceless tokens sit flush and Latin words keep only the space they carry — on the
/// lyrics page (main, adlib and romaji rows), the side panel and the mini player.
/// </summary>
[Collection("MetadataServiceStatics")]
public class CjkWordGapViewTests
{
    private readonly ITestOutputHelper _o;
    public CjkWordGapViewTests(ITestOutputHelper o) => _o = o;

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

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    // The head of the sidecar the live page loaded (lrc.red's TTML for YOASOBI "祝福", adjacent
    // spans with no whitespace), plus a Latin line whose "!" is timed on its own.
    private const string Sample =
        "[00:00.56]<00:00.56>遥<00:00.86>か<00:01.02>遠く<00:01.50>に<00:01.93>浮<00:02.12>かぶ<00:02.61>星<00:02.90>を<00:03.29>\r\n" +
        "[00:00.56]haruka tooku ni ukabu hoshi o\r\n" +
        "[00:00.56]You think of the stars floating\r\n" +
        "[00:03.29]<00:03.29>想<00:03.68>い<00:04.08>眠<00:04.37>り<00:04.66>に<00:04.86>つく<00:05.41>君<00:05.65>の<00:06.04>\r\n" +
        "[00:03.29]omoi nemuri ni tsuku kimi no\r\n" +
        "[00:03.29]Far, far away as you fall asleep\r\n" +
        "[00:06.13]<00:06.13>Hello <00:06.50>world<00:07.00>!<00:07.40>\r\n";

    private const string CjkLine = "想い眠りにつく君の";

    private static LyricsViewModel MakeLyrics(PlayerViewModel player, FakeLibraryService library)
    {
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(Sample));
        return vm;
    }

    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(4);
        } while (sw.ElapsedMilliseconds < ms);
        Dispatcher.UIThread.RunJobs();
    }

    private static LyricLine Line(LyricsViewModel vm, string text) =>
        vm.LyricLines.First(l => l.Text == text);

    /// <summary>The advance a plain TextBlock in the cell's font gives <paramref name="text"/>.</summary>
    private static double Advance(KaraokeWordText cellText, string text)
    {
        var plain = new TextBlock
        {
            Text = text,
            FontFamily = cellText.FontFamily,
            FontSize = cellText.FontSize,
            FontWeight = cellText.FontWeight,
            FontStyle = cellText.FontStyle,
            FontStretch = cellText.FontStretch,
        };
        plain.Measure(Size.Infinity);
        return plain.DesiredSize.Width;
    }

    /// <summary>
    /// Each word of <paramref name="words"/> as laid out: its item container in the layer's
    /// WrapPanel and the swept text inside it. Fails when a word has no cell.
    /// </summary>
    private static List<(WordTiming Word, Rect Cell, KaraokeWordText Text)> Cells(Visual root, IReadOnlyList<WordTiming> words)
    {
        var sweeps = root.GetVisualDescendants().OfType<KaraokeWordText>()
            .Where(k => k.Classes.Contains("word-sweep")).ToList();
        var cells = new List<(WordTiming, Rect, KaraokeWordText)>();
        foreach (var w in words)
        {
            var k = sweeps.FirstOrDefault(x => ReferenceEquals(x.DataContext, w));
            Assert.True(k != null, $"no cell for '{w.Text}'");
            Visual container = k!;
            while (container.GetVisualParent() is { } parent && parent is not WrapPanel)
                container = parent;
            Assert.IsAssignableFrom<WrapPanel>(container.GetVisualParent()); // LyricWrapPanel since plugin API 1.5
            cells.Add((w, container.Bounds, k));
        }
        return cells;
    }

    /// <summary>
    /// Every mid-line cell is as wide as its own text (trailing space included), and the next
    /// cell starts where it ends: no pad between tokens. Returns what each gap measured.
    /// </summary>
    private void AssertFlush(Visual root, IReadOnlyList<WordTiming> words, string where)
    {
        var cells = Cells(root, words);
        var report = new List<string>();
        var bad = new List<string>();
        for (int i = 0; i < cells.Count - 1; i++)
        {
            var (word, cell, text) = cells[i];
            var next = cells[i + 1].Cell;
            var expected = Advance(text, word.Text);
            var extra = cell.Width - expected;
            report.Add($"'{word.Text}' cell {cell.Width:F1} text {expected:F1} extra {extra:F1}");
            if (Math.Abs(extra) > 0.5) bad.Add(report[^1]);
            if (Math.Abs(next.Y - cell.Y) < 0.5)
                Assert.True(Math.Abs(next.X - cell.Right) < 0.5, $"{where}: '{cells[i + 1].Word.Text}' starts {next.X - cell.Right:F1} px after '{word.Text}' ends");
        }
        _o.WriteLine($"{where}: " + string.Join(" | ", report));
        Assert.True(bad.Count == 0, $"{where}: mid-line cells wider than their text: {string.Join(" | ", bad)}");
    }

    [AvaloniaFact]
    public void LyricsPage_CjkTokens_SitFlush_LatinWordsKeepTheirSpace()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = MakeLyrics(player, library);
        var line = Line(vm, CjkLine);
        Assert.All(line.Words!, w => Assert.Equal(w.Text.Trim(), w.Text));
        // A word-timed romaji row (TTML) with a punctuation token of its own.
        line.TransliterationEndTimestamp = TimeSpan.FromSeconds(6.04);
        line.TransliterationWords = new List<WordTiming>
        {
            new() { Text = "omoi ", Start = TimeSpan.FromSeconds(3.29) },
            new() { Text = "nemuri", Start = TimeSpan.FromSeconds(4.08) },
            new() { Text = ",", Start = TimeSpan.FromSeconds(4.60) },
            new() { Text = "kimi", Start = TimeSpan.FromSeconds(5.41) },
        };
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        try
        {
            win.Show();
            PumpFor(150);
            AssertFlush(view, line.Words!, "page");
            AssertFlush(view, Line(vm, "遥か遠くに浮かぶ星を").Words!, "page line 1");
            AssertFlush(view, line.TransliterationWords!, "page romaji");

            var latin = Line(vm, "Hello world!");
            AssertFlush(view, latin.Words!, "page latin");
            // The Latin space is still there: "world" starts a space's width after "Hello" ends.
            var cells = Cells(view, latin.Words!);
            var space = cells[1].Cell.X - (cells[0].Cell.X + Advance(cells[0].Text, "Hello"));
            Assert.True(space > 1, $"Latin words lost their space ({space:F1} px)");
            // The line's last word keeps the pad that stops its last glyph clipping.
            Assert.Equal(24, cells[^1].Text.Padding.Right);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SidePanel_CjkTokens_SitFlush()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = MakeLyrics(player, library);
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 420, Height = 800, Content = view };
        try
        {
            win.Show();
            PumpFor(150);
            AssertFlush(view, Line(vm, CjkLine).Words!, "panel");
            AssertFlush(view, Line(vm, "Hello world!").Words!, "panel latin");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void MiniPlayer_CjkTokens_SitFlush()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = MakeLyrics(player, library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var mini = new MiniPlayerViewModel(player, lyrics, settings, library);
        var win = new MiniPlayerWindow { DataContext = mini, Width = 340, Height = 432 };
        try
        {
            win.Show();
            PumpFor(150);
            mini.ToggleLyricsFormCommand.Execute(null);
            PumpFor(700);
            AssertFlush(win, Line(lyrics, CjkLine).Words!, "mini");
        }
        finally { win.Close(); }
    }
}
