using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The playback bar's EQ bars used to set Rectangle.Height on a 16 ms timer, so every tick
/// ran a layout pass (and a whole-window frame) on every page while music played. The bars
/// now repaint only (<see cref="EqBar"/>) at ~30 fps and must look exactly as before: the
/// same rect, where layout rounding put the old Rectangle, for every height.
/// </summary>
public class EqBarRepaintTests
{
    private const double BarWidth = 1.75;
    private const double Radius = 0.875;
    private const double RowHeight = 12;

    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    private static void Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    /// <summary>Heights the bars take: flat, the oscillation/level range, and flatten lerps.</summary>
    private static IEnumerable<double> Heights()
    {
        yield return 1.75;
        for (var level = 0.0; level <= 1.0001; level += 0.05)
            yield return EqVisualizer.HeightForLevel(level);
        foreach (var h in new[] { 1.8, 2.0, 2.49, 2.5, 2.51, 3.3333, 4.5, 6.02, 7.999, 8.5, 9.75, 10.0 })
            yield return h;
    }

    /// <summary>The template as it was before EqBar: five Rectangles, Height set directly.</summary>
    private static (StackPanel Panel, Rectangle[] Bars) OldBars(IBrush fill)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1.75,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Height = RowHeight,
        };
        var bars = new Rectangle[5];
        for (var i = 0; i < 5; i++)
        {
            bars[i] = new Rectangle
            {
                Width = BarWidth, Height = 1.75, RadiusX = Radius, RadiusY = Radius,
                VerticalAlignment = VerticalAlignment.Center, Fill = fill,
            };
            panel.Children.Add(bars[i]);
        }
        return (panel, bars);
    }

    private static EqVisualizer NewEq(IBrush fill) => new() { Width = 34, Height = 22, Foreground = fill };

    private static Window Host(Control content)
    {
        var win = new Window { Width = 120, Height = 60, Background = Brushes.Black, Content = content };
        win.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis/Controls/EqVisualizer.axaml"),
        });
        win.Show();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return win;
    }

    private static EqBar[] BarsOf(EqVisualizer eq)
    {
        var bars = eq.GetVisualDescendants().OfType<EqBar>().ToArray();
        Assert.Equal(5, bars.Length);
        return bars;
    }

    [AvaloniaFact]
    public void EqBar_DrawsTheOldRectanglesBounds_ForEveryHeight()
    {
        var (oldPanel, oldBars) = OldBars(Brushes.White);
        var eq = NewEq(Brushes.White);
        var oldWin = Host(new Border { Width = 34, Height = 22, Child = oldPanel });
        var newWin = Host(eq);
        try
        {
            var bars = BarsOf(eq);
            var newPanel = bars[0].GetVisualParent<StackPanel>()!;
            foreach (var h in Heights())
            {
                foreach (var r in oldBars) r.Height = h;
                foreach (var b in bars) b.BarHeight = h;
                oldWin.UpdateLayout();
                newWin.UpdateLayout();

                Assert.Equal(oldPanel.Bounds.Size, newPanel.Bounds.Size);
                for (var i = 0; i < 5; i++)
                {
                    // The old bar's rect in its row vs. the new bar's drawn rect in its row.
                    var drawn = bars[i].BarRect.Translate(new Vector(bars[i].Bounds.X, bars[i].Bounds.Y));
                    Assert.Equal(oldBars[i].Bounds, drawn);
                }
            }
        }
        finally
        {
            oldWin.Close();
            newWin.Close();
        }
    }

    [AvaloniaFact]
    public void Tick_OnlyRepaints_NoLayoutPass()
    {
        var eq = NewEq(Brushes.White);
        var win = Host(eq);
        var layoutPasses = 0;
        win.LayoutUpdated += (_, _) => layoutPasses++;
        try
        {
            var bars = BarsOf(eq);
            var panel = bars[0].GetVisualParent<StackPanel>()!;
            eq.IsPlaying = true;
            var timer = Field<DispatcherTimer>(eq, "_animTimer");
            Assert.True(timer.IsEnabled);
            Assert.Equal(EqVisualizer.FrameInterval, timer.Interval);
            Assert.True(EqVisualizer.FrameInterval >= TimeSpan.FromMilliseconds(33)); // ~30 fps cap
            win.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            layoutPasses = 0;

            var before = bars.Select(b => b.BarHeight).ToArray();
            for (var i = 0; i < 6; i++)
            {
                Thread.Sleep(40);
                Call(eq, "OnAnimTick", null, EventArgs.Empty);
                foreach (var c in bars.Cast<Layoutable>().Append(panel).Append(eq))
                    Assert.True(c.IsMeasureValid && c.IsArrangeValid, "a tick invalidated layout");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(0, layoutPasses);
            // The ticks did move the bars.
            Assert.NotEqual(before, bars.Select(b => b.BarHeight).ToArray());

            // Sanity: the check above does catch a layout write (what the old bars did).
            bars[0].Height = 5;
            Assert.False(bars[0].IsMeasureValid);
            Dispatcher.UIThread.RunJobs();
            Assert.True(layoutPasses > 0);
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// Pixel check of the same comparison: needs real Skia rendering
    /// (NOCTIS_TEST_SKIA=1). Old Rectangles and new EqBars, side by side at each height,
    /// must produce identical pixels.
    /// </summary>
    [AvaloniaFact]
    public void EqBar_RendersTheSamePixelsAsTheOldRectangles()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");

        var (oldPanel, oldBars) = OldBars(Brushes.White);
        var eq = NewEq(Brushes.White);
        var oldWin = Host(new Border { Width = 34, Height = 22, Child = oldPanel });
        var newWin = Host(eq);
        try
        {
            var bars = BarsOf(eq);
            foreach (var h in Heights())
            {
                foreach (var r in oldBars) r.Height = h;
                foreach (var b in bars) b.BarHeight = h;
                oldWin.UpdateLayout();
                newWin.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var expected = Pixels(oldWin.CaptureRenderedFrame()!);
                Assert.Contains(expected, px => px != 0 && px != 255); // the bars are drawn (anti-aliased edges)
                Assert.Equal(expected, Pixels(newWin.CaptureRenderedFrame()!));
            }
        }
        finally
        {
            oldWin.Close();
            newWin.Close();
        }
    }

    private static byte[] Pixels(WriteableBitmap frame)
    {
        using var fb = frame.Lock();
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        return bytes;
    }
}
