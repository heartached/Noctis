using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Noctis.Controls;

/// <summary>
/// Animated 4-bar EQ visualizer used to indicate the currently playing track.
/// When IsAnimating is true, bars scale up and down on staggered loops.
/// When false, bars freeze at their current scale (matches Apple Music behavior).
/// </summary>
public class EqVisualizer : TemplatedControl
{
    public static readonly StyledProperty<bool> IsAnimatingProperty =
        AvaloniaProperty.Register<EqVisualizer, bool>(nameof(IsAnimating));

    public new bool IsAnimating
    {
        get => GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private readonly List<CancellationTokenSource> _animationCts = new();
    private Rectangle? _bar1, _bar2, _bar3, _bar4;

    private static readonly (string Name, int DurationMs, double StartScale)[] BarConfigs =
    {
        ("Bar1",  700, 0.40),
        ("Bar2",  950, 0.90),
        ("Bar3", 1100, 0.55),
        ("Bar4",  850, 0.75),
    };

    static EqVisualizer()
    {
        IsAnimatingProperty.Changed.AddClassHandler<EqVisualizer>((c, _) => c.OnIsAnimatingChanged());
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _bar1 = e.NameScope.Find<Rectangle>("Bar1");
        _bar2 = e.NameScope.Find<Rectangle>("Bar2");
        _bar3 = e.NameScope.Find<Rectangle>("Bar3");
        _bar4 = e.NameScope.Find<Rectangle>("Bar4");
        if (IsAnimating) StartAll();
    }

    private void OnIsAnimatingChanged()
    {
        if (IsAnimating) StartAll();
        else StopAll();
    }

    private void StartAll()
    {
        StopAll();
        var bars = new[] { _bar1, _bar2, _bar3, _bar4 };
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar?.RenderTransform is not ScaleTransform st) continue;
            var cts = new CancellationTokenSource();
            _animationCts.Add(cts);
            var (_, durationMs, _) = BarConfigs[i];
            _ = RunBarAnimation(st, durationMs, cts.Token);
        }
    }

    private void StopAll()
    {
        foreach (var cts in _animationCts) cts.Cancel();
        _animationCts.Clear();
    }

    private static async System.Threading.Tasks.Task RunBarAnimation(ScaleTransform st, int durationMs, CancellationToken ct)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new CubicEaseInOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(ScaleTransform.ScaleYProperty, 0.25d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(ScaleTransform.ScaleYProperty, 1.0d) }
                },
            }
        };

        try
        {
            await animation.RunAsync(st, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation freezes the bar at its last rendered ScaleY.
        }
    }
}
