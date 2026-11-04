using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Noctis.Helpers;

/// <summary>
/// Opens and shuts every ComboBox drop-down with the app menus' pop (owner 10-29: a list must
/// open and close like a menu, in Settings, on a plugin's page and in plugin windows alike):
/// the card grows out of the click while it fades in and its rows settle in one after another
/// (MenuOpenAnimation.PopIn), and it shrinks back and fades on close (PopOut). One class handler
/// on <see cref="ComboBox.IsDropDownOpenProperty"/> covers every ComboBox in the app, including
/// ones templated in later and the ones in plugin windows (user ask 09-08, then a fade + glide).
/// </summary>
/// <remarks>
/// The Fluent template hosts the list in Popup PART_Popup whose child is the PopupBorder.
/// The border is what animates (MenuOpenAnimation writes its start state with transitions off,
/// then posts the resting state at Render priority so the transitions carry it in).
///
/// Closing must never tear the popup down mid-fade. On Windows the popup is its own native
/// window, so a close followed by a re-open destroys and recreates that window and the user
/// sees a blink (09-08 report). The template binds Popup.IsOpen to IsDropDownOpen, so the
/// only way to keep the popup alive is to catch IsDropDownOpen going false BEFORE the
/// binding pushes it — class handlers run before binding subscribers — and put it straight
/// back to true; the binding then reads the current (true) value and the popup never
/// closes. That works for every close the ComboBox itself initiates (item click, Escape,
/// clicking the box). Light dismiss is the exception: there the popup closes itself first,
/// and the property flips afterwards. So the popup's own light dismiss is switched off and
/// re-implemented here (a press on the owner window outside the box, the box moving under
/// the popup because the page scrolled, or the window deactivating), routed through IsDropDownOpen so it takes the animated path too. A close
/// that still arrives from the popup itself (owner detached, etc.) is left alone.
/// </remarks>
public static class ComboBoxDropDownAnimator
{
    // The fade + glide curves, kept for DropDownFlyoutMotion's flyouts (BuildOpen / BuildClose).
    // Our own bezier, not Avalonia.SplineEasing: see CubicBezierEase for why.
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Avalonia.Animation.Easings.Easing OpenEase = new CubicBezierEase(0.16, 1.0, 0.3, 1.0);
    private static readonly Avalonia.Animation.Easings.Easing CloseEase = new CubicBezierEase(0.4, 0.0, 0.7, 0.4);
    private static readonly ConditionalWeakTable<ComboBox, State> States = new();
    private static bool _installed;

    private sealed class State
    {
        /// <summary>A close is being held open for its fade.</summary>
        public bool ClosingHeld;
        /// <summary>The next close skips the fade (window deactivated).</summary>
        public bool Plain;
        /// <summary>The popup body whose Opacity is watched to know when the fade has landed.</summary>
        public Control? WatchedBody;
        /// <summary>Our light-dismiss: owner top level + the handlers on it, while open.</summary>
        public TopLevel? Owner;
        public EventHandler<PointerPressedEventArgs>? PressHandler;
        public EventHandler? LayoutHandler;
        public EventHandler? DeactivatedHandler;
        /// <summary>Where the box sat in the owner when the list opened.</summary>
        public Point Anchor;
    }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>(static (box, e) =>
        {
            var state = States.GetOrCreateValue(box);
            if (e.NewValue is true)
            {
                // Our own re-assert during the closing fade: nothing to animate in.
                if (state.ClosingHeld) return;
                if (FindPopup(box) is not { Child: Control body } popup) return;
                // Liquid Glass first: it picks what the motion fades (GitHub #169).
                GlassMenus.ApplyCardPopup(popup);
                InstallDismiss(box, state, popup);
                MenuOpenAnimation.PopIn(body);
                return;
            }

            if (state.ClosingHeld)
            {
                // The real close after the fade (or the user closed it again mid-fade).
                state.ClosingHeld = false;
                RemoveDismiss(state);
                return;
            }
            if (state.Plain)
            {
                state.Plain = false;
                RemoveDismiss(state);
                return;
            }
            if (FindPopup(box) is not { Child: Control fadeBody } fadePopup || !fadePopup.IsOpen)
            {
                // The popup closed itself (owner detached, etc.): the property change is
                // the echo of that, and there is no popup left to fade.
                RemoveDismiss(state);
                return;
            }

            // Caught before the template binding runs: re-asserting true here keeps the
            // popup open through the fade (the binding reads the current value).
            state.ClosingHeld = true;
            box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
            WatchFadeOut(box, state, fadeBody);
            MenuOpenAnimation.PopOut(fadeBody);
        });
    }

    /// <summary>Replaces the popup's light dismiss with one that closes through IsDropDownOpen.</summary>
    private static void InstallDismiss(ComboBox box, State state, Popup popup)
    {
        popup.SetCurrentValue(Popup.IsLightDismissEnabledProperty, false);
        if (state.Owner != null) return;
        if (TopLevel.GetTopLevel(box) is not { } owner) return;

        state.Owner = owner;
        state.PressHandler = (_, e) =>
        {
            if (!box.IsDropDownOpen || state.ClosingHeld) return;
            if (e.Source is Visual source)
            {
                // The box toggles itself; presses inside an overlay-hosted popup are its own.
                if (source == box || box.IsVisualAncestorOf(source)) return;
                if (popup.Child is Visual body && (source == body || body.IsVisualAncestorOf(source))) return;
            }
            e.Handled = true; // light dismiss swallows the press too
            box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
        };
        owner.AddHandler(InputElement.PointerPressedEvent, state.PressHandler, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The popup never follows its box: on Windows it is its own window, and when the
        // page scrolls under it (wheel, scrollbar drag, keys) it stays where it opened,
        // detached from the box (09-19, Settings > Language). So the list closes the moment
        // the box moves in the owner. Plain, not faded: a fading ghost over the wrong card
        // is the glitch, and a fade already in flight is cut short for the same reason.
        state.Anchor = box.TranslatePoint(default, owner) ?? default;
        state.LayoutHandler = (_, _) =>
        {
            if (!box.IsDropDownOpen) return;
            if (box.TranslatePoint(default, owner) is not { } now) return;
            if (Math.Abs(now.X - state.Anchor.X) < 0.5 && Math.Abs(now.Y - state.Anchor.Y) < 0.5) return;
            if (!state.ClosingHeld) state.Plain = true;
            box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
        };
        owner.LayoutUpdated += state.LayoutHandler;

        if (owner is Window window)
        {
            state.DeactivatedHandler = (_, _) =>
            {
                if (!box.IsDropDownOpen) return;
                state.Plain = true;
                box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            };
            window.Deactivated += state.DeactivatedHandler;
        }
    }

    private static void RemoveDismiss(State state)
    {
        if (state.Owner is not { } owner) return;
        if (state.PressHandler != null)
            owner.RemoveHandler(InputElement.PointerPressedEvent, state.PressHandler);
        if (state.LayoutHandler != null)
            owner.LayoutUpdated -= state.LayoutHandler;
        if (owner is Window window && state.DeactivatedHandler != null)
            window.Deactivated -= state.DeactivatedHandler;
        state.Owner = null;
        state.PressHandler = null;
        state.LayoutHandler = null;
        state.DeactivatedHandler = null;
    }

    /// <summary>The real close fires when the fade-out lands on 0, so the two can never drift
    /// apart (a timer could close early on a slow frame, or leave a blank popup behind). The
    /// fade is Opacity, or MenuOpenAnimation.Fade on a Liquid Glass list (GlassMenus): watching
    /// only Opacity left a glass list open for good after its fade.</summary>
    private static void WatchFadeOut(ComboBox box, State state, Control body)
    {
        if (ReferenceEquals(state.WatchedBody, body)) return;
        state.WatchedBody = body;
        body.PropertyChanged += (_, e) =>
        {
            if (!state.ClosingHeld) return;
            var fade = MenuOpenAnimation.FadePropertyOf(body);
            if (e.Property != fade || body.GetValue(fade) > 0.001) return;
            box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
        };
    }

    private static Popup? FindPopup(ComboBox box) =>
        box.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup");

    /// <summary>The same curves for a Flyout that should read as a drop-down (<see cref="DropDownFlyoutMotion"/>).</summary>
    internal static Transitions BuildOpen(AvaloniaProperty? fade = null) => Build(OpenDuration, OpenEase, fade);
    internal static Transitions BuildClose(AvaloniaProperty? fade = null) => Build(CloseDuration, CloseEase, fade);

    /// <summary>Open and close get their own curves, so the transition set is rebuilt per
    /// phase. The fade lands a touch before the transform so the sheet never shows a hard
    /// edge while it is still moving. <paramref name="fade"/>: what fades (Opacity, or
    /// MenuOpenAnimation.Fade on a Liquid Glass card).</summary>
    private static Transitions Build(TimeSpan duration, Avalonia.Animation.Easings.Easing ease, AvaloniaProperty? fade) => new()
    {
        new DoubleTransition { Property = fade ?? Visual.OpacityProperty, Duration = duration * 0.85, Easing = ease },
        new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = ease },
    };
}
