using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
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
    private CancellationTokenSource? _flattenCts;
    private DateTime _animStart;
    private bool _initialized;

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

    protected override void OnDetachedFromLogicalTree(global::Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        StopAnimating();
        _flattenCts?.Cancel();
    }

    private void OnIsPlayingChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (!_initialized) return;

        _flattenCts?.Cancel();

        if (IsPlaying)
        {
            StartAnimating();
        }
        else
        {
            StopAnimating();
            _flattenCts = new CancellationTokenSource();
            _ = AnimateFlattenAsync(_flattenCts.Token);
        }
    }

    private void StartAnimating()
    {
        _animStart = DateTime.UtcNow;
        if (_animTimer == null)
        {
            _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _animTimer.Tick += OnAnimTick;
        }
        _animTimer.Start();
    }

    private void StopAnimating()
    {
        _animTimer?.Stop();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _animStart).TotalSeconds;
        SetBar(_bar1, t, 0);
        SetBar(_bar2, t, 1);
        SetBar(_bar3, t, 2);
        SetBar(_bar4, t, 3);
        SetBar(_bar5, t, 4);
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

    private async Task AnimateFlattenAsync(CancellationToken ct)
    {
        var bars = new[] { _bar1, _bar2, _bar3, _bar4, _bar5 };
        var easing = new CubicEaseOut();
        var tasks = new Task[bars.Length];
        for (int i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar == null) { tasks[i] = Task.CompletedTask; continue; }
            tasks[i] = RunHeightAnimation(bar, bar.Height, FlatHeight, easing, ct);
        }
        try { await Task.WhenAll(tasks); }
        catch (TaskCanceledException) { }
    }

    private static Task RunHeightAnimation(Rectangle bar, double from, double to, Easing easing, CancellationToken ct)
    {
        var animation = new Animation
        {
            Duration = FlattenDuration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(HeightProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(HeightProperty, to) } }
            }
        };
        return animation.RunAsync(bar, ct);
    }
}
