using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// Wheel-driven eased scrolling, enabled app-wide by the ScrollViewer style in Styles.axaml.
///
/// Replaced the previous momentum behavior (a fling whose velocity decayed once per
/// delivered frame, so on heavy pages — the album grid realizing a row of tiles — the whole
/// travel collapsed into a few large jumps). This instead chases a target offset, stepped by
/// the compositor frame clock (<c>TopLevel.RequestAnimationFrame</c>, same source the lyrics
/// scroll uses) and integrated against real elapsed time, so the curve is the same wall-clock
/// shape however many frames actually land.
///
/// A wheel notch moves only the target, never the position or the clock: speed is proportional
/// to the distance left, so it stays continuous when notches arrive mid-glide. (An earlier
/// version re-based a fixed-duration ease-out on every notch; that zeroed the elapsed time, so
/// the frame right after each notch barely moved and the next one lurched — a stutter at wheel
/// rate that only showed up while the wheel kept turning.)
///
/// Suspend it (<c>SetIsEnabled(sv, false)</c>) while a ComboBox dropdown hosted inside the
/// ScrollViewer's subtree is open — the Tunnel handler would otherwise eat wheel events
/// meant for the popup. See SettingsView and MetadataWindow.
/// </summary>
public static class SmoothScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsEnabled", typeof(SmoothScrollBehavior));

    /// <summary>Pixels travelled per wheel notch.</summary>
    public static readonly AttachedProperty<double> StepProperty =
        AvaloniaProperty.RegisterAttached<InputElement, double>("Step", typeof(SmoothScrollBehavior), 220.0);

    /// <summary>How long a notch takes to land, in ms (~99% of the way there).</summary>
    public static readonly AttachedProperty<double> SettleMsProperty =
        AvaloniaProperty.RegisterAttached<InputElement, double>("SettleMs", typeof(SmoothScrollBehavior), 380.0);

    private static readonly AttachedProperty<SmoothScrollState?> StateProperty =
        AvaloniaProperty.RegisterAttached<InputElement, SmoothScrollState?>("State", typeof(SmoothScrollBehavior));

    static SmoothScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<InputElement>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(InputElement element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(InputElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetStep(InputElement element) => element.GetValue(StepProperty);
    public static void SetStep(InputElement element, double value) => element.SetValue(StepProperty, value);

    public static double GetSettleMs(InputElement element) => element.GetValue(SettleMsProperty);
    public static void SetSettleMs(InputElement element, double value) => element.SetValue(SettleMsProperty, value);

    private static SmoothScrollState? GetState(InputElement element) => element.GetValue(StateProperty);
    private static void SetState(InputElement element, SmoothScrollState? value) => element.SetValue(StateProperty, value);

    private static void OnIsEnabledChanged(InputElement element, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            SetState(element, GetState(element) ?? new SmoothScrollState(element));
            // Tunnel so this runs before the ScrollViewer's own wheel handling; handledEventsToo
            // so an outer handler marking the event doesn't silently disable smoothing.
            element.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            element.DetachedFromVisualTree += OnDetachedFromVisualTree;
        }
        else
        {
            element.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
            element.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            GetState(element)?.Stop();
            SetState(element, null);
        }
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is InputElement element)
            GetState(element)?.Stop();
    }

    private static void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not InputElement element || Math.Abs(e.Delta.Y) < 0.01)
            return;

        var state = GetState(element);
        var scrollViewer = state?.GetScrollViewer();
        if (state == null || scrollViewer == null || scrollViewer.Extent.Height <= scrollViewer.Viewport.Height)
            return;

        // Wheel over a nested scrollable region: leave the event alone so the inner
        // ScrollViewer scrolls instead of the page.
        if (e.Source is Visual source)
        {
            var inner = source as ScrollViewer ?? source.FindAncestorOfType<ScrollViewer>();
            if (inner != null && inner != scrollViewer && inner.Extent.Height > inner.Viewport.Height)
                return;
        }

        var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        if ((scrollViewer.Offset.Y <= 0 && e.Delta.Y > 0) ||
            (scrollViewer.Offset.Y >= maxY && e.Delta.Y < 0))
            return;

        e.Handled = true;
        state.Push(e.Delta.Y, GetStep(element), GetSettleMs(element));
    }

    private sealed class SmoothScrollState
    {
        private readonly InputElement _element;
        private ScrollViewer? _scrollViewer;
        private double _currentY;
        private double _targetY;
        private double _appliedY = double.NaN;
        private double _settleMs = 1;
        private long _lastFrameTimestamp;
        private bool _isRunning;
        private bool _isFrameQueued;

        public SmoothScrollState(InputElement element)
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

        /// <summary>Moves the target a further <paramref name="step"/> px along the wheel direction.</summary>
        public void Push(double deltaY, double step, double settleMs)
        {
            var scrollViewer = GetScrollViewer();
            if (scrollViewer == null)
                return;

            var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var offsetY = scrollViewer.Offset.Y;

            // Fresh glide, or something other than us moved the offset (scrollbar drag,
            // saved-offset restore, ScrollIntoView) — re-seat on where the content actually is.
            if (!_isRunning || double.IsNaN(_appliedY) || Math.Abs(offsetY - _appliedY) > 1.0)
            {
                _currentY = offsetY;
                _targetY = offsetY;
                _lastFrameTimestamp = Stopwatch.GetTimestamp();
            }

            // Only the target moves — never the position or the clock. Speed stays
            // proportional to the distance left, so a notch arriving mid-glide adds to the
            // motion instead of restarting it (a restart stalls the very next frame, which
            // is what reads as stutter while the wheel keeps turning).
            _targetY = Math.Clamp(_targetY - deltaY * Math.Max(1, step), 0, maxY);
            _settleMs = Math.Max(1, settleMs);
            _isRunning = true;
            QueueNextFrame();
        }

        public void Stop()
        {
            _isRunning = false;
            _appliedY = double.NaN;
        }

        private void OnFrame(TimeSpan frameTime)
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

            // Extent grows as virtualized rows are realized, so re-clamp every frame.
            var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var targetY = Math.Clamp(_targetY, 0, maxY);

            var now = Stopwatch.GetTimestamp();
            // Real elapsed time, clamped so a stalled UI thread can't produce one giant jump.
            var dt = Math.Min((now - _lastFrameTimestamp) / (double)Stopwatch.Frequency, 0.1);
            _lastFrameTimestamp = now;

            // Exponential approach to the target. Velocity is proportional to the distance
            // left, so it is continuous across notches — no restart, no stalled frame. The
            // exp(-dt/τ) form makes it frame-rate independent: the same wall-clock curve
            // whether frames arrive every 8ms or every 40ms. 4.6 time constants ≈ 99%, so
            // SettleMs reads as "how long a notch takes to land".
            _currentY += (targetY - _currentY) * (1 - Math.Exp(-dt / (_settleMs / 4600.0)));

            if (Math.Abs(targetY - _currentY) < 0.5)
            {
                Apply(scrollViewer, targetY);
                Stop();
                return;
            }

            Apply(scrollViewer, _currentY);

            if (_currentY <= 0 || _currentY >= maxY)
            {
                Stop();
                return;
            }

            QueueNextFrame();
        }

        private void Apply(ScrollViewer scrollViewer, double y)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, y);
            // Read back: the ScrollViewer may coerce, and _appliedY is what the next
            // Push() compares against to detect an external offset change.
            _appliedY = scrollViewer.Offset.Y;
        }

        private void QueueNextFrame()
        {
            if (!_isRunning || _isFrameQueued)
                return;

            var scrollViewer = GetScrollViewer();
            var topLevel = scrollViewer == null ? null : TopLevel.GetTopLevel(scrollViewer);
            if (topLevel == null)
            {
                // No frame clock (detached mid-glide) — land on the target so the offset
                // never strands part-way.
                if (scrollViewer != null)
                {
                    var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                    _currentY = Math.Clamp(_targetY, 0, maxY);
                    Apply(scrollViewer, _currentY);
                }
                Stop();
                return;
            }

            _isFrameQueued = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }
    }
}
