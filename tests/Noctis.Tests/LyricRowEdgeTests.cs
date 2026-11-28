using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// #269 follow-up: a wrapped lyric row aligned right ends with its last glyph at the edge, and a
/// centred one is centred on its glyphs. Avalonia aligned a row by its width with its trailing
/// space (plain text) and WrapPanel by its cells, the last of which ends in the word's space or
/// the line-final pad (word-synced lines), so right-aligned rows ended a space (or 24 px) short.
/// Checked for a right-to-left line, a second-voice duet line and a group line, plain and
/// word-synced, on the lyrics page, the side panel and the phone. Runs headless and with
/// NOCTIS_TEST_SKIA=1 (real shaping).
/// </summary>
public class LyricRowEdgeTests
{
    private const double Tolerance = 0.5;

    private readonly ITestOutputHelper _out;
    public LyricRowEdgeTests(ITestOutputHelper output) => _out = output;

    private const string Persian = "دوستت دارم تا ابد با تو می‌مانم در شب‌های بی‌پایان و روزهای روشن ما";
    private const string English = "I will stay with you until the end of all the days and nights we have together";

    private static string Timed(double at, string text)
    {
        var sb = new StringBuilder();
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var t = TimeSpan.FromSeconds(at + i * 0.3);
            sb.Append($"<{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}>{words[i]}{(i < words.Length - 1 ? " " : "")}");
        }
        return sb.ToString();
    }

    /// <summary>Plain and word-synced: a Persian line (right to left, against the right edge), a
    /// second-voice English line (against the right edge) and a group line (centred).</summary>
    private static readonly string Lrc =
        $"[00:01.00]{Persian} {Persian}\n" +
        $"[00:10.00]v2: {English}\n" +
        $"[00:20.00]v3: {English}\n" +
        $"[00:30.00]{Timed(30, Persian + " " + Persian)}\n" +
        $"[00:45.00]v2: {Timed(45, English)}\n" +
        $"[01:00.00]v3: {Timed(60, English)}\n";

    private enum Edge { Right, Centre }

    private static Edge Expected(LyricLine line) => line.Voice == LyricVoice.Group ? Edge.Centre : Edge.Right;

    /// <summary>Every visible row of <paramref name="line"/>'s block as (left, right) of its glyphs,
    /// in the block's coordinates, with what Avalonia alone would have drawn (before).</summary>
    private static List<(double Left, double Right, double Before)> Rows(Button block, LyricLine line)
    {
        var rows = new List<(double, double, double)>();
        if (line.HasWords)
        {
            var cells = block.GetVisualDescendants().OfType<KaraokeWordText>()
                .Where(w => w.Classes.Contains("word-sweep") && w.DataContext is WordTiming && w.IsEffectivelyVisible).ToList();
            if (cells.Count > 0)
            {
                foreach (var row in cells.GroupBy(c => Math.Round(c.TranslatePoint(default, block)!.Value.Y)))
                {
                    var spans = row.Select(c =>
                    {
                        var (l, r) = c.GlyphSpan;
                        return (L: c.TranslatePoint(new Point(l, 0), block)!.Value.X, R: c.TranslatePoint(new Point(r, 0), block)!.Value.X);
                    }).ToList();
                    rows.Add((spans.Min(s => s.L), spans.Max(s => s.R), double.NaN));
                }
                return rows;
            }
            var resting = block.GetVisualDescendants().OfType<KaraokeRestingWords>().Single(r => r.IsEffectivelyVisible);
            var count = line.Words!.Count;
            foreach (var row in Enumerable.Range(0, count).GroupBy(i => resting.CellOf(i).Y))
            {
                var left = row.Min(i => resting.GlyphSpan(i).Left);
                var right = row.Max(i => resting.GlyphSpan(i).Right);
                rows.Add((resting.TranslatePoint(new Point(left, 0), block)!.Value.X,
                          resting.TranslatePoint(new Point(right, 0), block)!.Value.X, double.NaN));
            }
            return rows;
        }
        var text = block.GetVisualDescendants().OfType<LyricTextBlock>().First(t => t.IsEffectivelyVisible && t.Text == line.Text);
        for (var i = 0; i < text.TextLayout.TextLines.Count; i++)
        {
            var (l, r) = text.VisibleRow(i);
            var shift = text.EdgeShift(text.TextLayout.TextLines[i]);
            rows.Add((text.TranslatePoint(new Point(l, 0), block)!.Value.X,
                      text.TranslatePoint(new Point(r, 0), block)!.Value.X,
                      text.TranslatePoint(new Point(r - shift, 0), block)!.Value.X));
        }
        return rows;
    }

    private void AssertEdges(Visual root, IEnumerable<LyricLine> lines, string surface)
    {
        foreach (var line in lines.Where(l => !l.IsIntroPlaceholder))
        {
            var block = root.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.DataContext, line) && b.IsEffectivelyVisible);
            var rows = Rows(block, line);
            var kind = $"{surface} {(line.HasWords ? "word-synced" : "plain")} {line.Voice}{(line.IsRightToLeft ? " RTL" : "")}";
            if (!line.HasWords) Assert.True(rows.Count >= 2, $"{kind}: wraps ({rows.Count} row)");
            var width = block.Bounds.Width;
            foreach (var (left, right, before) in rows)
            {
                _out.WriteLine($"{kind}: glyphs {left:F2}..{right:F2} of {width:F2}{(double.IsNaN(before) ? "" : $" (Avalonia alone: ..{before:F2})")}");
                if (Expected(line) == Edge.Right)
                    Assert.True(Math.Abs(right - width) <= Tolerance, $"{kind}: a row ends at {right:F2}, the edge is {width:F2}");
                else
                    Assert.True(Math.Abs((left + right) / 2 - width / 2) <= Tolerance,
                        $"{kind}: a row is centred at {(left + right) / 2:F2}, the block at {width / 2:F2}");
            }
        }
    }

    private static LyricsViewModel MakeLyrics()
    {
        var vm = LongLyricsKit.MakeViewModel();
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(Lrc));
        Assert.True(vm.LyricLines.First(l => l.Text.StartsWith("دوستت")).IsRightToLeft);
        return vm;
    }

    [AvaloniaFact]
    public void LyricsPage_WrappedRows_EndAtTheirEdge()
    {
        var vm = MakeLyrics();
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 1400, Content = view };
        try
        {
            win.Show();
            RtlLyricLayersTests.PumpFor(200);
            AssertEdges(view, vm.LyricLines, "page");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SidePanel_WrappedRows_EndAtTheirEdge()
    {
        var vm = MakeLyrics();
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 420, Height = 2400, Content = view };
        try
        {
            win.Show();
            RtlLyricLayersTests.PumpFor(200);
            AssertEdges(view, vm.LyricLines, "panel");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Phone_WrappedRows_EndAtTheirEdge()
    {
        var (view, window, lyrics) = RtlLyricLayersTests.MountPhone(Lrc);
        try
        {
            var list = RtlLyricLayersTests.PhoneList(view);
            // The phone builds the rows near the sung line; scroll each into place before checking it.
            var scroll = list.FindAncestorOfType<ScrollViewer>()!;
            foreach (var line in lyrics.Lines.Where(l => !l.IsIntroPlaceholder))
            {
                for (var tries = 0; tries < 40 && !list.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.DataContext, line)); tries++)
                {
                    scroll.Offset = new Vector(0, scroll.Offset.Y + 200);
                    RtlLyricLayersTests.PumpFor(30);
                }
                AssertEdges(list, new[] { line }, "phone");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, WrapPanelItemsAlignment.End)]
    [InlineData(false, WrapPanelItemsAlignment.Center)]
    [InlineData(true, WrapPanelItemsAlignment.Start)]
    [InlineData(true, WrapPanelItemsAlignment.End)]
    [InlineData(true, WrapPanelItemsAlignment.Center)]
    public void RestingWords_AlignRowsByTheirGlyphs_LikeTheLiveRow(bool rightToLeft, WrapPanelItemsAlignment alignment)
    {
        // The resting copy (long lyrics far from the sung line) draws the same words in the
        // same places as the live row, so it must leave the same blank out.
        var text = rightToLeft ? Persian : English;
        var parts = text.Split(' ');
        var words = parts.Select((w, i) => new WordTiming
        {
            Text = w + (i < parts.Length - 1 ? " " : ""),
            IsRightToLeft = rightToLeft,
            IsLastInLine = i == parts.Length - 1,
        }).ToList();
        var resting = new KaraokeRestingWords
        {
            Words = words, RightToLeft = rightToLeft, ItemsAlignment = alignment, Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var win = new Window { Width = 400, Height = 800, Content = resting };
        Avalonia.Controls.Documents.TextElement.SetFontSize(resting, 32);
        try
        {
            win.Show();
            win.UpdateLayout();
            var spans = Enumerable.Range(0, words.Count).Select(resting.GlyphSpan).ToList();
            var rows = Enumerable.Range(0, words.Count).GroupBy(i => resting.CellOf(i).Y).ToList();
            Assert.True(rows.Count >= 2, "the words wrap");
            var width = resting.Bounds.Width;
            foreach (var row in rows)
            {
                var left = row.Min(i => spans[i].Left);
                var right = row.Max(i => spans[i].Right);
                _out.WriteLine($"{(rightToLeft ? "RTL" : "LTR")} {alignment}: {left:F2}..{right:F2} of {width:F2}");
                var visualRight = rightToLeft ? alignment == WrapPanelItemsAlignment.Start : alignment == WrapPanelItemsAlignment.End;
                var visualLeft = rightToLeft ? alignment == WrapPanelItemsAlignment.End : alignment == WrapPanelItemsAlignment.Start;
                if (visualRight) Assert.True(Math.Abs(right - width) <= Tolerance, $"ends at {right:F2} of {width:F2}");
                else if (visualLeft) Assert.True(Math.Abs(left) <= Tolerance, $"starts at {left:F2}");
                else Assert.True(Math.Abs((left + right) / 2 - width / 2) <= Tolerance, $"centred at {(left + right) / 2:F2} of {width / 2:F2}");
            }
        }
        finally { win.Close(); }
    }
}
