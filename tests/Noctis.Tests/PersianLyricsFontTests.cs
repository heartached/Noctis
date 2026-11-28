using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Converters;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #269: Persian lyrics "have bad fonts". The app's face (Inter SemiBold, every TextBlock)
/// has no Arabic letters, so each Persian run fell to whatever face the platform's fallback picked.
/// Arabic-script text now resolves to the bundled Vazirmatn UI (ScriptFonts) at the lyric weights,
/// and a Persian line reads from the right without a lyrics plugin.
/// </summary>
public class PersianLyricsFontTests
{
    private const string InterSemiBold = "avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold";
    private const string Vazirmatn = "Vazirmatn UI NL";

    // گ پ ی چ ژ ۱ and Arabic ع: the letters Persian adds to Arabic, a Persian digit, an Arabic letter.
    private static readonly int[] PersianCodepoints = { 0x06AF, 0x067E, 0x06CC, 0x0686, 0x0698, 0x06F1, 0x0639 };

    private readonly ITestOutputHelper _out;
    public PersianLyricsFontTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A font manager over Avalonia's real Skia font manager (the one the app runs with), with the
    /// given fallback options, whatever the headless session itself draws with.
    /// </summary>
    private static FontManager SkiaFontManager(FontManagerOptions? options)
    {
        var type = Type.GetType("Avalonia.Skia.FontManagerImpl, Avalonia.Skia", throwOnError: true)!;
        var impl = (IFontManagerImpl)Activator.CreateInstance(type, nonPublic: true)!;
        var previous = GetService<FontManagerOptions>();
        Bind(options);
        try { return new FontManager(impl); }
        finally { Bind(previous); }
    }

    // AvaloniaLocator is private API, hidden from the reference assembly (MobileSmoothMotionTests).
    private static object Locator(string property) => typeof(AvaloniaLocator)
        .GetProperty(property, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;

    private static T? GetService<T>() where T : class
    {
        var locator = Locator("Current");
        return (T?)locator.GetType().GetMethod("GetService", new[] { typeof(Type) })!.Invoke(locator, new object[] { typeof(T) });
    }

    private static void Bind<T>(T? value) where T : class
    {
        var locator = Locator("CurrentMutable");
        var bind = locator.GetType().GetMethods()
            .First(m => m.Name == "Bind" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var helper = bind.MakeGenericMethod(typeof(T)).Invoke(locator, null)!;
        var toConstant = helper.GetType().GetMethods().First(m => m.Name == "ToConstant" && m.IsGenericMethodDefinition);
        toConstant.MakeGenericMethod(typeof(T)).Invoke(helper, new object?[] { value });
    }

    /// <summary>Runs <paramref name="body"/> with <see cref="FontManager.Current"/> swapped for
    /// <paramref name="fontManager"/>, so TextLayout and TextBlock resolve fonts through it.</summary>
    private static void WithFontManager(FontManager fontManager, Action body)
    {
        var previous = GetService<FontManager>();
        Bind(fontManager);
        try { body(); }
        finally { Bind(previous); }
    }

    [AvaloniaFact]
    public void TheAppFace_InterSemiBold_HasNoPersianLetters()
    {
        var fm = SkiaFontManager(null);
        Assert.True(fm.TryGetGlyphTypeface(new Typeface(InterSemiBold), out var inter));
        Assert.Equal("Inter", inter!.FamilyName.Split(' ')[0]);
        foreach (var cp in PersianCodepoints)
            Assert.False(inter.CharacterToGlyphMap.TryGetGlyph(cp, out _), $"U+{cp:X4}");
    }

    [AvaloniaFact]
    public void Before_WithoutTheBundledFace_ThePlatformFallbackPickedTheFace()
    {
        // The app's state before #269 on Windows (no FontManagerOptions there): every Persian
        // letter goes to the platform's own fallback. Logged as the evidence of what it picked.
        var fm = SkiaFontManager(null);
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold })
        foreach (var cp in PersianCodepoints)
        {
            var matched = fm.TryMatchCharacter(cp, FontStyle.Normal, weight, FontStretch.Normal,
                new FontFamily(InterSemiBold), CultureInfo.GetCultureInfo("en-US"), out var typeface);
            var face = matched && fm.TryGetGlyphTypeface(typeface, out var gt) ? $"{gt!.FamilyName} {(int)gt.Weight}" : "none";
            _out.WriteLine($"{Environment.OSVersion.Platform} U+{cp:X4} at {weight}: {face}");
            Assert.DoesNotContain("Vazirmatn", face);
        }
    }

    [AvaloniaTheory]
    [InlineData(400, 600)]   // a control's default weight: the app draws it SemiBold, so does Persian
    [InlineData(600, 600)]   // the app's SemiBold
    [InlineData(700, 700)]   // the lyrics (FontWeight="Bold")
    public void PersianLetters_InTheLyricsFontStack_ResolveToTheBundledVazirmatn(int requested, int drawn)
    {
        var fm = SkiaFontManager(ScriptFonts.Options());
        foreach (var cp in PersianCodepoints)
        {
            Assert.True(fm.TryMatchCharacter(cp, FontStyle.Normal, (FontWeight)requested, FontStretch.Normal,
                new FontFamily(InterSemiBold), CultureInfo.GetCultureInfo("en-US"), out var typeface), $"U+{cp:X4}");
            Assert.Equal(Vazirmatn, typeface.FontFamily.Name);
            Assert.True(fm.TryGetGlyphTypeface(typeface, out var gt));
            Assert.Equal(drawn, (int)gt!.Weight);
            Assert.Equal(FontSimulations.None, gt.FontSimulations);
            Assert.True(gt.CharacterToGlyphMap.TryGetGlyph(cp, out _), $"U+{cp:X4}");
        }
    }

    [AvaloniaTheory]
    [InlineData(400, 600)]
    [InlineData(700, 700)]   // the phone's lyric lines (FontWeight="Bold")
    public void OnThePhone_PersianInItsInterFamily_ResolvesToTheBundledVazirmatn(int requested, int drawn)
    {
        // The phone (NoctisApplication: WithInterFont + ScriptFonts) gives its text the registered
        // Inter family with real weights (MobileStyles: "fonts:Inter#Inter, $Default").
        var fm = SkiaFontManager(ScriptFonts.Options());
        fm.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection());
        var family = FontFamily.Parse("fonts:Inter#Inter, $Default");
        foreach (var cp in PersianCodepoints)
        {
            Assert.True(fm.TryMatchCharacter(cp, FontStyle.Normal, (FontWeight)requested, FontStretch.Normal,
                family, CultureInfo.GetCultureInfo("en-US"), out var typeface), $"U+{cp:X4}");
            Assert.Equal(Vazirmatn, typeface.FontFamily.Name);
            Assert.True(fm.TryGetGlyphTypeface(typeface, out var gt));
            Assert.Equal(drawn, (int)gt!.Weight);
        }
    }

    [AvaloniaFact]
    public void OnMacOsAndLinux_TheArabicFaceComesBeforeThePlatformFaces_WhichKeepTheirOrder()
    {
        var fallbacks = ScriptFonts.Options("PingFang SC", "Apple Color Emoji").FontFallbacks!;
        Assert.Equal(3, fallbacks.Count);
        Assert.Equal(Vazirmatn, fallbacks[0].FontFamily.Name);
        Assert.True(fallbacks[0].UnicodeRange.IsInRange(0x06AF));
        Assert.False(fallbacks[0].UnicodeRange.IsInRange('a'));
        Assert.False(fallbacks[0].UnicodeRange.IsInRange(0x05D0));   // Hebrew: not Vazirmatn's
        Assert.Equal("PingFang SC", fallbacks[1].FontFamily.Name);
        Assert.Equal("Apple Color Emoji", fallbacks[2].FontFamily.Name);
    }

    [AvaloniaFact]
    public void ALyricLine_ThroughTheTextFormatter_DrawsItsPersianInVazirmatn_AndItsEnglishInInter()
    {
        // The path every lyric surface takes (TextBlock, KaraokeWordText and KaraokeRestingWords
        // all lay out with TextLayout), at the lyrics' Bold. "می‌خواهم" carries a ZWNJ (U+200C),
        // which Avalonia hands back to the line's own face between the two Persian runs: the
        // letters on both sides stay Vazirmatn and the ZWNJ itself takes no room.
        var fm = SkiaFontManager(ScriptFonts.Options());
        WithFontManager(fm, () =>
        {
            const string line = "می‌خواهم love تو را";
            using var layout = new TextLayout(line, new Typeface(InterSemiBold, FontStyle.Normal, FontWeight.Bold),
                40, Brushes.White, flowDirection: FlowDirection.RightToLeft);
            var runs = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().ToList();
            Assert.NotEmpty(runs);
            foreach (var run in runs)
            {
                var text = run.Text.ToString();
                var family = run.Properties.Typeface.FontFamily.Name;
                _out.WriteLine($"'{text}' -> {family} {run.Properties.Typeface.Weight}");
                if (text.Any(c => c is >= '؀' and <= 'ۿ'))
                    Assert.Equal(Vazirmatn, family);
                if (text.Any(char.IsAsciiLetter))
                    Assert.Equal("Inter SemiBold", family);
            }
            Assert.All(runs.Where(r => r.Text.ToString() == "‌"), r => Assert.True(r.Size.Width < 0.5, $"{r.Size}"));
        });
    }

    // ── Right to left ──

    [Fact]
    public void LineDirections_APersianSong_IsRightToLeft_EvenALineWithAnEnglishWord()
    {
        var lines = new[] { "می‌خواهم با تو باشم", "I love you عزیزم", "۱۲۳ ...", "دوستت دارم" };
        var rtl = new bool[lines.Length];
        TextDirection.LineDirections(lines, rtl);
        Assert.Equal(new[] { true, true, false, true }, rtl);
    }

    [Fact]
    public void LineDirections_AnEnglishSong_TurnsOnlyItsMostlyPersianLines()
    {
        var lines = new[] { "Hello darkness my old friend", "I love you عزیزم", "دوستت دارم baby", "Oh" };
        var rtl = new bool[lines.Length];
        TextDirection.LineDirections(lines, rtl);
        Assert.Equal(new[] { false, false, true, false }, rtl);
    }

    [Fact]
    public void PlainLineAlignment_IsMirroredForARightToLeftLine_LikeTheLineItself()
    {
        var text = new LyricLineTextAlignmentConverter();
        var line = new LyricLineAlignmentConverter();
        TextAlignment T(LyricVoice v, bool rtl) => (TextAlignment)text.Convert(new object?[] { v, rtl }, typeof(TextAlignment), null, CultureInfo.InvariantCulture)!;
        HorizontalAlignment H(LyricVoice v, bool rtl) => (HorizontalAlignment)line.Convert(new object?[] { v, rtl }, typeof(HorizontalAlignment), null, CultureInfo.InvariantCulture)!;
        foreach (var v in new[] { LyricVoice.Default, LyricVoice.Voice2, LyricVoice.Group })
        foreach (var rtl in new[] { false, true })
            Assert.Equal(H(v, rtl).ToString(), T(v, rtl).ToString());
        Assert.Equal(FlowDirection.RightToLeft, new LyricLineFlowDirectionConverter().Convert(true, typeof(FlowDirection), null, CultureInfo.InvariantCulture));
        Assert.Equal(FlowDirection.LeftToRight, new LyricLineFlowDirectionConverter().Convert(false, typeof(FlowDirection), null, CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void MixedPersianAndEnglish_InARightToLeftParagraph_RunsFromTheRight()
    {
        // "سلام Hello دنیا": read from the right, سلام is rightmost and دنیا leftmost. In a
        // left-to-right paragraph (every lyric line before #269) the two Persian words swap ends.
        const string line = "سلام Hello دنیا";
        double X(FlowDirection dir, int index)
        {
            using var layout = new TextLayout(line, new Typeface(InterSemiBold), 32, Brushes.White, flowDirection: dir);
            return layout.HitTestTextPosition(index).X;
        }
        int salam = 0, hello = line.IndexOf('H'), donya = line.IndexOf('د');
        Assert.True(X(FlowDirection.RightToLeft, salam) > X(FlowDirection.RightToLeft, hello));
        Assert.True(X(FlowDirection.RightToLeft, hello) > X(FlowDirection.RightToLeft, donya));
        Assert.True(X(FlowDirection.LeftToRight, salam) < X(FlowDirection.LeftToRight, donya));
    }

    private static LyricsViewModel MakeViewModel() => LongLyricsKit.MakeViewModel();

    private const string PersianWordSynced =
        "[00:01.00]<00:01.00>می‌خواهم <00:01.60>با <00:02.00>تو <00:02.40>باشم\n" +
        "[00:03.00]دوستت دارم baby, تا ابد\n" +
        "[00:05.00]<00:05.00>I <00:05.30>love <00:05.60>you <00:05.90>عزیزم";

    [AvaloniaFact]
    public void WithoutALyricsPlugin_PersianLines_AreRightToLeft_AndSoAreTheirWords()
    {
        Assert.Null(LyricsWordEffect.Current);
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(PersianWordSynced));
        Assert.All(vm.LyricLines.Where(l => !l.IsIntroPlaceholder), l => Assert.True(l.IsRightToLeft, l.Text));
        Assert.All(vm.LyricLines.Where(l => l.Words is not null).SelectMany(l => l.Words!), w => Assert.True(w.IsRightToLeft));

        vm.UnsyncedLines.ReplaceAll(new[] { new LyricLine { Text = "دوستت دارم" }, new LyricLine { Text = "Hello" } });
        Assert.True(vm.UnsyncedLines[0].IsRightToLeft);
        Assert.False(vm.UnsyncedLines[1].IsRightToLeft);

        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent("[00:01.00]<00:01.00>Hello <00:01.50>world"));
        Assert.All(vm.LyricLines, l => Assert.False(l.IsRightToLeft));
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void OnTheLyricsPage_APersianLine_SitsRight_ItsFirstWordRightmost_ItsPlainTextReadsFromTheRight()
    {
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(PersianWordSynced));
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800 };
        try
        {
            win.Show();
            win.Content = view;
            Pump();

            var buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("lyric-line-btn")).ToList();
            Assert.Equal(3, buttons.Count);
            Assert.All(buttons, b => Assert.Equal(HorizontalAlignment.Right, b.HorizontalAlignment));

            // Word-synced: the row runs from the right, so the first sung word is the rightmost.
            var row = buttons[0].GetVisualDescendants().OfType<LyricWrapPanel>().First();
            Assert.True(row.RightToLeft);
            var cells = row.Children.Where(c => c.IsVisible).ToList();
            Assert.True(cells.Count >= 2);
            Assert.True(cells[0].Bounds.X > cells[1].Bounds.X, $"{cells[0].Bounds} vs {cells[1].Bounds}");
            // Each word sweeps from its right edge (KaraokeWordText mirrors the band).
            var words = buttons[0].GetVisualDescendants().OfType<KaraokeWordText>().Where(w => w.DataContext is WordTiming).ToList();
            Assert.Equal(8, words.Count);   // four words, each a glow and a sweep layer
            Assert.All(words, w => Assert.True(w.RightToLeft));

            // Plain text: a right-to-left paragraph whose wrapped rows sit against the right edge.
            var plain = buttons[1].GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("lyric-line"));
            Assert.Equal(FlowDirection.RightToLeft, plain.FlowDirection);
            Assert.Equal(TextAlignment.Right, plain.TextAlignment);
            // Its runs from the right: دوستت دارم rightmost, then "baby", then تا ابد leftmost.
            var text = plain.Text!;
            double X(int i) => plain.TextLayout.HitTestTextPosition(i).X;
            Assert.True(X(0) > X(text.IndexOf('b')), $"{X(0)} vs {X(text.IndexOf('b'))}");
            Assert.True(X(text.IndexOf('b')) > X(text.IndexOf("تا", StringComparison.Ordinal)));
        }
        finally
        {
            win.Close();
        }
    }

    // ── Before / after picture (NOCTIS_TEST_SKIA=1 + NOCTIS_FARSI_SHOTS=<dir>) ──

    [AvaloniaFact]
    public void Shots_APersianLyricLine_BeforeAndAfter()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_FARSI_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var (name, options) in new[] { ("before", (FontManagerOptions?)null), ("after", ScriptFonts.Options()) })
        {
            WithFontManager(SkiaFontManager(options), () =>
            {
                var panel = new StackPanel { Margin = new Thickness(40), Spacing = 18 };
                foreach (var (text, weight, size) in new[]
                {
                    ("می‌خواهم با تو باشم، همیشه", FontWeight.Bold, 44.0),
                    ("دوستت دارم baby, تا ابد", FontWeight.Bold, 44.0),
                    ("Hello darkness, my old friend", FontWeight.Bold, 44.0),
                    ("گل پیچک ـ ژاله (۱۳۹۹)", FontWeight.Normal, 18.0),
                })
                    panel.Children.Add(new TextBlock
                    {
                        Text = text, FontWeight = weight, FontSize = size, Foreground = Brushes.White,
                        FontFamily = new FontFamily(InterSemiBold),
                        FlowDirection = TextDirection.FirstStrong(text) ?? FlowDirection.LeftToRight,
                        TextAlignment = TextAlignment.Right,
                    });
                var win = new Window { Width = 900, Height = 420, Background = new SolidColorBrush(Color.Parse("#1C1C22")), Content = panel };
                try
                {
                    win.Show();
                    Pump();
                    var path = Path.Combine(dir, $"persian-lyric-{name}.png");
                    win.CaptureRenderedFrame()!.Save(path);
                    _out.WriteLine(path);
                }
                finally { win.Close(); }
            });
        }
    }
}
