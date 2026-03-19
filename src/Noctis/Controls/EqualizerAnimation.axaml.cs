using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Noctis.Controls;

public partial class EqualizerAnimation : UserControl
{
    /// <summary>
    /// Controls whether the bars are animating (true = playing) or frozen (false = paused).
    /// </summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<EqualizerAnimation, bool>(nameof(IsActive), true);

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private Border[] _bars = Array.Empty<Border>();
    private readonly DispatcherTimer _timer;
    private double _elapsed;

    // Each bar uses a sum of two sine waves at different frequencies for organic movement
    private static readonly double[] Freq1 = { 2.1, 1.7, 2.8, 1.4, 2.4 };
    private static readonly double[] Freq2 = { 3.3, 2.5, 1.9, 3.1, 2.0 };
    private static readonly double[] Phase = { 0.0, 1.1, 0.4, 2.3, 1.6 };

    private const double BarMinHeight = 2.0;
    private const double BarMaxHeight = 14.0;

    public EqualizerAnimation()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += OnTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5 };
        if (IsActive && IsVisible)
            _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty)
        {
            if (IsActive && IsVisible && VisualRoot != null)
                _timer.Start();
            else
                _timer.Stop();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _elapsed += 0.04;
        for (int i = 0; i < _bars.Length; i++)
        {
            // Combine two sine waves for more organic movement
            var wave = (Math.Sin(_elapsed * Freq1[i] * Math.PI * 2 + Phase[i])
                      + Math.Sin(_elapsed * Freq2[i] * Math.PI * 2 + Phase[i] * 1.7)) / 2.0;
            // Normalize from [-1,1] to [BarMinHeight, BarMaxHeight]
            _bars[i].Height = BarMinHeight + (wave + 1.0) / 2.0 * (BarMaxHeight - BarMinHeight);
        }
    }
}
