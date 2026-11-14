using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation.Peers;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Noctis.Mobile.Views;

/// <summary>
/// Apple Music's Now Playing bar (the timeline and the volume): a rounded capsule with no thumb,
/// its played part a light translucent white over a dimmer track. A press anywhere on it puts
/// the value under the finger and a drag follows the finger exactly (absolute, not relative);
/// while held the capsule grows taller and brighter (<see cref="Expansion"/>), shrinking back on
/// release. Drawn in Render, so a drag costs one repaint of two rounded rectangles; the grow is a
/// transition on a property nothing binds, so it never restarts under the per-frame Value.
/// <see cref="ScrubStarted"/> / <see cref="ScrubCompleted"/> / <see cref="ScrubCanceled"/> bracket
/// a drag for a caller that commits on release (the seek bar).
/// </summary>
public sealed class CapsuleSlider : RangeBase
{
    public static readonly StyledProperty<double> TrackHeightProperty =
        AvaloniaProperty.Register<CapsuleSlider, double>(nameof(TrackHeight), 7);

    public static readonly StyledProperty<double> PressedTrackHeightProperty =
        AvaloniaProperty.Register<CapsuleSlider, double>(nameof(PressedTrackHeight), 12);

    /// <summary>0 at rest, 1 while held; animated between the two (see the constructor).</summary>
    public static readonly StyledProperty<double> ExpansionProperty =
        AvaloniaProperty.Register<CapsuleSlider, double>(nameof(Expansion));

    public static readonly DirectProperty<CapsuleSlider, bool> IsScrubbingProperty =
        AvaloniaProperty.RegisterDirect<CapsuleSlider, bool>(nameof(IsScrubbing), s => s.IsScrubbing);

    // White over the always-dark Now Playing page: the track dim, the played part light, both
    // a step brighter while held (Apple's).
    internal const double TrackAlpha = 0.2, PressedTrackAlpha = 0.28;
    internal const double FillAlpha = 0.62, PressedFillAlpha = 0.95;

    static CapsuleSlider()
    {
        AffectsRender<CapsuleSlider>(ValueProperty, MinimumProperty, MaximumProperty, ExpansionProperty,
            TrackHeightProperty, PressedTrackHeightProperty);
        FocusableProperty.OverrideDefaultValue<CapsuleSlider>(false);
    }

    public CapsuleSlider()
    {
        Transitions = new Transitions
        {
            new DoubleTransition { Property = ExpansionProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() },
        };
    }

    public double TrackHeight { get => GetValue(TrackHeightProperty); set => SetValue(TrackHeightProperty, value); }
    public double PressedTrackHeight { get => GetValue(PressedTrackHeightProperty); set => SetValue(PressedTrackHeightProperty, value); }
    public double Expansion { get => GetValue(ExpansionProperty); set => SetValue(ExpansionProperty, value); }

    private bool _isScrubbing;
    public bool IsScrubbing { get => _isScrubbing; private set => SetAndRaise(IsScrubbingProperty, ref _isScrubbing, value); }

    /// <summary>A finger went down on the bar (before the value moves under it).</summary>
    public event EventHandler? ScrubStarted;

    /// <summary>The finger lifted: Value is where it let go.</summary>
    public event EventHandler? ScrubCompleted;

    /// <summary>The gesture was taken away (capture lost) before a release.</summary>
    public event EventHandler? ScrubCanceled;

    /// <summary>Value for a point <paramref name="x"/> along the bar: the whole width is the range.</summary>
    internal double ValueAt(double x)
    {
        var width = Bounds.Width;
        var fraction = width > 0 ? Math.Clamp(x / width, 0, 1) : 0;
        return Minimum + fraction * (Maximum - Minimum);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        IsScrubbing = true;
        Expansion = 1;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        SetCurrentValue(ValueProperty, ValueAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsScrubbing) return;
        SetCurrentValue(ValueProperty, ValueAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsScrubbing) return;
        SetCurrentValue(ValueProperty, ValueAt(e.GetPosition(this).X));
        // Cleared before the capture goes, so the capture-lost that follows is not a cancel.
        EndScrub();
        e.Pointer.Capture(null);
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!IsScrubbing) return;
        EndScrub();
        ScrubCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void EndScrub()
    {
        IsScrubbing = false;
        Expansion = 0;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 0) return;
        // Hit testing follows what is drawn: with only the capsule, a press had to land on its
        // 7 dp in the middle of the 30 dp bar (beta, 11-09). A clear fill over the whole box
        // takes the finger anywhere on it and paints nothing.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var grow = Math.Clamp(Expansion, 0, 1);
        var height = TrackHeight + (PressedTrackHeight - TrackHeight) * grow;
        var capsule = new RoundedRect(new Rect(0, (Bounds.Height - height) / 2, width, height), height / 2);
        context.DrawRectangle(White(TrackAlpha + (PressedTrackAlpha - TrackAlpha) * grow), null, capsule);

        var range = Maximum - Minimum;
        var fraction = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        if (fraction <= 0) return;
        using (context.PushClip(capsule))
            context.DrawRectangle(White(FillAlpha + (PressedFillAlpha - FillAlpha) * grow), null,
                new Rect(0, capsule.Rect.Y, width * fraction, height));
    }

    private static IImmutableSolidColorBrush White(double alpha) => new ImmutableSolidColorBrush(Colors.White, alpha);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>Reads as a slider (its value and range) to TalkBack.</summary>
    private sealed class Peer(CapsuleSlider owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }
}
