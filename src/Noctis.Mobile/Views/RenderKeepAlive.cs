using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace Noctis.Mobile.Views;

/// <summary>
/// A 1×1 transparent visual whose compositor-side opacity moves while <see cref="Run"/> is on, so
/// every vsync carries a frame to the screen while the page under it animates.
/// S23 trace (11-15): on the lyrics page frames stopped app-wide for 75-85 ms, no thread ran,
/// then they resumed. S23 logcat (11-22) shows what that is: SurfaceFlinger got no new buffer
/// from the app for its idle timeout ("IdleFps - choose 24.00 Hz"), dropped the panel to 24 Hz,
/// and the app's next frame waited out the switch back to 120 Hz (each of 25 such stalls in
/// 40 s came right after a 24 Hz drop, and no drop came without one). Avalonia draws nothing on
/// a vsync where nothing changed (ServerCompositionTarget.Render), so a page that moves only
/// now and then lets the panel idle in between; a moving compositor value is a change on every
/// vsync.
/// Invisible: alpha 0.001-0.002 on a transparent 1×1 box (the compositor skips drawing anything
/// under 0.003, but still counts the box as changed).
/// <para>Each <see cref="Run"/>(true) is a pulse: the animation is (re)started at most every
/// <see cref="RearmMs"/> and runs <see cref="Tail"/>, then ends on its own. It is never stopped
/// from here: Avalonia 12's StopAnimation edits the render thread's animation list straight
/// from the UI thread (CompositionObject.StopAnimation) and misses an animation still in flight
/// to it in a batch, and a forever animation it misses redraws the screen on every vsync for as
/// long as the app lives, even under a hidden page (a hidden page's children still mark their
/// spot changed: MobileLyricsPageTests). S23 (11-22): after Lyrics went back to Now Playing the
/// render thread stayed at 55%, with playback stopped.</para>
/// </summary>
public sealed class RenderKeepAlive : Control
{
    private const string Property = "Opacity";

    /// <summary>How long one pulse keeps frames coming after the last <see cref="Run"/>(true).</summary>
    internal static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(500);

    /// <summary>A running pulse is restarted only once it has run this long (so a page asking on
    /// every frame starts one animation a quarter second, not 120 a second).</summary>
    internal const double RearmMs = 250;

    private bool _running;
    private double _pulsedAtMs = double.NegativeInfinity;
    private ScalarKeyFrameAnimation? _pulse;
    private Compositor? _pulseCompositor;

    public RenderKeepAlive()
    {
        Width = 1;
        Height = 1;
        IsHitTestVisible = false;
        Focusable = false;
        Opacity = 0.001;
    }

    /// <summary>Whether the page asks for frames (tests read it). Once it stops asking, the last
    /// pulse still runs out within <see cref="Tail"/>.</summary>
    internal bool IsRunning => _running;

    /// <summary>Pulses started so far (tests and the stall monitor's summary read it).</summary>
    internal int Pulses { get; private set; }

    public override void Render(DrawingContext context) =>
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    /// <summary>On: keep frames coming for <see cref="Tail"/> from now (if attached). Off: stop
    /// asking; the last pulse ends by itself.</summary>
    public void Run(bool on)
    {
        if (!on || ElementComposition.GetElementVisual(this) is not { } visual)
        {
            if (_running) Services.FrameStallMonitor.SetState("keepalive", false);
            _running = false;
            return;
        }
        if (!_running) Services.FrameStallMonitor.SetState("keepalive", true);
        _running = true;
        var now = Noctis.Mobile.ViewModels.LyricsPageViewModel.NowMs();
        if (now - _pulsedAtMs < RearmMs) return;
        _pulsedAtMs = now;
        if (_pulse == null || !ReferenceEquals(_pulseCompositor, visual.Compositor))
        {
            _pulseCompositor = visual.Compositor;
            _pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
            _pulse.InsertKeyFrame(0f, 0.001f);
            _pulse.InsertKeyFrame(1f, 0.002f);
            _pulse.Duration = Tail;
            _pulse.IterationBehavior = AnimationIterationBehavior.Count;
            _pulse.IterationCount = 1;
        }
        // Replaces the previous pulse on the render thread (through the next batch, as every
        // compositor change should go), restarting the tail.
        visual.StartAnimation(Property, _pulse);
        Pulses++;
        Services.FrameStallMonitor.Count("keepalive pulses");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Run(false);
        _pulsedAtMs = double.NegativeInfinity;   // a new visual on re-attach needs a new pulse at once
        base.OnDetachedFromVisualTree(e);
    }
}
