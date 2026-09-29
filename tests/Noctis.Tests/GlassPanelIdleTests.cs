using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Choppy with Liquid Glass on" (09-24): every frosting panel invalidated itself on every
/// frame, so the app rendered continuously with nothing changing, and the compositor's
/// single dirty rect joined the sidebar rail and the island into one box over most of the
/// window (1142×1012 of 1728×1030 in the headless probe, ~30 ms a frame, forever). The
/// panel now repaints only when it, or what lies under or beside it, changes.
/// </summary>
public class GlassPanelIdleTests
{
    public sealed class CountingGlassPanel : GlassPanel
    {
        public int Renders;
        public override void Render(DrawingContext context)
        {
            Renders++;
            base.Render(context);
        }
    }

    public static void Ticks(int n)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void FrostingPanel_RepaintsNothingByItself()
    {
        var panel = new CountingGlassPanel
        {
            UseAppGlass = false, IsGlassActive = true, BlurRadius = 14,
            Width = 200, Height = 60, Background = Brushes.Gray,
        };
        var window = new Window { Width = 300, Height = 120, Content = panel };
        window.Show();
        try
        {
            Ticks(5);
            Assert.True(panel.Renders > 0, "the panel never rendered");
            var renders = panel.Renders;
            Ticks(30);
            Assert.Equal(renders, panel.Renders);

            // A real change still repaints it.
            panel.Fade = 0.5;
            Ticks(2);
            Assert.True(panel.Renders > renders);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReachChild_SpansTheRingTheBlurReads_AndIsInvisibleToInput()
    {
        var panel = new GlassPanel
        {
            UseAppGlass = false, IsGlassActive = true, BlurRadius = 14,
            Width = 200, Height = 60,
        };
        var window = new Window { Width = 400, Height = 200, Content = panel };
        window.Show();
        try
        {
            Ticks(2);
            var reach = panel.GetVisualChildren().OfType<GlassReach>().Single();
            // 3σ = 42, plus one: past every edge.
            Assert.Equal(new Rect(-43, -43, 286, 146), reach.Bounds);
            Assert.False(reach.IsHitTestVisible);
            Assert.False(reach.Focusable);

            panel.BlurRadius = 20;
            Ticks(2);
            Assert.Equal(new Rect(-61, -61, 322, 182), reach.Bounds);

            // Hit tests on the panel still land on the panel, not the reach child.
            var hit = window.InputHitTest(new Point(window.Bounds.Width / 2, window.Bounds.Height / 2));
            Assert.IsNotType<GlassReach>(hit);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FullRepaintRequests_AreCoalescedUntilTheRepaintRuns()
    {
        var panel = new CountingGlassPanel { UseAppGlass = false, IsGlassActive = true, BlurRadius = 14, Width = 100, Height = 40 };
        var window = new Window { Width = 200, Height = 100, Content = panel };
        window.Show();
        try
        {
            Ticks(3);
            var renders = panel.Renders;
            // What the render thread does when a frame repainted pixels beside or under the panel.
            panel.RequestFullRepaint();
            panel.RequestFullRepaint();
            panel.RequestFullRepaint();
            Ticks(3);
            Assert.Equal(renders + 1, panel.Renders);

            panel.OnReachRepainted();
            Ticks(3);
            Assert.Equal(renders + 2, panel.Renders);
        }
        finally { window.Close(); }
    }
}
