using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #269 follow-up. A translation or romanization row sits on its line's side (under a
/// right-aligned Persian line, on the right) while its own text reads in its own direction: an
/// English translation under a Persian line is left-to-right text against the right edge, a
/// Persian translation under an English line right-to-left text against the left edge. Checked on
/// the lyrics page (fullscreen is the same view), the side panel and the mini player; and the
/// phone lays Persian lines out the way the desktop does.
/// </summary>
public class RtlLyricLayersTests
{
    private const string PersianLine = "دوستت دارم";
    private const string EnglishTranslation = "I love you more than all the words I know";
    private const string Romanization = "dustet daram";
    private const string EnglishLine = "Hello my love";
    private const string PersianTranslation = "سلام عشق من، سلام";

    private static WordTiming Word(string text, double at) =>
        new() { Text = text, Start = TimeSpan.FromSeconds(at), End = TimeSpan.FromSeconds(at + 0.4) };

    /// <summary>A Persian line with an English translation and a romanization; an English line
    /// with a Persian translation; a word-synced Persian line with word-synced romanization.</summary>
    private static List<LyricLine> Lines() => new()
    {
        new LyricLine { Timestamp = TimeSpan.FromSeconds(1), Text = PersianLine, Translation = EnglishTranslation, Transliteration = Romanization },
        new LyricLine { Timestamp = TimeSpan.FromSeconds(4), Text = EnglishLine, Translation = PersianTranslation },
        new LyricLine
        {
            Timestamp = TimeSpan.FromSeconds(7), Text = "با تو باشم",
            Words = new[] { Word("با ", 7), Word("تو ", 7.4), Word("باشم", 7.8) },
            TransliterationWords = new[] { Word("ba ", 7), Word("to ", 7.4), Word("basham", 7.8) },
        },
    };

    internal static void PumpFor(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBlock Text(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text && t is not KaraokeWordText && t.IsEffectivelyVisible);

    // Where a TextBlock's first row of text starts and ends, in the coordinates of its line.
    private static double TextLeft(TextBlock t, Visual to) =>
        t.TranslatePoint(new Point(t.TextLayout.TextLines[0].Start, 0), to)!.Value.X;
    private static double TextRight(TextBlock t, Visual to)
    {
        var row = t.TextLayout.TextLines[0];
        return t.TranslatePoint(new Point(row.Start + row.Width, 0), to)!.Value.X;
    }

    private static KaraokeWordText Cell(Visual root, WordTiming word) =>
        root.GetVisualDescendants().OfType<KaraokeWordText>().First(w => ReferenceEquals(w.DataContext, word) && w.IsEffectivelyVisible);

    /// <summary>The three lines' secondary rows, on any desktop lyrics surface.</summary>
    private static void AssertSecondaryRows(Visual root, IList<LyricLine> lines, string surface)
    {
        // 1. Persian line, English translation: both against the right edge of the line's block,
        //    the translation reading left to right.
        var line = Text(root, PersianLine);
        var block = line.FindAncestorOfType<Button>()!;
        var translation = Text(root, EnglishTranslation);
        var romanization = Text(root, Romanization);
        Assert.Equal(FlowDirection.LeftToRight, translation.FlowDirection);
        Assert.Equal(FlowDirection.LeftToRight, romanization.FlowDirection);
        Assert.Equal(block.Bounds.Width, TextRight(line, block), 1);
        // The rows' boxes end at the line's right edge and their text is flush with it (a wrapped
        // row is measured by its box: Avalonia right-aligns a row with its trailing space).
        foreach (var row in new[] { translation, romanization })
        {
            var end = row.TranslatePoint(new Point(row.Bounds.Width, 0), block)!.Value.X;
            Assert.True(Math.Abs(end - block.Bounds.Width) < 1,
                $"{surface}: '{row.Text}' ends at {end:F1}, its Persian line at {block.Bounds.Width:F1}");
            Assert.Equal(TextAlignment.Right, row.TextAlignment);
        }

        // 2. English line, Persian translation: both against the left edge, the translation
        //    reading right to left.
        var english = Text(root, EnglishLine);
        var englishBlock = english.FindAncestorOfType<Button>()!;
        var persianTranslation = Text(root, PersianTranslation);
        Assert.Equal(FlowDirection.RightToLeft, persianTranslation.FlowDirection);
        Assert.Equal(0, TextLeft(english, englishBlock), 1);
        Assert.True(Math.Abs(TextLeft(persianTranslation, englishBlock)) < 1,
            $"{surface}: Persian translation starts at {TextLeft(persianTranslation, englishBlock):F1}");

        // 3. Word-synced Persian line, word-synced romanization: the row on the right, its words
        //    in reading order from the left; the Persian words from the right.
        var words = lines[2].Words!;
        var roman = lines[2].TransliterationWords!;
        var wordBlock = Cell(root, words[0]).FindAncestorOfType<Button>()!;
        double X(WordTiming w) => Cell(root, w).TranslatePoint(default, wordBlock)!.Value.X;
        Assert.True(X(words[0]) > X(words[1]), $"{surface}: Persian words run from the right");
        Assert.True(X(roman[0]) < X(roman[1]), $"{surface}: romanization {X(roman[0]):F1} vs {X(roman[1]):F1}");
        Assert.All(roman, w => Assert.False(Cell(root, w).RightToLeft));
        // Its last glyph at the right edge (the line-final pad hangs past it, LyricWrapPanel).
        var romanRight = roman.Max(w => Cell(root, w).TranslatePoint(new Point(Cell(root, w).GlyphSpan.Right, 0), wordBlock)!.Value.X);
        Assert.True(Math.Abs(romanRight - wordBlock.Bounds.Width) < 1,
            $"{surface}: romanization row ends at {romanRight:F1} of {wordBlock.Bounds.Width:F1}");
    }

    private static LyricsViewModel MakeLyrics(PlayerViewModel? player = null)
    {
        var vm = LongLyricsKit.MakeViewModel(player);
        vm.LyricLines.ReplaceAll(Lines());
        return vm;
    }

    [AvaloniaFact]
    public void LyricsPage_SecondaryRows_FollowTheirLinesSide_AndReadInTheirOwnDirection()
    {
        var vm = MakeLyrics();
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        try
        {
            win.Show();
            PumpFor(150);
            AssertSecondaryRows(view, vm.LyricLines, "page");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SidePanel_SecondaryRows_FollowTheirLinesSide_AndReadInTheirOwnDirection()
    {
        var vm = MakeLyrics();
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 420, Height = 900, Content = view };
        try
        {
            win.Show();
            PumpFor(150);
            AssertSecondaryRows(view, vm.LyricLines, "panel");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void MiniPlayer_SecondaryRows_FollowTheirLinesSide_AndReadInTheirOwnDirection()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = MakeLyrics(player);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var mini = new MiniPlayerViewModel(player, lyrics, settings, library);
        var win = new MiniPlayerWindow { DataContext = mini, Width = 340, Height = 900 };
        try
        {
            win.Show();
            PumpFor(150);
            mini.ToggleLyricsFormCommand.Execute(null);
            PumpFor(700);
            AssertSecondaryRows(win, lyrics.LyricLines, "mini");
        }
        finally { win.Close(); }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    // ── The phone ──

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>The phone's lyrics page showing <paramref name="lrc"/> from a sidecar, the way a
    /// song's lyrics load on the phone.</summary>
    internal static (ShellView View, Window Window, LyricsPageViewModel Lyrics) MountPhone(string lrc)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = lrc;
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var lyrics = new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work()));
        var shell = new ShellViewModel(new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()), nowPlaying, lyrics);
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        shell.Player.PlayTracks(new[]
        {
            new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "Tester", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) },
        }, 0);
        shell.Player.TogglePlayPauseCommand.Execute(null);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        PumpFor(300);
        return (view, window, lyrics);
    }

    internal static Visual PhoneList(ShellView view) =>
        view.GetLogicalDescendants().OfType<LyricsPage>().Single().FindControl<Control>("LyricsList")!;

    // The sample lines from the desktop fix's live check.
    private const string PersianSong =
        "[00:01.00]<00:01.00>می‌خواهم <00:01.60>با <00:02.00>تو <00:02.40>باشم\n" +
        "[00:04.00]دوستت دارم baby, تا ابد\n" +
        "[00:07.00]گل پیچک، ژاله (۱۳۹۹)\n" +
        "[00:10.00]Hello darkness, my old friend\n";

    [AvaloniaFact]
    public void Phone_APersianSong_ReadsLikeTheDesktop()
    {
        var (view, window, lyrics) = MountPhone(PersianSong);
        try
        {
            var list = PhoneList(view);
            var lines = lyrics.Lines.Where(l => !l.IsIntroPlaceholder).ToList();
            Assert.Equal(4, lines.Count);
            double Right(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), list)!.Value.X;
            double Left(Visual v) => v.TranslatePoint(default, list)!.Value.X;
            Button Block(LyricLine l) => list.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.DataContext, l));

            // Word-synced: on the right, first word rightmost, each word sweeping from its right.
            var words = lines[0].Words!;
            var cells = words.Select(w => Cell(list, w)).ToList();
            Assert.Equal(list.Bounds.Width, Right(Block(lines[0])), 1);
            Assert.True(Left(cells[0]) > Left(cells[1]));
            Assert.All(cells, c => Assert.True(c.RightToLeft, "the sweep fills a Persian word from its right"));

            // Plain Persian lines: on the right, in a right-to-left paragraph.
            foreach (var line in new[] { lines[1], lines[2] })
            {
                var text = Text(Block(line), line.Text!);
                Assert.Equal(list.Bounds.Width, Right(Block(line)), 1);
                Assert.Equal(FlowDirection.RightToLeft, text.FlowDirection);
            }
            var mixed = Text(Block(lines[1]), lines[1].Text!);
            double X(int i) => mixed.TranslatePoint(new Point(mixed.TextLayout.HitTestTextPosition(i).X, 0), list)!.Value.X;
            Assert.True(X(0) > X(lines[1].Text!.IndexOf('b')));

            // An English line in a Persian song stays an English line: left, left to right
            // (the desktop's rule; the phone used to turn the whole song).
            var english = Text(Block(lines[3]), lines[3].Text!);
            Assert.Equal(0, Left(Block(lines[3])), 1);
            Assert.Equal(FlowDirection.LeftToRight, english.FlowDirection);
            Assert.Equal(0, english.TranslatePoint(new Point(english.TextLayout.HitTestTextPosition(0).X, 0), list)!.Value.X, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Phone_APersianLineInAnEnglishSong_IsRightToLeft()
    {
        var (view, window, lyrics) = MountPhone(
            "[00:01.00]Hello darkness, my old friend\n[00:04.00]I've come to talk with you again\n[00:07.00]دوستت دارم\n");
        try
        {
            var list = PhoneList(view);
            var persian = lyrics.Lines.First(l => l.Text == "دوستت دارم");
            var block = list.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.DataContext, persian));
            Assert.Equal(list.Bounds.Width, block.TranslatePoint(new Point(block.Bounds.Width, 0), list)!.Value.X, 1);
            Assert.Equal(FlowDirection.RightToLeft, Text(block, persian.Text!).FlowDirection);
        }
        finally { window.Close(); }
    }
}
