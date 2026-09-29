using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Noctis.Controls;

/// <summary>
/// Hosts one collapsible section body (the Home sections' content under their
/// disclosure header) and eases it open and shut instead of snapping.
/// </summary>
/// <remarks>
/// The animation is a CLIP, not a re-layout. <see cref="MeasureOverride"/> always
/// measures the child at its natural height and only shrinks the height this control
/// reports; <see cref="ArrangeOverride"/> then arranges the child full-size behind a
/// rectangular clip. Animating the child's own height instead would re-measure and
/// re-wrap the section's ItemsControl on every frame, which on a large library is the
/// difference between a smooth fold and a stuttering one.
///
/// Collapsed sections still cost nothing. At rest with <see cref="IsOpen"/> false this
/// control turns its own <see cref="Visual.IsVisible"/> off, so the child is never
/// measured — its containers stay unrealized and its CachedImages never load, exactly
/// as the plain IsVisible binding this replaced behaved. It also keeps a parent
/// StackPanel's Spacing from leaving a phantom gap under a folded header.
/// </remarks>
public class CollapsibleContent : Decorator
{
    /// <summary>Duration of the fold, both directions.</summary>
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Easing FoldEase = new CubicEaseInOut();

    /// <summary>
    /// The <see cref="CollapsibleMotion.Glide"/> reveal, per direction. Opening is the longer
    /// of the two and decelerates the whole way (an expo-style spline with no overshoot), so
    /// the panel arrives fast and then settles; shutting is shorter and eases in and out, so
    /// it folds away calmly instead of mirroring the open's long tail in reverse.
    /// </summary>
    // Our own bezier, not Avalonia.SplineEasing: see CubicBezierEase for why.
    internal static readonly TimeSpan GlideOpenDuration = TimeSpan.FromMilliseconds(360);
    internal static readonly TimeSpan GlideCloseDuration = TimeSpan.FromMilliseconds(240);
    internal static readonly Easing GlideOpenEase = new Noctis.Helpers.CubicBezierEase(0.22, 1.0, 0.36, 1.0);
    internal static readonly Easing GlideCloseEase = new Noctis.Helpers.CubicBezierEase(0.45, 0.0, 0.55, 1.0);

    /// <summary>
    /// Fraction of the glide over which the body fades up. Tying opacity to the whole
    /// travel is what leaves a half-open panel sitting at half opacity, which reads as
    /// washed-out text rather than as motion; landing it early keeps the body legible for
    /// most of the reveal and lets the height do the animating.
    /// </summary>
    internal const double GlideFadeWindow = 0.55;

    /// <summary>Below this the section counts as shut (float dust off the transition).</summary>
    private const double ShutEpsilon = 0.0001;

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<CollapsibleContent, bool>(nameof(IsOpen), defaultValue: true);

    /// <summary>
    /// 0 shut, 1 open. The animated value — bound to nothing, driven by <see cref="IsOpen"/>
    /// through the transition below. Public so a style could retime it.
    /// </summary>
    public static readonly StyledProperty<double> RevealProperty =
        AvaloniaProperty.Register<CollapsibleContent, double>(nameof(Reveal), defaultValue: 1.0);

    /// <summary>
    /// Pixels the body glides down from as it opens (and back up as it shuts). 0, the
    /// default, keeps the plain fold the Home sections use; the Settings sub-menus set a
    /// few pixels so a block reads as sliding out from under its control.
    /// </summary>
    public static readonly StyledProperty<double> LiftProperty =
        AvaloniaProperty.Register<CollapsibleContent, double>(nameof(Lift));

    /// <summary>
    /// Which reveal this instance plays. Defaults to <see cref="CollapsibleMotion.Fold"/>,
    /// so every existing collapsible keeps the exact fold it had.
    /// </summary>
    public static readonly StyledProperty<CollapsibleMotion> MotionProperty =
        AvaloniaProperty.Register<CollapsibleContent, CollapsibleMotion>(nameof(Motion));

    public CollapsibleMotion Motion
    {
        get => GetValue(MotionProperty);
        set => SetValue(MotionProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double Lift
    {
        get => GetValue(LiftProperty);
        set => SetValue(LiftProperty, value);
    }

    public double Reveal
    {
        get => GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    /// <summary>
    /// The one transition driving <see cref="Reveal"/>. Fold keeps a single symmetric
    /// cubic both ways; Glide retimes it per direction (see <see cref="ApplyMotion"/>).
    /// Retiming happens BEFORE the target value is written, so each toggle starts a fresh
    /// transition instance on the new curve — a running fold is never re-curved mid-frame.
    /// </summary>
    private readonly DoubleTransition _revealTransition = new()
    {
        Property = RevealProperty,
        Duration = RevealDuration,
        Easing = FoldEase
    };

    /// <summary>
    /// Retimes the reveal for the current <see cref="Motion"/> and, for Glide, the direction
    /// about to play. Called on construction, whenever Motion changes, and on each toggle.
    /// </summary>
    private void ApplyMotion(bool opening)
    {
        if (Motion == CollapsibleMotion.Glide)
        {
            _revealTransition.Duration = opening ? GlideOpenDuration : GlideCloseDuration;
            _revealTransition.Easing = opening ? GlideOpenEase : GlideCloseEase;
        }
        else
        {
            _revealTransition.Duration = RevealDuration;
            _revealTransition.Easing = FoldEase;
        }
    }

    /// <summary>Natural (unfolded) size of the child, from the last measure.</summary>
    private Size _childNatural;

    /// <summary>The glide (see <see cref="Lift"/>); Y is written from the reveal.</summary>
    private readonly Avalonia.Media.TranslateTransform _lift = new();

    /// <summary>
    /// Transitions stay off until the first layout pass has run. A section restored
    /// folded from settings must come up folded, not play its collapse on startup.
    /// </summary>
    private bool _armed;

    public CollapsibleContent()
    {
        // The fold is this clip. Decorator is not a Border, so this is the plain
        // rectangular clip it looks like (Border would round it by CornerRadius).
        ClipToBounds = true;
        RenderTransform = _lift;
        ApplyMotion(IsOpen);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Posted at Loaded priority, not called inline: arming here directly would
        // catch the initial binding pass and animate the restored state into view.
        // A section that comes up folded never arranges, so ArrangeOverride can't be
        // the arming point either.
        Dispatcher.UIThread.Post(() =>
        {
            if (_armed) return;
            _armed = true;
            Transitions ??= new Transitions { _revealTransition };
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Re-arm on the next attach. Avalonia disables transitions while detached, so a
        // section toggled off-screen (Home swapped out) would otherwise land mid-fold.
        _armed = false;
        Transitions = null;
        SetReveal(IsOpen ? 1.0 : 0.0);
    }

    /// <summary>
    /// Writes the fold target as a real local value.
    /// </summary>
    /// <remarks>
    /// SetValue, never SetCurrentValue. SetCurrentValue only retargets the running
    /// transition and leaves the BASE value unset, so the moment the animation layer is
    /// torn down — which is exactly what turning IsVisible off at the end of a close
    /// does — the property falls back to its registered default of 1.0 and the section
    /// animates straight back open. That was a fold that undid itself.
    /// </remarks>
    private void SetReveal(double value) => SetValue(RevealProperty, value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsOpenProperty)
        {
            var open = change.GetNewValue<bool>();

            // Has to become visible BEFORE the reveal changes, or there is no measured
            // child height for the fold to grow into.
            if (open) IsVisible = true;

            ApplyMotion(open);

            SetReveal(open ? 1.0 : 0.0);
        }
        else if (change.Property == MotionProperty)
        {
            ApplyMotion(IsOpen);
        }
        else if (change.Property == RevealProperty)
        {
            var reveal = Math.Clamp(change.GetNewValue<double>(), 0, 1);

            if (Motion == CollapsibleMotion.Glide)
            {
                // Two shaped curves off the one animated value, so the reveal still costs a
                // single transition. Opacity finishes early (see GlideFadeWindow) and the
                // slide settles ahead of the height, so the body reads as arriving and then
                // the panel growing to fit it — not as one block scaling into place.
                Opacity = Noctis.Helpers.Easing.SmootherStep(reveal / GlideFadeWindow);
                var remaining = 1 - reveal;
                _lift.Y = -Lift * remaining * remaining;
            }
            else
            {
                Opacity = reveal;
                _lift.Y = -Lift * (1 - reveal);
            }

            InvalidateMeasure();

            if (reveal <= ShutEpsilon && !IsOpen) IsVisible = false;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var child = Child;
        if (child is null)
        {
            _childNatural = default;
            return default;
        }

        // Natural height, every frame — the child lays out once and Avalonia serves the
        // cached measure for the rest of the fold. Passing the folded height here is
        // what would re-wrap the section's rows on each frame.
        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        _childNatural = child.DesiredSize;

        return new Size(_childNatural.Width, _childNatural.Height * Math.Clamp(Reveal, 0, 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Top-anchored at FULL height: the section slides up behind the clip rather than
        // squashing, so artwork keeps its aspect ratio all the way down.
        Child?.Arrange(new Rect(0, 0, finalSize.Width, _childNatural.Height));
        return finalSize;
    }
}

/// <summary>Which reveal a <see cref="CollapsibleContent"/> plays.</summary>
public enum CollapsibleMotion
{
    /// <summary>
    /// The original fold: 220ms cubic in-out with opacity and slide tracking the height
    /// linearly. Every collapsible in the app uses this unless it opts out.
    /// </summary>
    Fold,

    /// <summary>
    /// Softer reveal: a 360ms decelerating open and a 240ms ease-in-out close, opacity
    /// landing at just over half the travel, and the slide settling ahead of the height.
    /// </summary>
    Glide
}
