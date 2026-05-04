using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

public static class MomentumScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsEnabled", typeof(MomentumScrollBehavior));

    public static readonly AttachedProperty<double> FrictionProperty =
        AvaloniaProperty.RegisterAttached<InputElement, double>("Friction", typeof(MomentumScrollBehavior), 0.94);

    public static readonly AttachedProperty<double> WheelVelocityProperty =
        AvaloniaProperty.RegisterAttached<InputElement, double>("WheelVelocity", typeof(MomentumScrollBehavior), 13.0);

    public static readonly AttachedProperty<double> FastWheelReferenceMsProperty =
        AvaloniaProperty.RegisterAttached<InputElement, double>("FastWheelReferenceMs", typeof(MomentumScrollBehavior), 80.0);

    public static readonly AttachedProperty<double> MaxWheelSpeedMultiplierProperty =
        AvaloniaProperty.RegisterAttached<InputElement, double>("MaxWheelSpeedMultiplier", typeof(MomentumScrollBehavior), 2.4);

    private static readonly AttachedProperty<MomentumScrollState?> StateProperty =
        AvaloniaProperty.RegisterAttached<InputElement, MomentumScrollState?>("State", typeof(MomentumScrollBehavior));

    static MomentumScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<InputElement>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(InputElement element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(InputElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetFriction(InputElement element) => element.GetValue(FrictionProperty);
    public static void SetFriction(InputElement element, double value) => element.SetValue(FrictionProperty, value);

    public static double GetWheelVelocity(InputElement element) => element.GetValue(WheelVelocityProperty);
    public static void SetWheelVelocity(InputElement element, double value) => element.SetValue(WheelVelocityProperty, value);

    public static double GetFastWheelReferenceMs(InputElement element) => element.GetValue(FastWheelReferenceMsProperty);
    public static void SetFastWheelReferenceMs(InputElement element, double value) => element.SetValue(FastWheelReferenceMsProperty, value);

    public static double GetMaxWheelSpeedMultiplier(InputElement element) => element.GetValue(MaxWheelSpeedMultiplierProperty);
    public static void SetMaxWheelSpeedMultiplier(InputElement element, double value) => element.SetValue(MaxWheelSpeedMultiplierProperty, value);

    private static MomentumScrollState? GetState(InputElement element) => element.GetValue(StateProperty);
    private static void SetState(InputElement element, MomentumScrollState? value) => element.SetValue(StateProperty, value);

    private static void OnIsEnabledChanged(InputElement element, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            var state = GetState(element) ?? new MomentumScrollState(element);
            SetState(element, state);
            element.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            element.DetachedFromVisualTree += OnDetachedFromVisualTree;
        }
        else
        {
            Disable(element);
        }
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is InputElement element)
            GetState(element)?.Stop();
    }

    private static void Disable(InputElement element)
    {
        element.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
        element.DetachedFromVisualTree -= OnDetachedFromVisualTree;
        GetState(element)?.Stop();
        SetState(element, null);
    }

    private static void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not InputElement element || Math.Abs(e.Delta.Y) < 0.01)
            return;

        var state = GetState(element);
        if (state == null)
            return;

        var scrollViewer = state.GetScrollViewer();
        if (scrollViewer == null || scrollViewer.Extent.Height <= scrollViewer.Viewport.Height)
            return;

        var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        if ((scrollViewer.Offset.Y <= 0 && e.Delta.Y > 0) ||
            (scrollViewer.Offset.Y >= maxY && e.Delta.Y < 0))
            return;

        e.Handled = true;

        var wheelVelocity = Math.Max(0, GetWheelVelocity(element));
        var velocityY = -e.Delta.Y * wheelVelocity * state.GetWheelSpeedMultiplier();
        state.AddVelocity(velocityY);
    }

    private sealed class MomentumScrollState
    {
        private readonly InputElement _element;
        private ScrollViewer? _scrollViewer;
        private double _velocityY;
        private long _lastWheelTimestamp;
        private bool _isRunning;
        private bool _isFrameQueued;

        public MomentumScrollState(InputElement element)
        {
            _element = element;
        }

        public ScrollViewer? GetScrollViewer()
        {
            if (_scrollViewer != null)
                return _scrollViewer;

            _scrollViewer = _element as ScrollViewer ?? (_element as Control)?.FindDescendantOfType<ScrollViewer>();
            return _scrollViewer;
        }

        public double GetWheelSpeedMultiplier()
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastWheelTimestamp == 0)
            {
                _lastWheelTimestamp = now;
                return 1.0;
            }

            var elapsedMs = (now - _lastWheelTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastWheelTimestamp = now;
            if (elapsedMs <= 0)
                return GetMaxWheelSpeedMultiplier(_element);

            var referenceMs = Math.Max(1, GetFastWheelReferenceMs(_element));
            return Math.Clamp(referenceMs / elapsedMs, 1.0, Math.Max(1.0, GetMaxWheelSpeedMultiplier(_element)));
        }

        public void AddVelocity(double velocityY)
        {
            _velocityY += velocityY;
            _isRunning = true;
            QueueNextFrame();
        }

        public void Stop()
        {
            _isRunning = false;
            _velocityY = 0;
            _lastWheelTimestamp = 0;
        }

        private void Render()
        {
            _isFrameQueued = false;
            if (!_isRunning)
                return;

            var scrollViewer = GetScrollViewer();
            if (scrollViewer == null)
            {
                Stop();
                return;
            }

            if (Math.Abs(_velocityY) < 0.2)
            {
                Stop();
                return;
            }

            var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var nextY = Math.Clamp(scrollViewer.Offset.Y + _velocityY, 0, maxY);
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextY);

            if (nextY <= 0 || nextY >= maxY)
            {
                Stop();
                return;
            }

            var friction = Math.Clamp(GetFriction(_element), 0.80, 0.995);
            _velocityY *= friction;
            QueueNextFrame();
        }

        private void QueueNextFrame()
        {
            if (!_isRunning || _isFrameQueued)
                return;

            _isFrameQueued = true;
            Dispatcher.UIThread.Post(Render, DispatcherPriority.Render);
        }
    }
}
