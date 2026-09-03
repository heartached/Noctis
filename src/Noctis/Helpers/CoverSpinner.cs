using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Turns any visual like a disc: the same coasting <see cref="SpinClock"/> the Now
/// Playing costumes use (spin-up when play starts, coast to a stop on pause), driven
/// from the host TopLevel's frame clock and applied as a centre rotation. Used for the
/// Pill mini player design, whose round cover has no CD hub to justify a MediaArtwork.
/// </summary>
public sealed class CoverSpinner
{
    private readonly Control _host;
    private readonly Visual _target;
    private readonly RotateTransform _rotate = new();
    private readonly SpinClock _clock = new();
    private long _lastTimestamp;
    private bool _frameQueued;

    public CoverSpinner(Control host, Visual target)
    {
        _host = host;
        _target = target;
        _target.RenderTransformOrigin = RelativePoint.Center;
        _target.RenderTransform = _rotate;
    }

    /// <summary>Drive (true) or let the disc coast to a stop (false).</summary>
    public bool IsSpinning
    {
        get => _clock.IsRunning;
        set
        {
            if (_clock.IsRunning == value) return;
            _clock.IsRunning = value;
            QueueFrame();
        }
    }

    /// <summary>Current angle in degrees (tests/diagnostics).</summary>
    public double Angle => _clock.Angle;

    private void QueueFrame()
    {
        if (_frameQueued || _clock.IsSettled) return;
        if (TopLevel.GetTopLevel(_host) is not { } topLevel) return;
        _lastTimestamp = Stopwatch.GetTimestamp();
        _frameQueued = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameQueued = false;
        var now = Stopwatch.GetTimestamp();
        // Clamped so a long stall (hidden window, sleep) doesn't whip the cover round.
        var elapsed = Math.Min((now - _lastTimestamp) / (double)Stopwatch.Frequency, 0.1);
        _lastTimestamp = now;

        _clock.Advance(elapsed);
        _rotate.Angle = _clock.Angle;

        if (_clock.IsSettled) return;
        if (TopLevel.GetTopLevel(_host) is { } topLevel)
        {
            _frameQueued = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }
    }
}
