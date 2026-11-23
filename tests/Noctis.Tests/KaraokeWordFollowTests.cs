using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's word cells follow their <see cref="WordTiming"/> (KaraokeWordText.Word) instead
/// of binding its values: no binding listens to the word, so its per-frame changes reach the
/// cell without event args, boxing or a weak-event relay, and the float moves the cell's
/// composition visual instead of recording the word again. It must look exactly as a bound
/// cell does (the desktop's, unchanged).
/// </summary>
public class KaraokeWordFollowTests
{
    private readonly ITestOutputHelper _o;
    public KaraokeWordFollowTests(ITestOutputHelper o) => _o = o;

    private sealed class CountingWord : KaraokeWordText
    {
        public int Draws;

        protected override void RenderTextLayout(DrawingContext context, Point origin)
        {
            Draws++;
            base.RenderTextLayout(context, origin);
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Window Host(KaraokeWordText word)
    {
        var win = new Window { Width = 200, Height = 120, Background = Brushes.Black, Content = new Border { Padding = new Thickness(40), Child = word } };
        win.Show();
        Pump();
        Pump();
        return win;
    }

    private static WordTiming Word(string text = "had") => new()
    {
        Text = text, SweepLeadIn = true, SweepTrailOut = false, IsEmphasis = false, HeldDurationMs = 300, IsLastInLine = true,
        Progress = 1,
    };

    [AvaloniaFact]
    public void AFollowedWord_TakesItsTextAndFlags_AndFollowsItsValues()
    {
        var model = Word("tellin'");
        var cell = new KaraokeWordText { FontSize = 30, Word = model };
        var win = Host(cell);
        try
        {
            Assert.Equal("tellin'", cell.Text);
            Assert.True(cell.LeadIn);
            Assert.False(cell.TrailOut);
            Assert.True(cell.IsLastInLine);
            Assert.Equal(300, cell.HeldDurationMs);
            model.Progress = 0.4;
            model.EmphasisMs = 120;
            Assert.Equal(0.4, cell.Progress);
            Assert.Equal(120, cell.EmphasisMs);

            // A row handed the next line hands the cell the next word.
            var next = Word("you");
            cell.Word = next;
            Assert.Equal("you", cell.Text);
            model.Progress = 0.9;
            Assert.Equal(1, cell.Progress);   // the old word no longer reaches it
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>A rising word moves its composition visual by the float, a quarter device pixel
    /// at a time, and is drawn only as the rise starts (off the pixel grid) and ends (back on
    /// it); a bound word was recorded again for every quarter pixel.</summary>
    [AvaloniaFact]
    public void TheFloat_MovesTheVisual_AndRedrawsOnlyAtItsStartAndEnd()
    {
        var model = Word();
        model.Lift = 0;
        var cell = new CountingWord { FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 1, Word = model };
        var win = Host(cell);
        var bound = new CountingWord { Text = "had", FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 1, Progress = 1 };
        var boundWin = Host(bound);
        try
        {
            var visual = ElementComposition.GetElementVisual(cell)!;
            var draws = cell.Draws;
            var boundDraws = bound.Draws;
            for (var i = 0; i <= 20; i++)
            {
                model.Lift = i / 20.0;
                bound.Lift = i / 20.0;
                Pump();
            }
            _o.WriteLine($"rise: followed {cell.Draws - draws} draws, bound {bound.Draws - boundDraws}");
            Assert.InRange(cell.Draws - draws, 1, 2);
            Assert.True(bound.Draws - boundDraws > 4);
            // At rest: 0.055 em of 35 px rounded to whole pixels (2), drawn on the grid.
            Assert.Equal(-2, visual.Translation.Y, 6);
            model.Lift = 0;
            Pump();
            Assert.Equal(0, visual.Translation.Y, 6);
        }
        finally
        {
            win.Close();
            boundWin.Close();
        }
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): through the rise, a followed word is drawn
    /// pixel for pixel as a bound one.</summary>
    [AvaloniaFact]
    public void AFollowedWord_LooksExactlyLikeABoundOne()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var model = Word();
        var cell = new KaraokeWordText { FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 0.4, Word = model };
        var bound = new KaraokeWordText
        {
            Text = "had", FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 0.4,
            LeadIn = true, IsLastInLine = true, HeldDurationMs = 300,
        };
        var win = Host(cell);
        var boundWin = Host(bound);
        try
        {
            var worst = 0;
            for (var i = 0; i <= 12; i++)
            {
                model.Progress = bound.Progress = i / 10.0 - 0.1;
                model.Lift = bound.Lift = i / 12.0;
                Pump();
                Pump();
                worst = Math.Max(worst, Diff(win, boundWin));
            }
            _o.WriteLine($"largest channel difference: {worst}");
            Assert.InRange(worst, 0, 2);
        }
        finally
        {
            win.Close();
            boundWin.Close();
        }
    }

    private static WordTiming Held(string text = "love") => new()
    {
        Text = text, SweepLeadIn = true, SweepTrailOut = true, IsEmphasis = true, HeldDurationMs = 2400, IsLastInLine = true,
        Progress = KaraokeSweep.InertFuture,
    };

    /// <summary>A followed held note's letters are composition visuals of their own: through the
    /// hold each is drawn only as the hold starts and ends (off the pixel grid while it moves),
    /// and its pose is its visual's transform; a bound word drew every letter every frame.</summary>
    [AvaloniaFact]
    public void AHeldNote_PosesItsLettersOnTheCompositor_AndDrawsThemOnlyAtItsStartAndEnd()
    {
        var model = Held();
        var cell = new CountingWord { FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 0.4, Word = model };
        var win = Host(cell);
        var bound = new CountingWord
        {
            Text = "love", FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 0.4,
            LeadIn = true, TrailOut = true, IsEmphasis = true, HeldDurationMs = 2400, IsLastInLine = true,
        };
        var boundWin = Host(bound);
        try
        {
            Assert.True(cell.LettersOnCompositor);
            Assert.False(bound.LettersOnCompositor);
            var letterDraws = cell.LetterDraws;
            var draws = cell.Draws;
            var boundDraws = bound.Draws;
            var frames = 0;
            for (var ms = 0.0; ms <= 3200; ms += 1000 / 60.0, frames++)
            {
                model.EmphasisMs = bound.EmphasisMs = ms;
                model.Progress = bound.Progress = Math.Clamp(ms / 2400, 0, 1);
                Pump();
            }
            model.EmphasisMs = bound.EmphasisMs = double.NaN;
            model.Progress = bound.Progress = KaraokeSweep.InertPast;
            Pump();
            _o.WriteLine($"a {frames}-frame hold: followed word {cell.Draws - draws} draws, its 4 letters {cell.LetterDraws - letterDraws} draws; bound word {bound.Draws - boundDraws} draws");
            Assert.InRange(cell.Draws - draws, 0, 2);
            // Each letter at the start and end of the hold, and when the sweep's edge comes onto
            // and leaves it (its brush switches between solid and gradient).
            Assert.InRange(cell.LetterDraws - letterDraws, 4, 4 * 4 + 2);
            Assert.True(bound.Draws - boundDraws > frames / 2);
        }
        finally
        {
            win.Close();
            boundWin.Close();
        }
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): through a held note (the letters swelling,
    /// pushed and lifted on the compositor, the sweep crossing them) a followed word is drawn as
    /// a bound one draws its letters itself.</summary>
    [AvaloniaFact]
    public void AFollowedHeldNote_LooksLikeABoundOne()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var model = Held();
        var cell = new KaraokeWordText { FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 0.4, Word = model };
        var bound = new KaraokeWordText
        {
            Text = "love", FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, BaseOpacity = 0.4,
            LeadIn = true, TrailOut = true, IsEmphasis = true, HeldDurationMs = 2400, IsLastInLine = true,
        };
        var win = Host(cell);
        var boundWin = Host(bound);
        try
        {
            Assert.True(cell.LettersOnCompositor);
            var worst = 0;
            var worstMean = 0.0;
            foreach (var ms in new[] { double.NaN, 0, 150, 400, 800, 1200, 1700, 2300, 2900, 3400 })
            {
                model.EmphasisMs = bound.EmphasisMs = ms;
                model.Progress = bound.Progress = double.IsNaN(ms) ? KaraokeSweep.InertFuture : Math.Clamp(ms / 2400, 0, 1);
                Pump();
                Pump();
                var d = Diff(win, boundWin, out var mean);
                _o.WriteLine($"at {ms} ms: largest {d}, mean {mean:0.000}");
                worst = Math.Max(worst, d);
                worstMean = Math.Max(worstMean, mean);
            }
            _o.WriteLine($"largest channel difference: {worst}, largest mean {worstMean:0.000}");
            // Mostly pixel for pixel (0). The drawn path pushes a letter's sub-pixel shear (see
            // Unsnapped) after its swell, the compositor before it: at a few poses a glyph lands
            // a quarter pixel apart, a soft edge pixel up to ~40 levels off, never more.
            Assert.InRange(worst, 0, 64);
            Assert.InRange(worstMean, 0, 0.5);
        }
        finally
        {
            win.Close();
            boundWin.Close();
        }
    }

    private static int Diff(Window a, Window b) => Diff(a, b, out _);

    /// <summary>The largest channel difference, and the mean one over every pixel.</summary>
    private static int Diff(Window a, Window b, out double mean)
    {
        using var x = Decode(a);
        using var y = Decode(b);
        var worst = 0;
        long sum = 0;
        for (var j = 0; j < x.Height; j++)
            for (var i = 0; i < x.Width; i++)
            {
                SkiaSharp.SKColor p = x.GetPixel(i, j), q = y.GetPixel(i, j);
                var d = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                worst = Math.Max(worst, d);
                sum += d;
            }
        mean = sum / (double)(x.Width * x.Height);
        return worst;
    }

    private static SkiaSharp.SKBitmap Decode(Window window)
    {
        using var bmp = window.CaptureRenderedFrame()!;
        using var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return SkiaSharp.SKBitmap.Decode(ms);
    }
}
