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

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double Reveal
    {
        get => GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    /// <summary>
    /// One easing, both directions. CubicEaseInOut is symmetric about its midpoint, so
    /// shutting is the exact mirror of opening — a fold that eased out one way and in the
    /// other is the same curve reversed in time, but reads as two different animations.
    /// </summary>
    private readonly DoubleTransition _revealTransition = new()
    {
        Property = RevealProperty,
        Duration = RevealDuration,
        Easing = new CubicEaseInOut()
    };

    /// <summary>Natural (unfolded) size of the child, from the last measure.</summary>
    private Size _childNatural;

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

            SetReveal(open ? 1.0 : 0.0);
        }
        else if (change.Property == RevealProperty)
        {
            var reveal = Math.Clamp(change.GetNewValue<double>(), 0, 1);
            Opacity = reveal;
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
