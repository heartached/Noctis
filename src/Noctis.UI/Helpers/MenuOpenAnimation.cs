using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

    /// <summary>
    /// Set (by a style) on a menu whose chrome is a <c>GlassPanel</c> (GitHub #104): the motion
    /// then fades <see cref="FadeProperty"/> instead of <see cref="Visual.Opacity"/>. The GPU
    /// backend does not apply an ancestor's opacity layer to the glass's custom Skia blur, so an
    /// Opacity fade would leave the frost at full strength while the menu around it vanishes.
    /// </summary>
    public static readonly AttachedProperty<bool> UseFadeProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("UseFade", typeof(MenuOpenAnimation));

    public static void SetUseFade(Control control, bool value) => control.SetValue(UseFadeProperty, value);
    public static bool GetUseFade(Control control) => control.GetValue(UseFadeProperty);

    /// <summary>
    /// 0..1 fade a <see cref="UseFadeProperty"/> menu's template binds to its GlassPanel's
    /// <c>Fade</c> and to its content's Opacity, so the frost and the items fade together.
    /// </summary>
    public static readonly AttachedProperty<double> FadeProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Fade", typeof(MenuOpenAnimation), 1.0);

    public static void SetFade(Control control, double value) => control.SetValue(FadeProperty, value);
    public static double GetFade(Control control) => control.GetValue(FadeProperty);

    /// <summary>
    /// Non-zero: the open motion slides in sideways from this X offset instead of rising up.
    /// Set on submenu cards, which open beside their row (-8 = out of the parent menu).
    /// </summary>
    public static readonly AttachedProperty<double> OffsetXProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("OffsetX", typeof(MenuOpenAnimation));

    public static void SetOffsetX(Control control, double value) => control.SetValue(OffsetXProperty, value);
    public static double GetOffsetX(Control control) => control.GetValue(OffsetXProperty);

    /// <summary>
    /// The "pop" open (Albums-page album menu, trial 10-09): the card grows out of the click point
    /// (<see cref="PopAnchorProperty"/>) while it fades in, then its rows settle in one after
    /// another, and it closes with that motion reversed. Set on every ContextMenu and
    /// MenuFlyoutPresenter by the global menu style (Styles.axaml); replaces the rise-up there.
    /// </summary>
    public static readonly AttachedProperty<bool> PopProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Pop", typeof(MenuOpenAnimation));

    public static void SetPop(Control control, bool value) => control.SetValue(PopProperty, value);
    public static bool GetPop(Control control) => control.GetValue(PopProperty);

    /// <summary>Screen point (px) the <see cref="PopProperty"/> card grows from; set before each open.
    /// Null: the last pointer press if it was recent (the click that opened the menu), else the
    /// card's top-left corner.</summary>
    public static readonly AttachedProperty<PixelPoint?> PopAnchorProperty =
        AvaloniaProperty.RegisterAttached<Control, PixelPoint?>("PopAnchor", typeof(MenuOpenAnimation));

    public static void SetPopAnchor(Control control, PixelPoint? value) => control.SetValue(PopAnchorProperty, value);
    public static PixelPoint? GetPopAnchor(Control control) => control.GetValue(PopAnchorProperty);

    /// <summary>
    /// Set on a <see cref="MenuFlyoutPresenter"/> (plugin windows): when it opens, the
    /// <see cref="MenuFlyout"/> it belongs to gets <see cref="EnableFlyoutCloseProperty"/>, so a
    /// plugin's menu closes with the pop like the app's own (a plugin can't set it: plugins only
    /// see the SDK). The flyout is found on the control it opened from (Button.Flyout,
    /// ContextFlyout or FlyoutBase.AttachedFlyout).
    /// </summary>
    public static readonly AttachedProperty<bool> AutoFlyoutCloseProperty =
        AvaloniaProperty.RegisterAttached<MenuFlyoutPresenter, bool>("AutoFlyoutClose", typeof(MenuOpenAnimation));

    public static void SetAutoFlyoutClose(MenuFlyoutPresenter presenter, bool value) => presenter.SetValue(AutoFlyoutCloseProperty, value);
    public static bool GetAutoFlyoutClose(MenuFlyoutPresenter presenter) => presenter.GetValue(AutoFlyoutCloseProperty);

    private const double PopScale = 0.9;
    private const double PopDurationMs = 280;
    private const double PopFadeMs = 160;
    private const double PopRowDurationMs = 240;
    private const double PopRowStaggerMs = 16;
    private const double PopRowOffsetY = 6;
    private const int PopStaggeredRows = 10;
    private const double PopCloseMs = 150;
    private const double PopCloseScale = 0.94;
    // Accelerate out: leaves gently, gone quickly.
    private static readonly Avalonia.Animation.Easings.Easing PopCloseEase = new CubicBezierEase(0.4, 0, 1, 1);
    // Soft decelerate (easeOutQuint-like): quick start, long gentle landing, no overshoot.
    private static readonly Avalonia.Animation.Easings.Easing PopEase = new CubicBezierEase(0.22, 1, 0.36, 1);

    // The last pointer press anywhere in the app (screen px + when): the default PopAnchor, so a
    // menu grows out of the right-click or the button press that opened it.
    private static PixelPoint _lastPress;
    private static long _lastPressAt;
    private const long PressAnchorMaxAgeMs = 1500;

    /// <summary>Tests: drop the remembered press so an earlier test's click doesn't anchor this menu.</summary>
    internal static void ForgetLastPress() => _lastPressAt = 0;

    private static readonly AttachedProperty<long> LastRunProperty =
        AvaloniaProperty.RegisterAttached<Control, long>("LastRun", typeof(MenuOpenAnimation));

    private static readonly AttachedProperty<bool> CloseAnimationRunningProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("CloseAnimationRunning", typeof(MenuOpenAnimation));

    private static readonly AttachedProperty<bool> CloseAfterAnimationProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("CloseAfterAnimation", typeof(MenuOpenAnimation));

    static MenuOpenAnimation()
    {
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((top, e) =>
        {
            _lastPress = top.PointToScreen(e.GetPosition(top));
            _lastPressAt = Environment.TickCount64;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

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
            if (TopLevel.GetTopLevel(control) is not null)
                TryRun(control);
        });

        // A Pop menu also closes with the motion reversed (see OnPopMenuClosing for how it
        // avoids the stranded-popup trap described above).
        PopProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (control is not ContextMenu menu)
                return;
            menu.Closing -= OnPopMenuClosing;
            menu.Closed -= OnPopMenuClosed;
            if (!args.GetNewValue<bool>())
                return;
            menu.Closing += OnPopMenuClosing;
            menu.Closed += OnPopMenuClosed;
        });

        AutoFlyoutCloseProperty.Changed.AddClassHandler<MenuFlyoutPresenter>((presenter, args) =>
        {
            presenter.AttachedToVisualTree -= OnAutoFlyoutPresenterAttached;
            if (!args.GetNewValue<bool>())
                return;
            presenter.AttachedToVisualTree += OnAutoFlyoutPresenterAttached;
            if (TopLevel.GetTopLevel(presenter) is not null)
                EnableOwnFlyoutClose(presenter);
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
        var now = Environment.TickCount64;
        if (now - control.GetValue(LastRunProperty) < ReopenGuardMs)
            return; // collapse the popup's double-attach into a single animation
        control.SetValue(LastRunProperty, now);

        if (GetPop(control))
        {
            RunPop(control);
            return;
        }

        EnsureTransitions(control, TimeSpan.FromMilliseconds(OpenDurationMs));

        // Start hidden + nudged down, then settle into place on the next frame so the
        // transitions animate the change instead of snapping straight to the end state.
        var fade = FadePropertyOf(control);
        var offsetX = GetOffsetX(control);
        ShowOpacityUnderFade(control, fade);
        control.SetValue(fade, 0.0);
        control.RenderTransform = TransformOperations.Parse(offsetX != 0
            ? $"translateX({offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)"
            : $"translateY({OpenOffsetY}px)");
        Dispatcher.UIThread.Post(() =>
        {
            control.SetValue(fade, 1.0);
            control.RenderTransform = TransformOperations.Parse(offsetX != 0 ? "translateX(0px)" : "translateY(0px)");
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// <see cref="PopProperty"/> open: the card scales up from the click point while it fades in,
    /// and its visible rows fade + rise into place one after another. Start values are set with
    /// no transitions (so they snap), then the targets are set a frame later with them.
    /// </summary>
    private static void RunPop(Control card)
    {
        var run = card.GetValue(LastRunProperty);
        var fade = FadePropertyOf(card);
        card.Transitions = null;
        card.IsHitTestVisible = true;
        ShowOpacityUnderFade(card, fade);
        card.SetValue(fade, 0.0);
        card.RenderTransform = TransformOperations.Parse(
            $"scale({PopScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

        Dispatcher.UIThread.Post(() =>
        {
            if (card.GetValue(LastRunProperty) != run || card.GetValue(CloseAnimationRunningProperty))
                return; // reopened or closing meanwhile; that run owns the card

            card.RenderTransformOrigin = PopOrigin(card);
            // A menu is its own list; a drop-down's card (Border#PopupBorder) shows its ComboBox's rows.
            var rows = (card as ItemsControl ?? card.TemplatedParent as ItemsControl) is { } items
                ? items.GetRealizedContainers().Where(c => c.IsVisible).ToList()
                : new List<Control>();
            foreach (var row in rows)
            {
                row.Transitions = null;
                row.Opacity = 0;
                row.RenderTransform = TransformOperations.Parse($"translateY({PopRowOffsetY}px)");
            }

            card.Transitions = new Transitions
            {
                new DoubleTransition { Property = fade, Duration = TimeSpan.FromMilliseconds(PopFadeMs), Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(PopDurationMs), Easing = PopEase },
            };
            card.SetValue(fade, 1.0);
            card.RenderTransform = TransformOperations.Parse("scale(1)");

            Dispatcher.UIThread.Post(() =>
            {
                if (card.GetValue(LastRunProperty) != run || card.GetValue(CloseAnimationRunningProperty))
                    return;
                for (var i = 0; i < rows.Count; i++)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Min(i, PopStaggeredRows) * PopRowStaggerMs);
                    var duration = TimeSpan.FromMilliseconds(PopRowDurationMs);
                    rows[i].Transitions = new Transitions
                    {
                        new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Delay = delay, Easing = new CubicEaseOut() },
                        new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Delay = delay, Easing = PopEase },
                    };
                    rows[i].Opacity = 1;
                    rows[i].RenderTransform = TransformOperations.Parse("translateY(0px)");
                }

                // Once every row has landed, hand the rows back to their styles.
                DispatcherTimer.RunOnce(() =>
                {
                    foreach (var row in rows)
                    {
                        row.Transitions = null;
                        row.ClearValue(Visual.OpacityProperty);
                        row.ClearValue(Visual.RenderTransformProperty);
                    }
                }, TimeSpan.FromMilliseconds(PopRowDurationMs + PopStaggeredRows * PopRowStaggerMs + 60));
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// <see cref="PopProperty"/> close: the open motion in reverse (shrink back toward the click
    /// point + fade). The real close is held for <see cref="PopCloseMs"/>, which is what once
    /// stranded right-click menus open but invisible (see the note in the static constructor).
    /// This path guards against that: the card stops taking input the moment the close starts,
    /// the deferred close is forced through <see cref="CloseNow"/> (which also restores the card),
    /// and a reopen or any other close cancels the pending one instead of racing it.
    /// </summary>
    private static void OnPopMenuClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        // Our own forced close (CloseNow): let it through. RestorePopCard clears the flag.
        if (menu.GetValue(CloseAfterAnimationProperty))
            return;

        // Never refuse a close we didn't start: a second close while fading (or one from a
        // teardown) goes straight through and simply ends the fade early. Popup closes itself
        // from OnDetachedFromLogicalTree when its window closes (Avalonia 12.1.3 Popup.cs:651),
        // and cancelling that left the popup open while the window tore down, which threw in
        // StyledElement.OnDetachedFromLogicalTreeCore (caught by AppMenusV2Tests).
        if (menu.GetValue(CloseAnimationRunningProperty))
            return;

        // Animate only while the menu, its popup and the control it opened from are all still in
        // a live window; otherwise this close is part of a teardown or navigation, so close now.
        if (TopLevel.GetTopLevel(menu) is null ||
            menu.Parent is not Popup popup ||
            !((Avalonia.LogicalTree.ILogical)popup).IsAttachedToLogicalTree ||
            popup.PlacementTarget is { } target && TopLevel.GetTopLevel(target) is null)
            return;

        e.Cancel = true;
        menu.SetValue(CloseAnimationRunningProperty, true);
        StartPopClose(menu);

        DispatcherTimer.RunOnce(() =>
        {
            if (menu.GetValue(CloseAnimationRunningProperty))
                CloseNow(menu); // still ours (nothing else closed or reopened it meanwhile)
        }, TimeSpan.FromMilliseconds(PopCloseMs));
    }

    /// <summary>The pop close motion: inert at once (an invisible-but-open card must never take a
    /// click or a scroll), then shrink toward the open's origin while fading out.</summary>
    private static void StartPopClose(Control card)
    {
        var duration = TimeSpan.FromMilliseconds(PopCloseMs);
        var fade = FadePropertyOf(card);
        card.IsHitTestVisible = false;
        card.Transitions = new Transitions
        {
            new DoubleTransition { Property = fade, Duration = duration, Easing = PopCloseEase },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = PopCloseEase },
        };
        card.SetValue(fade, 0.0);
        card.RenderTransform = TransformOperations.Parse(
            $"scale({PopCloseScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    /// <summary>
    /// Closes a <see cref="PopProperty"/> menu at once, skipping (or cutting short) its close
    /// motion. Call it before reopening the same menu: a reopen must not wait on, or be swallowed
    /// by, a close that is still fading out.
    /// </summary>
    public static void CloseNow(ContextMenu menu)
    {
        if (menu.IsOpen)
        {
            menu.SetValue(CloseAfterAnimationProperty, true);
            // Close the menu's Popup (its logical parent) directly, NOT via menu.Close().
            // ContextMenu.Close() works by setting Popup.IsOpen = false; when that close was
            // cancelled (the close motion cancels it), Popup.CloseCore returns early and leaves
            // IsOpen false while the popup is still shown (Avalonia 12.1.3 Popup.cs CloseCore).
            // Every later menu.Close() then sets false -> false, which changes nothing, so the
            // menu could never close again: the "stranded open but invisible" bug noted above.
            // Popup.Close() calls CloseCore whatever IsOpen says.
            if (menu.Parent is Popup popup)
                popup.Close();
            else
                menu.Close();
        }
        RestorePopCard(menu);
    }

    private static void OnPopMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            RestorePopCard(menu);
    }

    /// <summary>Back to a visible, clickable, idle card once the popup is gone, so no close state
    /// can leak into the next open. LastRun is cleared too: a reopen is always a real open, even
    /// inside the double-attach guard window.</summary>
    private static void RestorePopCard(ContextMenu menu)
    {
        ResetCloseState(menu);
        menu.Transitions = null;
        menu.IsHitTestVisible = true;
        menu.Opacity = 1;
        if (GetUseFade(menu)) SetFade(menu, 1);
        menu.RenderTransform = TransformOperations.Parse("scale(1)");
        menu.SetValue(LastRunProperty, 0L);
    }

    // ── The pop for a card that is not a menu: every drop-down list (ComboBoxDropDownAnimator) ──

    /// <summary>How long <see cref="PopOut"/> takes: the caller holds the real close this long.</summary>
    public static TimeSpan PopCloseDuration => TimeSpan.FromMilliseconds(PopCloseMs);

    /// <summary>
    /// The menus' pop open on any card (owner 10-29: a drop-down list opens like a menu): it grows
    /// out of the click that opened it while it fades in, then its rows settle in one after
    /// another (a ComboBox's list card finds its rows on the ComboBox it belongs to). Drives
    /// Opacity, or <see cref="FadeProperty"/> on a <see cref="UseFadeProperty"/> card.
    /// </summary>
    public static void PopIn(Control card)
    {
        ResetCloseState(card);
        card.SetValue(LastRunProperty, Environment.TickCount64);
        RunPop(card);
    }

    /// <summary>The menus' pop close on any card: inert at once, then shrinks back toward where it
    /// grew from while it fades out, over <see cref="PopCloseDuration"/>. The caller closes the
    /// popup when it lands (the card is left at its end state; <see cref="PopIn"/> resets it).</summary>
    public static void PopOut(Control card)
    {
        card.SetValue(LastRunProperty, 0L); // an open still settling stops where it is
        StartPopClose(card);
    }

    // ── AutoFlyoutClose: a plugin's MenuFlyout closes with the pop ──

    private static void OnAutoFlyoutPresenterAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is MenuFlyoutPresenter presenter)
            EnableOwnFlyoutClose(presenter);
    }

    private static void EnableOwnFlyoutClose(MenuFlyoutPresenter presenter)
    {
        if (presenter.Parent is not Popup popup)
            return;
        var target = popup.PlacementTarget ?? popup.Parent as Control;
        if (target is null)
            return;
        var flyout = (target as Button)?.Flyout as MenuFlyout
                     ?? target.ContextFlyout as MenuFlyout
                     ?? FlyoutBase.GetAttachedFlyout(target) as MenuFlyout;
        if (flyout is not null && !GetEnableFlyoutClose(flyout))
            SetEnableFlyoutClose(flyout, true);
    }

    /// <summary>The <see cref="PopAnchorProperty"/> point inside the card, clamped to its edges, so
    /// the card grows out of the corner the pointer is at even when the menu flipped up or left.
    /// Mapped through the popup's top level: the card fills it, and unlike the card it carries no
    /// RenderTransform that would skew the mapping.</summary>
    private static RelativePoint PopOrigin(Control card)
    {
        var size = card.Bounds.Size;
        var anchor = GetPopAnchor(card) ??
            (Environment.TickCount64 - _lastPressAt <= PressAnchorMaxAgeMs ? _lastPress : (PixelPoint?)null);
        if (anchor is null || size.Width <= 0 || size.Height <= 0 ||
            TopLevel.GetTopLevel(card) is not { } top)
            return RelativePoint.TopLeft;

        var p = top.PointToClient(anchor.Value);
        // A card in the window's overlay layer (plugin windows) doesn't start at the top level's
        // corner: map through its parent, which carries no transform, to the card's own corner.
        if (card.GetVisualParent() is { } parent && !ReferenceEquals(parent, top) && top.TranslatePoint(p, parent) is { } inParent)
            p = new Point(inParent.X - card.Bounds.X, inParent.Y - card.Bounds.Y);
        return new RelativePoint(
            Math.Clamp(p.X, 0, size.Width), Math.Clamp(p.Y, 0, size.Height), RelativeUnit.Absolute);
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

        if (!TryGetPresenter(flyout, out var presenter))
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
        if (TryGetPresenter(flyout, out var presenter))
            ResetMenuTransform(presenter);
    }

    private static void RunCloseAnimation(Control control, Action afterAnimation)
    {
        var durationMs = CloseDurationMs;
        if (GetPop(control))
        {
            StartPopClose(control);
            durationMs = PopCloseMs;
        }
        else
        {
            EnsureTransitions(control, TimeSpan.FromMilliseconds(CloseDurationMs));
            control.SetValue(FadePropertyOf(control), 0.0);
            control.RenderTransform = TransformOperations.Parse($"translateY({CloseOffsetY}px)");
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            afterAnimation();
        };
        timer.Start();
    }

    /// <summary>
    /// The flyout's own card. This used to be the most recently attached presenter of any
    /// flyout: with two flyouts open, closing the first ran its close motion on the second
    /// (still open) card and never on its own. Avalonia 12 exposes PopupFlyoutBase.Popup, so
    /// the card is read from the flyout itself.
    /// </summary>
    private static bool TryGetPresenter(MenuFlyout flyout, out MenuFlyoutPresenter presenter)
    {
        if (flyout.Popup.Child is MenuFlyoutPresenter own && TopLevel.GetTopLevel(own) is not null)
        {
            presenter = own;
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
        if (GetUseFade(control)) SetFade(control, 1);
        control.IsHitTestVisible = true;
        control.RenderTransform = TransformOperations.Parse(GetPop(control) ? "scale(1)" : "translateY(0px)");
    }

    /// <summary>An open that fades <see cref="FadeProperty"/> (Liquid Glass) leaves Opacity alone,
    /// and a plain close leaves the card at Opacity 0: a card closed plain and opened again with
    /// glass on stayed invisible. Opacity goes back to 1 for such an open.</summary>
    private static void ShowOpacityUnderFade(Control card, StyledProperty<double> fade)
    {
        if (fade != Visual.OpacityProperty) card.Opacity = 1;
    }

    /// <summary>The property the open/close fade drives: <see cref="FadeProperty"/> on a glass
    /// menu (<see cref="UseFadeProperty"/>), Opacity everywhere else.</summary>
    internal static StyledProperty<double> FadePropertyOf(Control control) =>
        GetUseFade(control) ? FadeProperty : Visual.OpacityProperty;

    private static void EnsureTransitions(Control control, TimeSpan duration)
    {
        control.Transitions = new Transitions
        {
            new DoubleTransition { Property = FadePropertyOf(control), Duration = duration, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = new CubicEaseOut() },
        };
    }
}
