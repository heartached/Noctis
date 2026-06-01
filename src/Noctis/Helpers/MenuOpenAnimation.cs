using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// Global rise-up + fade open/close animation for popup menus (right-click
/// <see cref="ContextMenu"/> and 3-dots <c>MenuFlyout</c> presenters).
///
/// Enable it once per control type via a style setter:
/// <c>&lt;Setter Property="(helpers:MenuOpenAnimation.Enable)" Value="True" /&gt;</c>
///
/// Enable close animation on individual <see cref="MenuFlyout"/> instances:
/// <c>helpers:MenuOpenAnimation.EnableFlyoutClose="True"</c>
///
/// The animation runs via per-instance transitions (the same mechanism proven on the
/// Favorites menu) rather than <c>Style.Animations</c>: a style animation re-runs on
/// every visual-tree attach, and a popup attaches its content more than once while
/// opening, which made the animation play twice (the "two animations at once" bug).
///
/// It is triggered both when the control is already attached at the moment the style
/// setter is applied (freshly-created flyout presenters) and on later attaches (reused
/// context menus reopening). A short time-based guard collapses the popup's rapid
/// double-attach into a single run while still letting a genuine reopen animate again.
/// </summary>
public static class MenuOpenAnimation
{
    private const double OpenDurationMs = 150;
    private const double CloseDurationMs = 120;
    private const double OpenOffsetY = 10;
    private const double CloseOffsetY = 6;
    // Rapid re-attaches during a single popup open happen within a frame or two;
    // a real reopen is always far slower than this human-interaction threshold.
    private const long ReopenGuardMs = 200;

    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enable", typeof(MenuOpenAnimation));

    public static void SetEnable(Control control, bool value) => control.SetValue(EnableProperty, value);
    public static bool GetEnable(Control control) => control.GetValue(EnableProperty);

    public static readonly AttachedProperty<bool> EnableFlyoutCloseProperty =
        AvaloniaProperty.RegisterAttached<MenuFlyout, bool>("EnableFlyoutClose", typeof(MenuOpenAnimation));

    public static void SetEnableFlyoutClose(MenuFlyout flyout, bool value) =>
        flyout.SetValue(EnableFlyoutCloseProperty, value);

    public static bool GetEnableFlyoutClose(MenuFlyout flyout) =>
        flyout.GetValue(EnableFlyoutCloseProperty);

    private static readonly AttachedProperty<long> LastRunProperty =
        AvaloniaProperty.RegisterAttached<Control, long>("LastRun", typeof(MenuOpenAnimation));

    private static readonly AttachedProperty<bool> CloseAnimationRunningProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("CloseAnimationRunning", typeof(MenuOpenAnimation));

    private static readonly AttachedProperty<bool> CloseAfterAnimationProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("CloseAfterAnimation", typeof(MenuOpenAnimation));

    private static WeakReference<MenuFlyoutPresenter>? _lastMenuFlyoutPresenter;

    static MenuOpenAnimation()
    {
        EnableProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.AttachedToVisualTree -= OnAttached;
            if (control is ContextMenu contextMenu)
            {
                contextMenu.Closing -= OnContextMenuClosing;
                contextMenu.Closed -= OnContextMenuClosed;
            }

            if (!args.GetNewValue<bool>())
                return;

            control.AttachedToVisualTree += OnAttached;
            // NOTE: ContextMenu intentionally does NOT get a close animation. Animating the
            // close requires cancelling the real close (e.Cancel = true) and keeping the
            // popup open-but-invisible (Opacity = 0) for the animation's duration, then
            // closing it from a timer. When a menu item opens a modal dialog, navigates, or
            // refreshes the owning list, that deferred close is disrupted and the popup is
            // stranded: open and hit-testable but invisible, so it swallows scroll/right-click
            // and fires stray clicks on the now-invisible items. Right-click menus therefore
            // close instantly. (MenuFlyout below keeps its close animation — it is anchored to
            // a persistent button, so a stranded flyout can always be re-toggled.)

            // The setter is often applied after the popup has already attached, so the
            // attach event would be missed. Run now if we're already in the visual tree.
            if (control.GetVisualRoot() is not null)
                TryRun(control);
        });

        EnableFlyoutCloseProperty.Changed.AddClassHandler<MenuFlyout>((flyout, args) =>
        {
            flyout.Closing -= OnMenuFlyoutClosing;
            flyout.Closed -= OnMenuFlyoutClosed;

            if (!args.GetNewValue<bool>())
                return;

            flyout.Closing += OnMenuFlyoutClosing;
            flyout.Closed += OnMenuFlyoutClosed;
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
            TryRun(control);
    }

    private static void TryRun(Control control)
    {
        if (control is MenuFlyoutPresenter presenter)
            _lastMenuFlyoutPresenter = new WeakReference<MenuFlyoutPresenter>(presenter);

        var now = Environment.TickCount64;
        if (now - control.GetValue(LastRunProperty) < ReopenGuardMs)
            return; // collapse the popup's double-attach into a single animation
        control.SetValue(LastRunProperty, now);

        EnsureTransitions(control, TimeSpan.FromMilliseconds(OpenDurationMs));

        // Start hidden + nudged down, then settle into place on the next frame so the
        // transitions animate the change instead of snapping straight to the end state.
        control.Opacity = 0;
        control.RenderTransform = TransformOperations.Parse($"translateY({OpenOffsetY}px)");
        Dispatcher.UIThread.Post(() =>
        {
            control.Opacity = 1;
            control.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Render);
    }

    private static void OnContextMenuClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        if (menu.GetValue(CloseAfterAnimationProperty))
        {
            menu.SetValue(CloseAfterAnimationProperty, false);
            return;
        }

        if (menu.GetValue(CloseAnimationRunningProperty))
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        menu.SetValue(CloseAnimationRunningProperty, true);
        RunCloseAnimation(menu, () =>
        {
            if (!menu.IsOpen)
            {
                ResetCloseState(menu);
                return;
            }

            menu.SetValue(CloseAfterAnimationProperty, true);
            menu.Close();
        });
    }

    private static void OnContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            ResetCloseState(menu);
    }

    private static void OnMenuFlyoutClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        if (flyout.GetValue(CloseAfterAnimationProperty))
        {
            flyout.SetValue(CloseAfterAnimationProperty, false);
            return;
        }

        if (flyout.GetValue(CloseAnimationRunningProperty))
        {
            e.Cancel = true;
            return;
        }

        if (!TryGetLastMenuFlyoutPresenter(out var presenter))
            return;

        e.Cancel = true;
        flyout.SetValue(CloseAnimationRunningProperty, true);
        RunCloseAnimation(presenter, () =>
        {
            if (!flyout.IsOpen)
            {
                ResetCloseState(flyout);
                ResetMenuTransform(presenter);
                return;
            }

            flyout.SetValue(CloseAfterAnimationProperty, true);
            flyout.Hide();
        });
    }

    private static void OnMenuFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        ResetCloseState(flyout);
        if (TryGetLastMenuFlyoutPresenter(out var presenter))
            ResetMenuTransform(presenter);
    }

    private static void RunCloseAnimation(Control control, Action afterAnimation)
    {
        EnsureTransitions(control, TimeSpan.FromMilliseconds(CloseDurationMs));
        control.Opacity = 0;
        control.RenderTransform = TransformOperations.Parse($"translateY({CloseOffsetY}px)");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CloseDurationMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            afterAnimation();
        };
        timer.Start();
    }

    private static bool TryGetLastMenuFlyoutPresenter(out MenuFlyoutPresenter presenter)
    {
        if (_lastMenuFlyoutPresenter?.TryGetTarget(out var target) == true &&
            target.GetVisualRoot() is not null)
        {
            presenter = target;
            return true;
        }

        presenter = null!;
        return false;
    }

    private static void ResetCloseState(AvaloniaObject target)
    {
        target.SetValue(CloseAnimationRunningProperty, false);
        target.SetValue(CloseAfterAnimationProperty, false);
    }

    private static void ResetMenuTransform(Control control)
    {
        control.Opacity = 1;
        control.RenderTransform = TransformOperations.Parse("translateY(0px)");
    }

    private static void EnsureTransitions(Control control, TimeSpan duration)
    {
        control.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = new CubicEaseOut() },
        };
    }
}
