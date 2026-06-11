using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// Renders waveform peaks as vertical bars with the played portion tinted.
/// Purely visual — hit testing/seeking is handled by the transparent Slider
/// that the playback bar overlays on top, same as the plain seek track.
/// </summary>
public class WaveformSeekBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> PeaksProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IReadOnlyList<float>?>(nameof(Peaks));

    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(PlayedBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> UnplayedBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(UnplayedBrush),
            new SolidColorBrush(Color.Parse("#40FFFFFF")));

    static WaveformSeekBar()
    {
        AffectsRender<WaveformSeekBar>(PeaksProperty, FractionProperty, PlayedBrushProperty, UnplayedBrushProperty);
    }

    public IReadOnlyList<float>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush? UnplayedBrush
    {
        get => GetValue(UnplayedBrushProperty);
        set => SetValue(UnplayedBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var peaks = Peaks;
        var bounds = Bounds;
        if (peaks == null || peaks.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var played = PlayedBrush ?? Brushes.White;
        var unplayed = UnplayedBrush ?? Brushes.Gray;
        double fraction = Math.Clamp(Fraction, 0, 1);
        double playedX = bounds.Width * fraction;

        int count = peaks.Count;
        double slot = bounds.Width / count;
        double barWidth = Math.Max(1.0, slot * 0.66);
        double midY = bounds.Height / 2.0;
        double minBar = 1.5;

        for (int i = 0; i < count; i++)
        {
            double x = i * slot + (slot - barWidth) / 2.0;
            double h = Math.Max(minBar, peaks[i] * bounds.Height);
            var rect = new Rect(x, midY - h / 2.0, barWidth, h);
            var radius = barWidth / 2.0;

            // A bar counts as played once its center passes the playhead.
            var brush = x + barWidth / 2.0 <= playedX ? played : unplayed;
            context.DrawRectangle(brush, null, new RoundedRect(rect, radius));
        }
    }
}
