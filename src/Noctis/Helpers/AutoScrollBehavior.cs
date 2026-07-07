using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Path = Avalonia.Controls.Shapes.Path;

namespace Noctis.Helpers;

/// <summary>
/// Windows-style middle-click autoscroll. Enable once per <see cref="ScrollViewer"/> via a style
/// setter: <c>&lt;Setter Property="(helpers:AutoScrollBehavior.IsEnabled)" Value="True" /&gt;</c>
///
/// Middle-clicking a scrollable area drops an anchor (with an on-screen up/down indicator) and
/// enters autoscroll mode: moving the pointer above/below the anchor scrolls at a speed
/// proportional to the distance from it. Any click, key press, or wheel notch exits the mode.
///
/// Only one autoscroll session is active app-wide at a time. The behavior no-ops when the target
/// has nothing to scroll vertically, and nested scroll viewers resolve to the innermost one
/// (the pointer-pressed handler bubbles, and the first viewer to start the session marks the
/// event handled so ancestors skip it).
/// </summary>
public static class AutoScrollBehavior
{
    // Pointer can sit within this many px of the anchor without scrolling — matches the
    // indicator's radius so scrolling starts right at its edge.
    private const double DeadZone = 13;
    // Browser-style velocity curve in px/second, integrated against real elapsed time each
    // tick so speed stays accurate even when UI-thread ticks run late. A linear term gives
    // fine control near the anchor; a quadratic term takes over at range:
    // speed = dist * LinearGain + dist² * QuadraticGain, capped at MaxSpeed.
    private const double LinearGain = 6.0;
    private const double QuadraticGain = 0.09;
    private const double MaxSpeed = 6000;
    private const double IndicatorSize = 26;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsEnabled", typeof(AutoScrollBehavior));

    public static bool GetIsEnabled(InputElement element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(InputElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    // ── Single global session ──
    private static ScrollViewer? _scrollViewer;
    private static TopLevel? _topLevel;
    private static OverlayLayer? _overlay;
    private static Control? _indicator;
    private static DispatcherTimer? _timer;
    private static Point _anchor;
    private static double _pointerY;
    private static Cursor? _savedCursor;
    private static long _lastTickTimestamp;

    static AutoScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<InputElement>((element, args) =>
        {
            element.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            element.DetachedFromVisualTree -= OnDetachedFromVisualTree;

            if (!args.GetNewValue<bool>())
                return;

            // Bubble so the innermost scroll viewer under the pointer wins; it marks the event
            // handled once a session starts, so ancestor viewers (also enabled) skip it.
            element.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
            element.DetachedFromVisualTree += OnDetachedFromVisualTree;
        });
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(sender, _scrollViewer))
            Stop();
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Already scrolling → this click exits the mode (and is swallowed so it doesn't
        // also activate whatever is under the pointer).
        if (_scrollViewer != null)
        {
            Stop();
            e.Handled = true;
            return;
        }

        // An inner viewer already started a session for this same press.
        if (e.Handled)
            return;

        var point = e.GetCurrentPoint(sender as Visual);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
            return;

        var scrollViewer = sender as ScrollViewer
                           ?? (sender as Control)?.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer == null)
            return;

        // Nothing to scroll vertically — let the middle click fall through untouched.
        if (scrollViewer.Extent.Height <= scrollViewer.Viewport.Height + 1)
            return;

        Start(scrollViewer, e);
        e.Handled = true;
    }

    private static void Start(ScrollViewer scrollViewer, PointerPressedEventArgs e)
    {
        _scrollViewer = scrollViewer;
        _topLevel = TopLevel.GetTopLevel(scrollViewer);
        _overlay = OverlayLayer.GetOverlayLayer(scrollViewer);

        // Anchor + indicator live in overlay-layer space so position math and rendering agree.
        var space = (Visual?)_overlay ?? _topLevel;
        _anchor = space != null ? e.GetPosition(space) : e.GetPosition(scrollViewer);
        _pointerY = _anchor.Y;

        if (_overlay != null)
        {
            _indicator = BuildIndicator();
            Canvas.SetLeft(_indicator, _anchor.X - IndicatorSize / 2);
            Canvas.SetTop(_indicator, _anchor.Y - IndicatorSize / 2);
            _overlay.Children.Add(_indicator);
        }

        if (_topLevel != null)
        {
            _topLevel.AddHandler(InputElement.PointerMovedEvent, OnGlobalPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _topLevel.AddHandler(InputElement.PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            _topLevel.AddHandler(InputElement.PointerWheelChangedEvent, OnGlobalWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
            _topLevel.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        _savedCursor = scrollViewer.Cursor;
        scrollViewer.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void Stop()
    {
        _timer?.Stop();
        if (_timer != null) _timer.Tick -= OnTick;
        _timer = null;

        if (_topLevel != null)
        {
            _topLevel.RemoveHandler(InputElement.PointerMovedEvent, OnGlobalPointerMoved);
            _topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnGlobalPointerPressed);
            _topLevel.RemoveHandler(InputElement.PointerWheelChangedEvent, OnGlobalWheel);
            _topLevel.RemoveHandler(InputElement.KeyDownEvent, OnGlobalKeyDown);
        }

        if (_indicator != null && _overlay != null)
            _overlay.Children.Remove(_indicator);

        if (_scrollViewer != null)
            _scrollViewer.Cursor = _savedCursor;

        _indicator = null;
        _overlay = null;
        _topLevel = null;
        _scrollViewer = null;
        _savedCursor = null;
    }

    private static void OnGlobalPointerMoved(object? sender, PointerEventArgs e)
    {
        var space = (Visual?)_overlay ?? _topLevel;
        if (space != null)
            _pointerY = e.GetPosition(space).Y;
    }

    private static void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Stop();
        e.Handled = true;
    }

    private static void OnGlobalWheel(object? sender, PointerWheelEventArgs e) => Stop();

    private static void OnGlobalKeyDown(object? sender, KeyEventArgs e) => Stop();

    private static void OnTick(object? sender, EventArgs e)
    {
        var sv = _scrollViewer;
        if (sv == null)
        {
            Stop();
            return;
        }

        // Real elapsed time since the previous tick (DispatcherTimer isn't metronome-exact),
        // clamped so a stalled UI thread can't produce one giant jump.
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Min((now - _lastTickTimestamp) / (double)Stopwatch.Frequency, 0.1);
        _lastTickTimestamp = now;

        var delta = _pointerY - _anchor.Y;
        var magnitude = Math.Abs(delta) - DeadZone;
        if (magnitude <= 0)
            return;

        var speed = Math.Min(MaxSpeed, magnitude * (LinearGain + magnitude * QuadraticGain));
        var step = speed * dt * Math.Sign(delta);
        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var nextY = Math.Clamp(sv.Offset.Y + step, 0, maxY);
        if (Math.Abs(nextY - sv.Offset.Y) > 0.01)
            sv.Offset = new Vector(sv.Offset.X, nextY);
    }

    private static Control BuildIndicator()
    {
        var glyph = new SolidColorBrush(Color.Parse("#F2FFFFFF"));
        var up = new Path
        {
            Data = Geometry.Parse("M 0,4 L 4,0 L 8,4 Z"),
            Fill = glyph,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        var down = new Path
        {
            Data = Geometry.Parse("M 0,0 L 4,4 L 8,0 Z"),
            Fill = glyph,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        var dot = new Ellipse
        {
            Width = 3,
            Height = 3,
            Fill = glyph,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        // Stack the up-arrow / dot / down-arrow and center the whole group in the circle so
        // they're aligned on the same axis and balanced top-to-bottom.
        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 2,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        content.Children.Add(up);
        content.Children.Add(dot);
        content.Children.Add(down);

        return new Border
        {
            Width = IndicatorSize,
            Height = IndicatorSize,
            CornerRadius = new CornerRadius(IndicatorSize / 2),
            Background = new SolidColorBrush(Color.Parse("#D91C1C1C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF")),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 2,
                Blur = 8,
                Color = Color.Parse("#66000000"),
            }),
            IsHitTestVisible = false,
            Child = content,
        };
    }
}
