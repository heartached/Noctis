using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Noctis.Helpers;

namespace Noctis.Mobile.Views;

/// <summary>
/// The lyric line's size: lines rest at <see cref="Resting"/> and the sung one grows to full size
/// on the line curve over the glide's second (<see cref="LineMotion"/>), about its
/// RenderTransformOrigin (the voice's side). Played by the compositor on the line's own
/// composition Scale: as a TransformOperationsTransition on RenderTransform it built a new
/// transform, its operations and a property change for the two changing lines on every frame of
/// every line change (S23, 11-23: part of the steady allocation that set off a GC every ~30 s).
/// A line shown afresh (a row handed the next line) takes its size at once, as the transition
/// (which does not run off the visual tree) did.
/// </summary>
public static class LineScale
{
    /// <summary>The line is sung (null until bound: false is then a change too).</summary>
    public static readonly AttachedProperty<bool?> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool?>("IsActive", typeof(LineScale));

    public static bool? GetIsActive(Control element) => element.GetValue(IsActiveProperty);
    public static void SetIsActive(Control element, bool? value) => element.SetValue(IsActiveProperty, value);

    /// <summary>A line's size while it is not sung.</summary>
    internal const double Resting = 0.96;

    private const string Property = "Scale";
    private static readonly LineMotionEase Ease = new();

    static LineScale()
    {
        IsActiveProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.OldValue is null)
            {
                control.AttachedToVisualTree += (_, _) => Apply(control, animate: false);
                control.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Visual.BoundsProperty || e.Property == Visual.RenderTransformOriginProperty) Centre(control);
                };
            }
            // A line's first value is its size, not a change of it.
            Apply(control, animate: args.OldValue is not null);
        });
    }

    /// <summary>The scale the line shows once any animation has run (tests read it).</summary>
    internal static double Target(Control control) => GetIsActive(control) == true ? 1 : Resting;

    private static void Apply(Control control, bool animate)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual) return;
        Centre(control);
        var s = Target(control);
        var target = new Vector3D(s, s, 1);
        // The UI side's value: where a later instant write compares from.
        visual.Scale = target;
        if (!animate) return;
        // From wherever the line stands now (no key frame at 0: the render thread starts from
        // its current scale), as the transition did.
        var grow = visual.Compositor.CreateVector3DKeyFrameAnimation();
        grow.InsertKeyFrame(1f, target, Ease);
        grow.Duration = TimeSpan.FromMilliseconds(LineMotion.DurationMs);
        visual.StartAnimation(Property, grow);
    }

    private static void Centre(Control control)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual) return;
        var at = control.RenderTransformOrigin.ToPixels(control.Bounds.Size);
        visual.CenterPoint = new Vector3D(at.X, at.Y, 0);
    }
}
