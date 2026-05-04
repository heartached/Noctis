using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Noctis.Helpers;

public sealed class SmoothScrollAnimator
{
    private readonly Func<ScrollViewer?> _getScrollViewer;
    private readonly Stopwatch _stopwatch = new();
    private double _fromY;
    private double _toY;
    private double _durationMs;
    private double _velocityY;
    private bool _isMomentumScroll;
    private bool _isRunning;
    private bool _isFrameQueued;

    private const double MomentumDecay = 0.95;
    private const double MomentumStopVelocity = 0.25;

    public SmoothScrollAnimator(Func<ScrollViewer?> getScrollViewer)
    {
        _getScrollViewer = getScrollViewer;
    }

    public bool IsRunning => _isRunning;
    public double TargetY => _toY;

    public void Start(double fromY, double toY, int durationMs)
    {
        Stop();

        _isMomentumScroll = false;
        _fromY = fromY;
        _toY = toY;
        _durationMs = Math.Max(1, durationMs);
        _stopwatch.Restart();
        EnsureTimer();
    }

    public void AddMomentum(double velocityY)
    {
        var scrollViewer = _getScrollViewer();
        if (scrollViewer == null)
            return;

        _isMomentumScroll = true;
        _velocityY += velocityY;
        _toY = scrollViewer.Offset.Y;
        _stopwatch.Reset();
        EnsureTimer();
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _stopwatch.Stop();
        _velocityY = 0;
        _isMomentumScroll = false;
    }

    private void Render()
    {
        _isFrameQueued = false;
        if (!_isRunning)
            return;

        var scrollViewer = _getScrollViewer();
        if (scrollViewer == null)
        {
            Stop();
            return;
        }

        if (_isMomentumScroll)
        {
            TickMomentum(scrollViewer);
            QueueNextFrame();
            return;
        }

        var t = Math.Min(1.0, _stopwatch.Elapsed.TotalMilliseconds / _durationMs);
        var eased = 1.0 - Math.Pow(1.0 - t, 3.0);
        var value = _fromY + (_toY - _fromY) * eased;

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, value);

        if (t < 1.0)
        {
            QueueNextFrame();
            return;
        }

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, _toY);
        Stop();
    }

    private void TickMomentum(ScrollViewer scrollViewer)
    {
        if (Math.Abs(_velocityY) < MomentumStopVelocity)
        {
            Stop();
            return;
        }

        var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextY = Math.Clamp(scrollViewer.Offset.Y + _velocityY, 0, maxY);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextY);
        _toY = nextY;

        if (nextY <= 0 || nextY >= maxY)
        {
            Stop();
            return;
        }

        _velocityY *= MomentumDecay;
    }

    private void EnsureTimer()
    {
        _isRunning = true;
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
