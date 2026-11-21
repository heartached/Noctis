using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A sweeping word moves only its brush stops, which are composition resources: the screen
/// follows without the word being drawn again. Redrawing every word every frame re-recorded
/// its draw list, resource table and glyph runs, most of the ~4 MB a second the phone's
/// lyrics allocated (S23, 11-14).
/// </summary>
public class KaraokeWordRedrawTests
{
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

    private static (CountingWord Word, Window Window) Show(bool emphasis = false)
    {
        var word = new CountingWord
        {
            Text = "tellin'", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            Progress = 0, BaseOpacity = 0.4, IsEmphasis = emphasis, HeldDurationMs = emphasis ? 2000 : 0,
        };
        var win = new Window { Width = 300, Height = 120, Background = Brushes.Black, Content = new Border { Padding = new Thickness(30), Child = word } };
        win.Show();
        Pump();
        Pump();
        return (word, win);
    }

    /// <summary>The edge crossing a word redraws it twice at most: as the edge comes onto it (one
    /// colour to the gradient) and as it leaves (back to one colour, see the next test). In
    /// between only the stops move.</summary>
    [AvaloniaFact]
    public void Sweep_DoesNotRedrawTheWord()
    {
        var (word, win) = Show();
        try
        {
            word.Progress = -2;
            Pump();
            var draws = word.Draws;
            for (var i = 0; i <= 60; i++)
            {
                word.Progress = -2 + i / 15.0;
                Pump();
            }
            Assert.InRange(word.Draws - draws, 1, 2);
            draws = word.Draws;
            word.BaseOpacity = 1;
            Pump();
            Assert.Equal(draws, word.Draws);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>
    /// A word the edge is not on, in one opaque colour (sung, or on a line not being sung), is
    /// drawn with a solid brush: a gradient brush is a new Skia shader (and its managed handle)
    /// every frame the word is drawn, which for every word on the lyrics page was the largest
    /// part of what the phone allocated while a song played (headless Skia, 11-17: 40% of the
    /// frame's allocation). The translucent unsung ghost keeps the gradient (see the next test).
    /// </summary>
    [AvaloniaFact]
    public void AWordTheEdgeIsNotOn_IsDrawnInOneColour()
    {
        var (word, win) = Show();
        try
        {
            word.Progress = KaraokeSweep.InertPast;
            Pump();
            Assert.True(word.DrawnUniform);
            word.Progress = 0.5;
            Pump();
            Assert.False(word.DrawnUniform);
            word.Progress = KaraokeSweep.InertFuture;
            Pump();
            Assert.False(word.DrawnUniform);   // the 40% ghost
            word.BaseOpacity = 1;              // a line not being sung
            Pump();
            Assert.True(word.DrawnUniform);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): drawn in one opaque colour, a word looks as
    /// its gradient drew it (measured 202544 vs 202496 summed red). Translucent it would not
    /// (75848 vs 81098: Skia picks a solid paint's glyph gamma from its colour), so the ghost
    /// stays on the gradient.</summary>
    [AvaloniaFact]
    public void OneColour_LooksLikeTheGradient()
    {
        if (!HeadlessTestApp.RealRendering) return;
        foreach (var (progress, baseOpacity) in new[] { (KaraokeSweep.InertPast, 0.4), (KaraokeSweep.InertFuture, 1.0) })
        {
            var (word, win) = Show();
            try
            {
                word.BaseOpacity = baseOpacity;
                word.Progress = progress;
                Pump();
                Assert.True(word.DrawnUniform);
                var solid = Brightness(win);
                word.ForceGradient = true;
                word.InvalidateVisual();
                Pump();
                Assert.False(word.DrawnUniform);
                var gradient = Brightness(win);
                Assert.True(Math.Abs(solid - gradient) <= gradient * 0.001, $"solid {solid:F0} vs gradient {gradient:F0}");
            }
            finally
            {
                win.Close();
            }
        }
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): the moved stops reach the screen.</summary>
    [AvaloniaFact]
    public void Sweep_ReachesTheScreen()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var (word, win) = Show();
        try
        {
            var before = Brightness(win);
            word.Progress = 1;
            Pump();
            var after = Brightness(win);
            Assert.True(after > before * 1.8, $"lit {after:F0} vs unlit {before:F0}");
        }
        finally
        {
            win.Close();
        }
    }


    private static double Brightness(Window win)
    {
        using var bmp = win.CaptureRenderedFrame()!;
        using var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        using var sk = SkiaSharp.SKBitmap.Decode(ms);
        double sum = 0;
        for (var y = 0; y < sk.Height; y++)
            for (var x = 0; x < sk.Width; x++)
                sum += sk.GetPixel(x, y).Red;
        return sum;
    }
}
