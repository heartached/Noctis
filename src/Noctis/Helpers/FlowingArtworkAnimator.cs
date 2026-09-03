using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>
/// Drives one surface's flowing-artwork background: two drifting/rotating copies of
/// the pre-blurred cover over the static one, and a beat pulse that breathes the whole
/// backdrop and lifts a white glow. One instance per surface (lyrics page, lyrics
/// panel, mini player), all running the same <see cref="FlowingArtworkMotion"/> math.
///
/// Frame-clock driven via <c>TopLevel.RequestAnimationFrame</c> — a DispatcherTimer
/// beats against the compositor's ~16.7 ms frame interval and reads as judder (the
/// mini player's old 33 ms timer stepped 2-2-2-3 frames). Only transform/opacity
/// values are written, never layout, so a frame costs a handful of property sets.
/// The loop stops itself while the host is detached, hidden or disabled and resumes
/// when it can be seen again.
/// </summary>
public sealed class FlowingArtworkAnimator : IDisposable
{
    private readonly Control _host;
    private readonly Visual _backdrop;
    private readonly Visual _layer1;
    private readonly Visual _layer2;
    private readonly Visual _glow;
    private readonly Func<BeatContext> _context;
    private readonly BeatMeter _meter;

    private readonly ScaleTransform _backdropScale = new();
    private readonly LayerTransforms _t1 = new();
    private readonly LayerTransforms _t2 = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
    private double _pulse; // smoothed on-screen pulse
    private bool _enabled;
    private bool _running;
    private bool _disposed;
    private DispatcherTimer? _visibilityPoll;

    private sealed class LayerTransforms
    {
        public readonly ScaleTransform Scale = new();
        public readonly RotateTransform Rotate = new();
        public readonly TranslateTransform Translate = new();
        public readonly TransformGroup Group;

        public LayerTransforms()
        {
            Group = new TransformGroup();
            Group.Children.Add(Scale);
            Group.Children.Add(Rotate);
            Group.Children.Add(Translate);
        }
    }

    /// <param name="host">The control whose attachment/visibility gates the loop and whose TopLevel supplies frames.</param>
    /// <param name="backdrop">The panel holding every artwork copy; scaled about its centre on the beat.</param>
    /// <param name="layer1">First drifting artwork copy.</param>
    /// <param name="layer2">Second drifting artwork copy.</param>
    /// <param name="glow">Full-bleed white rectangle whose opacity lifts on the beat.</param>
    /// <param name="context">Sampled once per frame for the BPM-grid fallback.</param>
    /// <param name="meter">Beat source; null uses <see cref="BeatMeter.Shared"/>.</param>
    public FlowingArtworkAnimator(Control host, Visual backdrop, Visual layer1, Visual layer2, Visual glow,
        Func<BeatContext> context, BeatMeter? meter = null)
    {
        _host = host;
        _backdrop = backdrop;
        _layer1 = layer1;
        _layer2 = layer2;
        _glow = glow;
        _context = context;
        _meter = meter ?? BeatMeter.Shared;

        _backdrop.RenderTransformOrigin = RelativePoint.Center;
        _backdrop.RenderTransform = _backdropScale;
        _layer1.RenderTransformOrigin = RelativePoint.Center;
        _layer1.RenderTransform = _t1.Group;
        _layer2.RenderTransformOrigin = RelativePoint.Center;
        _layer2.RenderTransform = _t2.Group;
        _glow.Opacity = 0;
    }

    /// <summary>The smoothed pulse currently on screen (0..1) — diagnostics/tests.</summary>
    public double CurrentPulse => _pulse;

    /// <summary>True while a frame callback is pending.</summary>
    public bool IsRunning => _running;

    /// <summary>Turns the animation on or off. Off snaps the backdrop back to rest.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value) TryStart();
            else Rest();
        }
    }

    private void TryStart()
    {
        if (_disposed || !_enabled || _running) return;
        if (!_host.IsEffectivelyVisible || TopLevel.GetTopLevel(_host) is not { } topLevel)
        {
            // Nothing to draw on yet — poll gently until the host can be seen. Cheaper
            // than keeping the compositor's frame loop hot for an invisible surface.
            StartVisibilityPoll();
            return;
        }
        StopVisibilityPoll();
        _running = true;
        _lastFrameMs = _clock.Elapsed.TotalMilliseconds;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        if (!_running) return;
        if (_disposed || !_enabled)
        {
            _running = false;
            return;
        }
        if (!_host.IsEffectivelyVisible || TopLevel.GetTopLevel(_host) is not { } topLevel)
        {
            _running = false;
            StartVisibilityPoll();
            return;
        }

        var nowMs = _clock.Elapsed.TotalMilliseconds;
        var dtMs = Math.Clamp(nowMs - _lastFrameMs, 0, 100);
        _lastFrameMs = nowMs;

        var target = BeatPulseSource.Evaluate(_meter, _meter.NowMs, _context());
        _pulse = FlowingArtworkMotion.Smooth(_pulse, target, dtMs);

        var size = _backdrop.Bounds.Size;
        if (size.Width > 0 && size.Height > 0)
            Apply(FlowingArtworkMotion.Evaluate(nowMs / 1000.0, size.Width, size.Height, _pulse));

        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void Apply(FlowFrame frame)
    {
        _backdropScale.ScaleX = frame.BackdropScale;
        _backdropScale.ScaleY = frame.BackdropScale;
        _glow.Opacity = frame.GlowOpacity;
        ApplyLayer(_t1, _layer1, frame.Layer1);
        ApplyLayer(_t2, _layer2, frame.Layer2);
    }

    private static void ApplyLayer(LayerTransforms t, Visual layer, FlowLayerPose pose)
    {
        t.Scale.ScaleX = pose.Scale;
        t.Scale.ScaleY = pose.Scale;
        t.Rotate.Angle = pose.AngleDeg;
        t.Translate.X = pose.X;
        t.Translate.Y = pose.Y;
        layer.Opacity = pose.Opacity;
    }

    private void Rest()
    {
        _running = false;
        StopVisibilityPoll();
        _pulse = 0;
        _backdropScale.ScaleX = 1;
        _backdropScale.ScaleY = 1;
        _glow.Opacity = 0;
    }

    private void StartVisibilityPoll()
    {
        if (_visibilityPoll != null) return;
        _visibilityPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _visibilityPoll.Tick += (_, _) =>
        {
            if (_disposed || !_enabled) { StopVisibilityPoll(); return; }
            if (_host.IsEffectivelyVisible && TopLevel.GetTopLevel(_host) != null)
                TryStart();
        };
        _visibilityPoll.Start();
    }

    private void StopVisibilityPoll()
    {
        if (_visibilityPoll == null) return;
        _visibilityPoll.Stop();
        _visibilityPoll = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _enabled = false;
        Rest();
    }
}
