using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone in Arabic: Arabic text shaped right to left but no screen mirrored (the back
/// chevron pointed left, the tab bar read Home-first from the left, rows had their chevrons on
/// the right). Now the shell mirrors at its root when the strings shown are Arabic, live on a
/// language change, while media playback (transport glyphs and rows, timelines, times) stays
/// left to right as Material's bidirectionality guidance asks. The desktop is not touched.
/// </summary>
[Collection("Localization")] // Loc.Instance is process-wide: these tests must not interleave
public class MobileRtlTests
{
    private readonly ITestOutputHelper _o;
    public MobileRtlTests(ITestOutputHelper o) => _o = o;

    /// <summary>Whether <paramref name="v"/> shows mirrored on screen: a step right in its own
    /// space lands left in the window.</summary>
    private static bool IsMirrored(Visual v, Visual window)
    {
        var a = v.TranslatePoint(new Point(0, 0), window)!.Value;
        var b = v.TranslatePoint(new Point(10, 0), window)!.Value;
        return b.X < a.X;
    }

    private static double Left(Visual v, Visual window)
    {
        var a = v.TranslatePoint(new Point(0, 0), window)!.Value;
        var b = v.TranslatePoint(new Point(v.Bounds.Width, 0), window)!.Value;
        return Math.Min(a.X, b.X);
    }

    private static (MobileFixtures.Rig Rig, Window Window, ShellView View) Open()
    {
        var songs = new[]
        {
            MobileFixtures.Song("أنا لحبيبي", "فيروز"), MobileFixtures.Song("Song One"), MobileFixtures.Song("قارئة الفنجان", "عبد الحليم حافظ"),
        };
        var album = MobileFixtures.MakeAlbum("Record", "Band", songs);
        var rig = MobileFixtures.MakeRig(songs, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        MobileFixtures.Land(window);
        return (rig, window, view);
    }

    private static void SetLanguage(ShellViewModel shell, string code)
    {
        // As AndroidApp and Settings › Language do: the culture, then the shell relabels.
        var before = Loc.Instance.Culture.Name;
        Loc.Instance.SetCulture(code);
        if (Loc.Instance.Culture.Name != before) shell.Relabel();
    }

    [Fact]
    public void OnlyAShippedRightToLeftTranslation_MirrorsThePhone()
    {
        Assert.True(ShellViewModel.IsRightToLeftLanguage("ar"));
        Assert.True(ShellViewModel.IsRightToLeftLanguage("ar-EG"));   // resolves to the Arabic strings
        Assert.False(ShellViewModel.IsRightToLeftLanguage("en"));
        Assert.False(ShellViewModel.IsRightToLeftLanguage("de"));
        Assert.False(ShellViewModel.IsRightToLeftLanguage("he"));     // no Hebrew strings: shown in English
    }

    [AvaloniaFact]
    public void Arabic_MirrorsTheRoot_Live_AndBackAgain()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        try
        {
            SetLanguage(rig.Shell, "en");
            Assert.Equal(FlowDirection.LeftToRight, view.FlowDirection);

            SetLanguage(rig.Shell, "ar");
            window.UpdateLayout();
            Assert.Equal(FlowDirection.RightToLeft, view.FlowDirection);
            Assert.True(rig.Shell.IsRightToLeft);

            SetLanguage(rig.Shell, "en");
            window.UpdateLayout();
            Assert.Equal(FlowDirection.LeftToRight, view.FlowDirection);
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Arabic_MirrorsChevronsAndTheTabBar()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        try
        {
            SetLanguage(rig.Shell, "ar");
            rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
            MobileFixtures.Land(window);
            var chevron = MobileFixtures.Named<PathIcon>(view, "RowChevron");
            Assert.True(IsMirrored(chevron, window), "the row chevron › still points right");

            // A pushed page's back chevron points the way Back goes: right.
            rig.Shell.OpenSongsCommand.Execute(null);
            MobileFixtures.Land(window);
            var page = MobileFixtures.PageView(view, rig.Shell.CurrentPage!);
            var back = page.GetVisualDescendants().OfType<PathIcon>().First(p => p.Data == view.FindResource("PhBack"));
            Assert.True(IsMirrored(back, window));

            // The tab bar reads from the right: its first tab (Home) sits right of its last.
            var tabs = view.FindControl<Grid>("TabBar")!.Children.OfType<Control>().Where(c => c.IsVisible).OrderBy(Grid.GetColumn).ToList();
            Assert.True(Left(tabs[0], window) > Left(tabs[^1], window), "the first tab is still on the left");
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Arabic_KeepsMediaPlaybackLeftToRight()
    {
        var (rig, window, view) = Open();
        using var _ = rig;
        try
        {
            SetLanguage(rig.Shell, "ar");
            rig.Shell.Player.PlayTracks(rig.Library.Tracks.ToList(), 0);
            rig.Shell.OpenNowPlayingCommand.Execute(null);
            MobileFixtures.Land(window);
            var np = view.GetVisualDescendants().OfType<NowPlayingPage>().First();

            // The glyphs: Next still points right, and the row reads Previous, Play, Next from the left.
            var next = MobileFixtures.Named<Button>(np, "NextButton");
            var previous = MobileFixtures.Named<Button>(np, "PreviousButton");
            Assert.False(IsMirrored(next.GetVisualDescendants().OfType<TransportGlyph>().First(), window), "the Next glyph is mirrored");
            Assert.True(Left(previous, window) < Left(next, window), "Previous is right of Next");

            // The timeline fills from the left, and the times read "0:00" … "-1:30" left to right.
            Assert.False(IsMirrored(MobileFixtures.Named<CapsuleSlider>(np, "SeekBar"), window));
            var elapsed = MobileFixtures.Named<TextBlock>(np, "ElapsedText");
            var remaining = MobileFixtures.Named<TextBlock>(np, "RemainingText");
            Assert.Equal(FlowDirection.LeftToRight, elapsed.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, remaining.FlowDirection);
            Assert.True(Left(elapsed, window) < Left(remaining, window));

            // The empty cover's music note is an object, not a direction: never mirrored.
            // (The headless app loads no icon geometry: the 72 px note is the empty cover's.)
            var placeholder = np.GetVisualDescendants().OfType<PathIcon>().First(p => p.Width == 72 && p.IsEffectivelyVisible);
            Assert.False(IsMirrored(placeholder, window), "the cover placeholder's note is mirrored");

            // Volume is an ordinary slider: it follows the layout.
            Assert.True(IsMirrored(MobileFixtures.Named<CapsuleSlider>(np, "VolumeSlider"), window));

            // The mini player: Play, then Next, from the left, at the pill's mirrored (left) end.
            rig.Shell.CloseNowPlayingCommand.Execute(null);
            MobileFixtures.Land(window);
            var miniPlay = view.FindControl<Button>("MiniPlayButton")!;
            var miniNext = view.FindControl<Button>("MiniNextButton")!;
            Assert.True(Left(miniPlay, window) < Left(miniNext, window), "the mini player's Next is left of Play");
            Assert.True(Left(miniNext, window) < 412 / 2.0, "the mini player's buttons are not at its leading (left) end");
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }

    [Fact]
    public void Lyrics_ReadByTheirOwnScript_NotThePhonesLanguage()
    {
        // The phone marks each line the desktop's way (#269: TextDirection.LineDirections).
        static bool[] Directions(params string[] lines)
        {
            var models = lines.Select(t => new Noctis.Models.LyricLine { Text = t }).ToList();
            LyricsPageViewModel.ApplyLineDirections(models);
            return models.Select(l => l.IsRightToLeft).ToArray();
        }
        Assert.Equal(new[] { true, true }, Directions("أنا لحبيبي وحبيبي إلي", "يا عصفورة بيضا"));
        Assert.Equal(new[] { true }, Directions("שלום עולם"));
        Assert.Equal(new[] { false, false }, Directions("Hello darkness, my old friend", "I've come to talk with you again"));
        // A Latin song with one Arabic word, and lines with no letters at all.
        Assert.Equal(new[] { false }, Directions("Habibi (حبيبي), come with me tonight"));
        Assert.Equal(new[] { false, false, false }, Directions("♪", "3:42", ""));
    }

    // ── Text in its own direction (S23, 11-23: "¿Cuál Es Tu Plan?" in an Arabic layout) ──

    [Fact]
    public void ALine_ReadsByItsFirstStrongCharacter()
    {
        Assert.Equal(FlowDirection.LeftToRight, TextDirection.FirstStrong("¿Cuál Es Tu Plan? - Single"));
        Assert.Equal(FlowDirection.LeftToRight, TextDirection.FirstStrong("(Remix) Hello"));
        Assert.Equal(FlowDirection.LeftToRight, TextDirection.FirstStrong("#1 Hits"));
        Assert.Equal(FlowDirection.LeftToRight, TextDirection.FirstStrong("...Baby One More Time"));
        Assert.Equal(FlowDirection.RightToLeft, TextDirection.FirstStrong("«قارئة الفنجان»"));
        Assert.Equal(FlowDirection.RightToLeft, TextDirection.FirstStrong("٣ أغانٍ"));   // Arabic digits are weak
        Assert.Null(TextDirection.FirstStrong("3:42"));
        Assert.Null(TextDirection.FirstStrong(""));
    }

    /// <summary>The title as a row shows it, in a right-to-left layout and back.</summary>
    [AvaloniaFact]
    public void ALatinTitle_KeepsItsOrder_AndTheArabicRowsEdge()
    {
        var songs = new[] { MobileFixtures.Song("¿Cuál Es Tu Plan?", "Bad Bunny"), MobileFixtures.Song("(Remix) Hello"), MobileFixtures.Song("قارئة الفنجان", "عبد الحليم حافظ") };
        var rig = MobileFixtures.MakeRig(songs, new[] { MobileFixtures.MakeAlbum("Record", "Band", songs) });
        using var _ = rig;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            SetLanguage(rig.Shell, "ar");
            rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
            rig.Shell.OpenSongsCommand.Execute(null);
            MobileFixtures.Land(window);
            TextBlock Row(string text) => view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text && t.IsEffectivelyVisible);

            foreach (var latin in new[] { "¿Cuál Es Tu Plan?", "(Remix) Hello" })
            {
                var t = Row(latin);
                Assert.Equal(FlowDirection.LeftToRight, t.FlowDirection);      // "¿" stays in front
                Assert.Equal(TextAlignment.End, t.TextAlignment);              // …against the row's right edge
                Assert.True(IsAgainstTheRightEdge(t), $"{latin} is not against the row's right edge");
            }
            var arabic = Row("قارئة الفنجان");
            Assert.Equal(FlowDirection.RightToLeft, arabic.FlowDirection);
            Assert.Equal(TextAlignment.Start, arabic.TextAlignment);
            Assert.True(IsAgainstTheRightEdge(arabic));

            // Back to English: the Latin title is an ordinary left-to-right line again.
            SetLanguage(rig.Shell, "en");
            MobileFixtures.Land(window);
            var back = Row("¿Cuál Es Tu Plan?");
            Assert.Equal(FlowDirection.LeftToRight, back.FlowDirection);
            Assert.Equal(TextAlignment.Start, back.TextAlignment);
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }

    /// <summary>Where the text's first and last glyphs land in the window: right aligned when its
    /// ink ends at the box's right edge (the box is mirrored with the layout).</summary>
    private static bool IsAgainstTheRightEdge(TextBlock t)
    {
        var layout = t.TextLayout;
        var inkRight = layout.TextLines.Max(l => l.Start + l.WidthIncludingTrailingWhitespace);
        var inkLeft = layout.TextLines.Min(l => l.Start);
        // A box narrower than its text has nothing to align; otherwise the ink hugs one edge.
        if (t.Bounds.Width <= inkRight - inkLeft + 1) return true;
        var start = t.TranslatePoint(new Point(inkLeft, 0), (Visual?)TopLevel.GetTopLevel(t) ?? t)!.Value.X;
        var end = t.TranslatePoint(new Point(inkRight, 0), (Visual?)TopLevel.GetTopLevel(t) ?? t)!.Value.X;
        var boxRight = Math.Max(t.TranslatePoint(new Point(0, 0), (Visual?)TopLevel.GetTopLevel(t) ?? t)!.Value.X,
            t.TranslatePoint(new Point(t.Bounds.Width, 0), (Visual?)TopLevel.GetTopLevel(t) ?? t)!.Value.X);
        return Math.Abs(Math.Max(start, end) - boxRight) < 1.5;
    }

    /// <summary>The mini player's ticker in Arabic: a long Latin title showed its head on the right
    /// and its loop copy's tail on the left at once. At rest only the head shows, at the ticker's
    /// start; the copy waits past the far edge (right for a Latin title, left for an Arabic one).</summary>
    [AvaloniaTheory]
    [InlineData("¿Cuál Es Tu Plan? (feat. DJ Nelson) - a title far too long for the mini player's row", false)]
    [InlineData("قارئة الفنجان - من حفل قاعة ألبرت هول في لندن عام ألف وتسعمائة وخمسة وسبعين كاملة", true)]
    public void TheMiniPlayersTicker_ShowsOneCopyAtRest_InItsTextsDirection(string title, bool arabic)
    {
        var saved = MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled;
        var song = MobileFixtures.Song(title, "Bad Bunny, Ñejo & Pj Sin Suela, DJ Nelson");
        var rig = MobileFixtures.MakeRig(new[] { song }, new[] { MobileFixtures.MakeAlbum("Record", "Band", song) });
        using var _ = rig;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = true;
            SetLanguage(rig.Shell, "ar");
            rig.Shell.Player.PlayTracks(new[] { song }, 0);
            for (var i = 0; i < 4; i++) { MobileFixtures.Land(window); Dispatcher.UIThread.RunJobs(); }
            var marquee = view.FindControl<MarqueeTextBlock>("MiniTitle")!;
            Assert.Equal(arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, marquee.FlowDirection);

            var viewport = (Border)marquee.Content!;
            var panel = (StackPanel)viewport.Child!;
            var texts = panel.Children.OfType<TextBlock>().ToList();
            var original = texts[0];
            var copy = texts[^1];
            Assert.True(copy.IsVisible, "the title does not scroll (the test needs it to)");
            (double L, double R) Span(Visual v) => (v.TranslatePoint(new Point(0, 0), window)!.Value.X, v.TranslatePoint(new Point(v.Bounds.Width, 0), window)!.Value.X) switch
            {
                var (a, b) => (Math.Min(a, b), Math.Max(a, b)),
            };
            var port = Span(viewport);
            var head = Span(original);
            var tail = Span(copy);
            // The copy is wholly outside the viewport, past the end the text runs towards.
            if (arabic) Assert.True(tail.R <= port.L + 0.5, $"the loop copy shows at rest ({tail.L:0}-{tail.R:0} in {port.L:0}-{port.R:0})");
            else Assert.True(tail.L >= port.R - 0.5, $"the loop copy shows at rest ({tail.L:0}-{tail.R:0} in {port.L:0}-{port.R:0})");
            // The head sits at the text's start: left for a Latin title, right for an Arabic one.
            if (arabic) Assert.Equal(port.R, head.R, 1);
            else Assert.Equal(port.L, head.L, 1);
        }
        finally
        {
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = saved;
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }

    // ── Pictures ──

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Motion.FinishAll();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Eyeball probe: key phone pages in Arabic, to PNGs. Runs only with
    /// NOCTIS_TEST_SKIA=1 and NOCTIS_RTL_SHOTS=&lt;dir&gt;.</summary>
    [AvaloniaFact]
    public void Shots_KeyPagesInArabic()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_RTL_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        var app = Application.Current!;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
        catch (ArgumentException) { }
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        new MobileTheme().Apply(app, "Dark", MobileTheme.DefaultAccent);

        var songs = new[]
        {
            MobileFixtures.Song("أنا لحبيبي", "فيروز", plays: 9), MobileFixtures.Song("Hello", "Adele", plays: 4),
            MobileFixtures.Song("¿Cuál Es Tu Plan? (feat. DJ Nelson)", "Bad Bunny, Ñejo & Pj Sin Suela, DJ Nelson", plays: 8),
            MobileFixtures.Song("قارئة الفنجان", "عبد الحليم حافظ", plays: 7), MobileFixtures.Song("Yesterday", "The Beatles", plays: 2),
        };
        var album = MobileFixtures.MakeAlbum("أغاني الحب", "فيروز", songs);
        using var rig = MobileFixtures.MakeRig(songs, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out _, 412, 915);
        try
        {
            SetLanguage(rig.Shell, "ar");
            void Shot(string name)
            {
                Pump(400);
                var path = Path.Combine(dir, $"rtl-{name}.png");
                window.CaptureRenderedFrame()!.Save(path);
                _o.WriteLine(path);
            }
            Shot("home");
            rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
            Shot("library");
            rig.Shell.OpenSongsCommand.Execute(null);
            Shot("songs");
            rig.Shell.OpenAlbumCommand.Execute(album);
            Shot("album");
            // Home with the mini player on a long Latin title (S23, 11-23: its ticker showed two copies).
            var scroll = MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled;
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = true;
            rig.Shell.Player.PlayTracks(songs, 2);
            rig.Shell.SelectTabCommand.Execute(MobileTab.Home);
            Shot("home-miniplayer");
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = scroll;
            rig.Shell.Player.PlayTracks(songs, 0);
            rig.Shell.OpenNowPlayingCommand.Execute(null);
            Shot("nowplaying");
            void Lyrics(string name, params string[] lines)
            {
                var lyrics = rig.Shell.Lyrics;
                var models = lines.Select(t => new Noctis.Models.LyricLine { Text = t }).ToList();
                LyricsPageViewModel.ApplyLineDirections(models);
                lyrics.Lines.ReplaceAll(models);
                lyrics.HasLyrics = true;
                if (!rig.Shell.IsLyricsOpen) rig.Shell.ToggleLyricsCommand.Execute(null);
                Shot(name);
            }
            Lyrics("lyrics-arabic", "أنا لحبيبي وحبيبي إلي", "يا عصفورة بيضا", "لا بقى تسألي", "لا تعتبي عليّ يا حبيبي");
            Lyrics("lyrics-latin", "Hello, it's me", "I was wondering if after all these years", "You'd like to meet");
            rig.Shell.ToggleLyricsCommand.Execute(null);
            rig.Shell.ToggleQueueCommand.Execute(null);
            Shot("queue");
            rig.Shell.TryHandleBack();
            rig.Shell.CloseNowPlayingCommand.Execute(null);
            rig.Shell.NavigateBackCommand.Execute(null);
            rig.Shell.NavigateBackCommand.Execute(null);
            rig.Shell.OpenArtistsCommand.Execute(null);
            Shot("artists-miniplayer");
            rig.Shell.OpenSettingsCommand.Execute(null);
            Shot("settings");
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }
}
