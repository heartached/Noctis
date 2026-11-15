using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
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

    [AvaloniaFact]
    public void Sweep_DoesNotRedrawTheWord()
    {
        var (word, win) = Show();
        try
        {
            var draws = word.Draws;
            for (var i = 1; i <= 20; i++)
            {
                word.Progress = i / 20.0;
                Pump();
            }
            word.BaseOpacity = 1;
            Pump();
            Assert.Equal(draws, word.Draws);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>The float redraws only when it moves a quarter device pixel (the finest step
    /// Skia draws it at): 0.055 em of 30 at scale 1 is 2 px, eight quarters.</summary>
    [AvaloniaFact]
    public void Lift_RedrawsOnlyPerQuarterPixel()
    {
        var (word, win) = Show();
        try
        {
            var draws = word.Draws;
            for (var i = 1; i <= 100; i++)
            {
                word.Lift = i / 100.0;
                Pump();
            }
            var redraws = word.Draws - draws;
            Assert.InRange(redraws, 8, 10);   // eight quarters, plus leaving and reaching the grid
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>A held note's letters follow its clock: those frames still redraw.</summary>
    [AvaloniaFact]
    public void HeldNote_StillRedrawsAsItsLettersMove()
    {
        var (word, win) = Show(emphasis: true);
        try
        {
            var draws = word.Draws;
            for (var i = 1; i <= 10; i++)
            {
                word.EmphasisMs = i * 50;
                Pump();
            }
            Assert.Equal(draws + 10, word.Draws);
        }
        finally
        {
            win.Close();
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
