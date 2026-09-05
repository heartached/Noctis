using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// Compact row EQ indicator for the currently playing track.
/// Five bars keep the old free-running oscillation (so they always move) and bounce
/// together on every beat of what is being heard (<see cref="BeatMeter"/>, the same
/// latency-aligned pulse that breathes the lyrics background), with a little per-bar
/// tonal colour from the live spectrum (bass left, treble right). Raw spectrum levels
/// alone read as stuck: five bands three octaves wide are always loud, so the bars sat
/// pinned near the top. Where no sample tap is flowing (engines without one, or the first
/// frames of a track) the oscillation alone runs, as before. Eases to flat on pause.
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

    // Live state: one spectrum band per bar for tonal colour, the beat pulse for the
    // bounce; smoothed per bar with a fast attack and a release short enough to fall
    // between beats.
    private readonly float[] _bands = new float[5];
    private readonly float[] _liveTarget = new float[5];
    private readonly float[] _liveShown = new float[5];
    private DateTime _lastTick;

    private const double LiveAttackMs = 25;
    private const double LiveReleaseMs = 120;
    // Level = Rest + Sway·osc + Bounce·pulse + Tone·(band − mean band). Sway keeps the
    // idle motion, Bounce lifts every bar on the beat, Tone makes bars differ by content.
    private const double LiveRest = 0.12;
    private const double LiveSway = 0.28;
    private const double LiveBounce = 0.60;
    private const double LiveTone = 0.60;

    static EqVisualizer()
    {
        IsPlayingProperty.Changed.AddClassHandler<EqVisualizer>((c, e) => c.OnIsPlayingChanged(e));
        // Rows bind IsPlaying to the GLOBAL play state and flip only this
        // control's IsVisible per row, so without this a hidden instance keeps
        // its 16ms timer running until the page is left.
        IsVisibleProperty.Changed.AddClassHandler<EqVisualizer>((c, e) => c.OnIsVisibleChanged());
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

        if (IsPlaying && IsVisible)
            StartAnimating();
    }

    protected override void OnAttachedToLogicalTree(global::Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // Recycled rows re-attach without a template re-apply or an IsPlaying
        // change; restart the oscillation or the bars come back frozen.
        if (_initialized && IsPlaying && IsVisible)
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
            if (IsVisible)
                StartAnimating();
        }
        else if (IsVisible)
        {
            BeginFlatten();
        }
        else
        {
            // Hidden: skip the animated flatten, just land flat with no timer.
            _flattening = false;
            StopAnimating();
            SetAllBars(FlatHeight);
        }
    }

    private void OnIsVisibleChanged()
    {
        if (!_initialized) return;

        if (IsVisible)
        {
            if (IsPlaying)
            {
                _flattening = false;
                StartAnimating();
            }
        }
        else
        {
            // Park hidden instances: finish any flatten instantly and stop the
            // timer; the visible/attach paths above restart the oscillation.
            _flattening = false;
            StopAnimating();
            SetAllBars(FlatHeight);
        }
    }

    private void StartAnimating()
    {
        _animStart = DateTime.UtcNow;
        _lastTick = _animStart;
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

        var now = DateTime.UtcNow;
        var dtMs = Math.Clamp((now - _lastTick).TotalMilliseconds, 0, 100);
        _lastTick = now;

        var t = (now - _animStart).TotalSeconds;
        var beat = BeatMeter.Shared;
        if (beat.TryRead(beat.NowMs, out var pulse))
        {
            var spectrum = SpectrumMeter.Shared;
            if (!spectrum.TryRead(spectrum.NowMs, _bands)) Array.Clear(_bands);
            LiveLevels(t, pulse, _bands, _liveTarget);
            SpectrumVisualizer.Smooth(_liveShown, _liveTarget, dtMs, LiveAttackMs, LiveReleaseMs);
            SetBarLevel(_bar1, _liveShown[0]);
            SetBarLevel(_bar2, _liveShown[1]);
            SetBarLevel(_bar3, _liveShown[2]);
            SetBarLevel(_bar4, _liveShown[3]);
            SetBarLevel(_bar5, _liveShown[4]);
            return;
        }

        Array.Clear(_liveShown);
        SetBar(_bar1, t, 0);
        SetBar(_bar2, t, 1);
        SetBar(_bar3, t, 2);
        SetBar(_bar4, t, 3);
        SetBar(_bar5, t, 4);
    }

    /// <summary>Bar height for a 0..1 level — the same range the oscillation uses.</summary>
    public static double HeightForLevel(double level)
        => BarMin + (BarMax - BarMin) * Math.Clamp(level, 0, 1);

    /// <summary>
    /// Target 0..1 levels for the five bars at time <paramref name="t"/> from the beat
    /// <paramref name="pulse"/> (0..1) and five spectrum bands (0..1, bass→treble).
    /// Pure; exposed for tests.
    /// </summary>
    public static void LiveLevels(double t, double pulse, ReadOnlySpan<float> bands, Span<float> levels)
    {
        double mean = 0;
        for (var i = 0; i < 5; i++) mean += bands[i];
        mean /= 5;
        for (var i = 0; i < 5; i++)
        {
            var osc = Math.Sin(2 * Math.PI * Frequencies[i] * t + Phases[i]) * 0.5 + 0.5;
            var level = LiveRest + LiveSway * osc + LiveBounce * pulse + LiveTone * (bands[i] - mean);
            levels[i] = (float)Math.Clamp(level, 0, 1);
        }
    }

    private static void SetBarLevel(Rectangle? bar, float level)
    {
        if (bar == null) return;
        bar.Height = HeightForLevel(level);
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
