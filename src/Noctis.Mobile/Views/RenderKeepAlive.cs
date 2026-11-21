using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace Noctis.Mobile.Views;

/// <summary>
/// A 1×1 transparent visual whose compositor-side opacity loops while <see cref="Run"/> is on, so
/// Avalonia's render loop never goes to sleep under a page that animates every frame.
/// S23 trace (11-15): on the lyrics page the render loop stopped the screen's frame callbacks
/// (DefaultRenderLoop sets IRenderTimer.Tick to null when no task wants a next tick) with a batch
/// still in flight, and nothing woke it for 75-85 ms: frames stopped app-wide, no thread ran, then
/// it resumed. A running composition animation keeps the compositor asking for ticks, so the loop
/// stays awake while the page plays. Invisible: alpha 0.001-0.002 on a transparent 1×1 box.
/// </summary>
public sealed class RenderKeepAlive : Control
{
    private const string Property = "Opacity";
    private bool _running;

    public RenderKeepAlive()
    {
        Width = 1;
        Height = 1;
        IsHitTestVisible = false;
        Focusable = false;
        Opacity = 0.001;
    }

    /// <summary>Whether the loop runs (tests read it).</summary>
    internal bool IsRunning => _running;

    public override void Render(DrawingContext context) =>
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    /// <summary>On: start the compositor loop (if attached). Off: stop it.</summary>
    public void Run(bool on)
    {
        if (on == _running) return;
        if (ElementComposition.GetElementVisual(this) is not { } visual) { _running = false; return; }
        if (on)
        {
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, 0.001f);
            animation.InsertKeyFrame(1f, 0.002f);
            animation.Duration = TimeSpan.FromSeconds(1);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation(Property, animation);
        }
        else visual.StopAnimation(Property);
        _running = on;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Run(false);
        base.OnDetachedFromVisualTree(e);
    }
}
