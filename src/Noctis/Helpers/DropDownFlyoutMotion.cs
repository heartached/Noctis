using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// Gives a <see cref="Flyout"/> the ComboBox drop-down's open and close motion
/// (<see cref="ComboBoxDropDownAnimator"/>: fade + a short glide out of its button), so a
/// flyout that sits next to combo pills reads as the same kind of menu (Lyrics Studio
/// Options, user ask 09-19). Enable per flyout in XAML:
/// <c>helpers:DropDownFlyoutMotion.Enable="True"</c>.
/// </summary>
/// <remarks>
/// The presenter is what animates (found from the flyout's content once it is open). A
/// close arrives cancellable on <see cref="FlyoutBase.Closing"/> for every path (Hide,
/// Escape, light dismiss): the first pass is cancelled and plays the mirror of the open;
/// the real Hide fires when the fade lands on 0. A flyout placed above its button grows
/// out of its bottom edge, one placed below out of its top edge.
/// </remarks>
public static class DropDownFlyoutMotion
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<FlyoutBase, bool>("Enable", typeof(DropDownFlyoutMotion));

    public static void SetEnable(FlyoutBase flyout, bool value) => flyout.SetValue(EnableProperty, value);
    public static bool GetEnable(FlyoutBase flyout) => flyout.GetValue(EnableProperty);

    private static readonly TransformOperations StartBelow = TransformOperations.Parse("scale(0.94) translateY(-4px)");
    private static readonly TransformOperations EndBelow = TransformOperations.Parse("scale(0.97) translateY(-2px)");
    private static readonly TransformOperations StartAbove = TransformOperations.Parse("scale(0.94) translateY(4px)");
    private static readonly TransformOperations EndAbove = TransformOperations.Parse("scale(0.97) translateY(2px)");
    private static readonly TransformOperations Rest = TransformOperations.Parse("scale(1) translateY(0px)");
    private static readonly ConditionalWeakTable<Flyout, State> States = new();

    private sealed class State
    {
        /// <summary>A close was cancelled so the fade-out can play; the next Closing is the real one.</summary>
        public bool Held;
        /// <summary>The presenter being animated, while the flyout is open.</summary>
        public Control? Body;
    }

    static DropDownFlyoutMotion()
    {
        EnableProperty.Changed.AddClassHandler<Flyout>(static (flyout, e) =>
        {
            flyout.Opened -= OnOpened;
            flyout.Closing -= OnClosing;
            if (e.NewValue is not true) return;
            flyout.Opened += OnOpened;
            flyout.Closing += OnClosing;
        });
    }

    private static bool OpensAbove(Flyout flyout) => flyout.Placement is
        PlacementMode.Top or PlacementMode.TopEdgeAlignedLeft or PlacementMode.TopEdgeAlignedRight;

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Flyout flyout) return;
        var body = (flyout.Content as Control)?.FindAncestorOfType<FlyoutPresenter>();
        if (body is null) return;

        var state = States.GetOrCreateValue(flyout);
        if (!ReferenceEquals(state.Body, body))
        {
            state.Body = body;
            body.PropertyChanged += (s, args) =>
            {
                if (!state.Held || s is not Control c) return;
                var fade = MenuOpenAnimation.FadePropertyOf(c);
                if (args.Property != fade || c.GetValue(fade) > 0.001) return;
                flyout.Hide();
            };
        }

        // A Liquid Glass presenter (GlassMenus) fades MenuOpenAnimation.Fade, which its frosted
        // card follows: the blur ignores Opacity, so an Opacity fade showed the frost at full
        // strength while the rest faded.
        var fadeProperty = MenuOpenAnimation.FadePropertyOf(body);
        var above = OpensAbove(flyout);
        body.Transitions = null;
        body.RenderTransformOrigin = new RelativePoint(0.5, above ? 1 : 0, RelativeUnit.Relative);
        if (fadeProperty != Visual.OpacityProperty) body.Opacity = 1;
        body.SetValue(fadeProperty, 0.0);
        body.RenderTransform = above ? StartAbove : StartBelow;
        body.Transitions = ComboBoxDropDownAnimator.BuildOpen(fadeProperty);
        Dispatcher.UIThread.Post(() =>
        {
            body.SetValue(fadeProperty, 1.0);
            body.RenderTransform = Rest;
        }, DispatcherPriority.Render);
    }

    private static void OnClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not Flyout flyout) return;
        var state = States.GetOrCreateValue(flyout);
        if (state.Held)
        {
            state.Held = false;
            return;
        }
        if (state.Body is not { } body) return;

        e.Cancel = true;
        state.Held = true;
        var end = OpensAbove(flyout) ? EndAbove : EndBelow;
        var fadeProperty = MenuOpenAnimation.FadePropertyOf(body);
        body.Transitions = ComboBoxDropDownAnimator.BuildClose(fadeProperty);
        Dispatcher.UIThread.Post(() =>
        {
            body.SetValue(fadeProperty, 0.0);
            body.RenderTransform = end;
        }, DispatcherPriority.Render);
    }
}
