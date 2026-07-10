using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;

namespace Noctis.Controls;

/// <summary>
/// Compact row EQ indicator for the currently playing track.
/// Five bars oscillate while playing and ease to flat on pause (Apple Music feel).
/// </summary>
public class EqVisualizer : TemplatedControl
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<EqVisualizer, bool>(nameof(IsPlaying));

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    private Rectangle? _bar1, _bar2, _bar3, _bar4, _bar5;
    private DispatcherTimer? _animTimer;
    private DateTime _animStart;
    private bool _initialized;

    // Pause-flatten runs on the same render timer with plain local Height sets.
    // Animation.RunAsync with FillMode.Forward would pin Height at animation
    // priority once finished, masking every later local set (frozen bars on
    // the next play).
    private bool _flattening;
    private DateTime _flattenStart;
    private readonly double[] _flattenFrom = new double[5];
    private static readonly Easing FlattenEasing = new CubicEaseOut();

    private const double FlatHeight = 1.75;
    private const double BarMin = 2.25;
    private const double BarMax = 10.0;
    // Phase offsets give each bar its own rhythm (radians).
    private static readonly double[] Phases = { 0.0, 1.2, 2.4, 0.8, 1.8 };
    // Slightly different frequencies per bar for an organic feel (Hz).
    private static readonly double[] Frequencies = { 1.6, 2.0, 1.4, 1.8, 1.7 };
    private static readonly TimeSpan FlattenDuration = TimeSpan.FromMilliseconds(420);

    static EqVisualizer()
    {
        IsPlayingProperty.Changed.AddClassHandler<EqVisualizer>((c, e) => c.OnIsPlayingChanged(e));
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _bar1 = e.NameScope.Find<Rectangle>("Bar1");
        _bar2 = e.NameScope.Find<Rectangle>("Bar2");
        _bar3 = e.NameScope.Find<Rectangle>("Bar3");
        _bar4 = e.NameScope.Find<Rectangle>("Bar4");
        _bar5 = e.NameScope.Find<Rectangle>("Bar5");

        SetAllBars(FlatHeight);
        _initialized = true;

        if (IsPlaying)
            StartAnimating();
    }

    protected override void OnAttachedToLogicalTree(global::Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // Recycled rows re-attach without a template re-apply or an IsPlaying
        // change; restart the oscillation or the bars come back frozen.
        if (_initialized && IsPlaying)
            StartAnimating();
    }

    protected override void OnDetachedFromLogicalTree(global::Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        _flattening = false;
        StopAnimating();
    }

    private void OnIsPlayingChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (!_initialized) return;

        if (IsPlaying)
        {
            _flattening = false;
            StartAnimating();
        }
        else
        {
            BeginFlatten();
        }
    }

    private void StartAnimating()
    {
        _animStart = DateTime.UtcNow;
        EnsureTimer().Start();
    }

    private void StopAnimating()
    {
        _animTimer?.Stop();
    }

    private DispatcherTimer EnsureTimer()
    {
        if (_animTimer == null)
        {
            _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _animTimer.Tick += OnAnimTick;
        }
        return _animTimer;
    }

    private void BeginFlatten()
    {
        _flattenFrom[0] = _bar1?.Height ?? FlatHeight;
        _flattenFrom[1] = _bar2?.Height ?? FlatHeight;
        _flattenFrom[2] = _bar3?.Height ?? FlatHeight;
        _flattenFrom[3] = _bar4?.Height ?? FlatHeight;
        _flattenFrom[4] = _bar5?.Height ?? FlatHeight;
        _flattenStart = DateTime.UtcNow;
        _flattening = true;
        EnsureTimer().Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_flattening)
        {
            var progress = (DateTime.UtcNow - _flattenStart).TotalMilliseconds / FlattenDuration.TotalMilliseconds;
            if (progress >= 1)
            {
                SetAllBars(FlatHeight);
                _flattening = false;
                StopAnimating();
                return;
            }

            var eased = FlattenEasing.Ease(progress);
            SetBarLerp(_bar1, _flattenFrom[0], eased);
            SetBarLerp(_bar2, _flattenFrom[1], eased);
            SetBarLerp(_bar3, _flattenFrom[2], eased);
            SetBarLerp(_bar4, _flattenFrom[3], eased);
            SetBarLerp(_bar5, _flattenFrom[4], eased);
            return;
        }

        var t = (DateTime.UtcNow - _animStart).TotalSeconds;
        SetBar(_bar1, t, 0);
        SetBar(_bar2, t, 1);
        SetBar(_bar3, t, 2);
        SetBar(_bar4, t, 3);
        SetBar(_bar5, t, 4);
    }

    private static void SetBarLerp(Rectangle? bar, double from, double eased)
    {
        if (bar == null) return;
        bar.Height = from + (FlatHeight - from) * eased;
    }

    private static void SetBar(Rectangle? bar, double t, int idx)
    {
        if (bar == null) return;
        var s = Math.Sin(2 * Math.PI * Frequencies[idx] * t + Phases[idx]);
        // Map sin in [-1,1] to [MinHeight, MaxHeight].
        var h = BarMin + (BarMax - BarMin) * (s * 0.5 + 0.5);
        bar.Height = h;
    }

    private void SetAllBars(double h)
    {
        if (_bar1 != null) _bar1.Height = h;
        if (_bar2 != null) _bar2.Height = h;
        if (_bar3 != null) _bar3.Height = h;
        if (_bar4 != null) _bar4.Height = h;
        if (_bar5 != null) _bar5.Height = h;
    }
}
