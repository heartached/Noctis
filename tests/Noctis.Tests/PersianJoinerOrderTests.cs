using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// #269 live finding: in a word-synced line "می‌خواهم" showed as "خواهم‌می". The word cell laid
/// its word out in a left-to-right paragraph, where Avalonia gives the joiner (ZWNJ U+200C, ZWJ
/// U+200D) the paragraph's level (0) in a run of its own instead of its neighbours' (1), so the
/// two right-to-left halves were placed left to right. Measured with the app's font stack (Inter
/// SemiBold, Vazirmatn fallback) through Avalonia's Skia font manager; the order checks run
/// headless and with NOCTIS_TEST_SKIA=1 (HarfBuzz).
/// </summary>
public class PersianJoinerOrderTests
{
    private const string Inter = "avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold";

    private readonly ITestOutputHelper _out;
    public PersianJoinerOrderTests(ITestOutputHelper output) => _out = output;

    /// <summary>Words, and two characters of each with the one that must be drawn to the right.</summary>
    public static TheoryData<string, int, int> Words => new()
    {
        { "می‌خواهم", 0, 3 },            // ZWNJ: می right of خواهم
        { "نمی‌دانم", 0, 4 },             // ZWNJ
        { "می‍خواهم", 0, 3 },            // ZWJ
        { "مُحَمَّد", 0, 7 },                  // diacritics: م (first) right of د (last)
        { "سال۱۳۹۹", 0, 3 },                   // letters then Persian digits: س right of the digits
        { "عزیزمbaby", 0, 5 },                 // Latin attached: ع right of b
    };

    private static void Check(TextLayout layout, string word, int right, int left, string where, ITestOutputHelper log)
    {
        var xr = layout.HitTestTextPosition(right).X;
        var xl = layout.HitTestTextPosition(left).X;
        log.WriteLine($"{where} '{word}': '{word[right]}' at {xr:F1}, '{word[left]}' at {xl:F1}");
        Assert.True(xr > xl, $"{where} '{word}': '{word[right]}' ({xr:F1}) must be right of '{word[left]}' ({xl:F1})");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Words))]
    public void AWordCell_DrawsAPersianWord_InItsOrder(string word, int right, int left)
    {
        PersianLyricsFontTests.WithFontManager(PersianLyricsFontTests.SkiaFontManager(ScriptFonts.Options()), () =>
        {
            // Twice: Avalonia's caches decided the run split (the first layout was right by luck).
            for (var pass = 0; pass < 2; pass++)
            {
                var cell = new KaraokeWordText { Text = word + " ", FontWeight = FontWeight.Bold, FontSize = 40, RightToLeft = true };
                TextElement.SetFontFamily(cell, new FontFamily(Inter));
                var win = new Window { Width = 400, Height = 200, Content = cell };
                try
                {
                    win.Show();
                    win.UpdateLayout();
                    Check(cell.WordLayout!, word, right, left, $"cell pass {pass}", _out);
                }
                finally { win.Close(); }
            }
        });
    }

    [AvaloniaFact]
    public void TheLyricsPage_WordSyncedAndPlainPersianLines_KeepTheirWordsInOrder()
    {
        PersianLyricsFontTests.WithFontManager(PersianLyricsFontTests.SkiaFontManager(ScriptFonts.Options()), () =>
        {
            var vm = LongLyricsKit.MakeViewModel();
            vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(
                "[00:01.00]<00:01.00>می‌خواهم <00:01.60>با <00:02.00>تو <00:02.40>باشم\n" +
                "[00:04.00]نمی‌دانم کجا هستی\n"));
            var view = new LyricsView { DataContext = vm };
            TextElement.SetFontFamily(view, new FontFamily(Inter));
            var win = new Window { Width = 1280, Height = 800, Content = view };
            try
            {
                win.Show();
                RtlLyricLayersTests.PumpFor(200);
                var cells = view.GetVisualDescendants().OfType<KaraokeWordText>()
                    .Where(k => k.Text?.Contains('‌') == true).ToList();
                Assert.NotEmpty(cells);
                foreach (var cell in cells) Check(cell.WordLayout!, cell.Text!.TrimEnd(), 0, 3, "page cell", _out);
                var plain = view.GetVisualDescendants().OfType<LyricTextBlock>().First(t => t.Text?.StartsWith("نمی") == true);
                Assert.Equal(FlowDirection.RightToLeft, plain.FlowDirection);
                Check(plain.TextLayout, plain.Text!, 0, 4, "page plain line", _out);
            }
            finally { win.Close(); }
        });
    }

    [AvaloniaFact]
    public void ThePhone_WordCell_KeepsAPersianWordInOrder()
    {
        PersianLyricsFontTests.WithFontManager(PersianLyricsFontTests.SkiaFontManager(ScriptFonts.Options()), () =>
        {
            var (view, window, lyrics) = RtlLyricLayersTests.MountPhone(
                "[00:01.00]<00:01.00>می‌خواهم <00:01.60>با <00:02.00>تو <00:02.40>باشم\n");
            try
            {
                var cell = view.GetVisualDescendants().OfType<KaraokeWordText>()
                    .First(k => k.Classes.Contains("word-sweep") && k.Text?.Contains('‌') == true);
                Assert.True(cell.RightToLeft);
                Check(cell.WordLayout!, cell.Text!.TrimEnd(), 0, 3, "phone cell", _out);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AWord_IsLaidOutInItsOwnDirection()
    {
        Assert.Equal(FlowDirection.RightToLeft, KaraokeWordText.ParagraphOf("می‌خواهم"));
        Assert.Equal(FlowDirection.RightToLeft, KaraokeWordText.ParagraphOf("«عزیزم»"));
        Assert.Equal(FlowDirection.LeftToRight, KaraokeWordText.ParagraphOf("baby"));
        Assert.Equal(FlowDirection.LeftToRight, KaraokeWordText.ParagraphOf("۱۳۹۹"));   // digits: no letter
    }
}
