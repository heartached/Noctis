using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Builds a left-to-right opacity-mask brush from a [0..1] progress value.
/// Used to drive the AMLL-style colour sweep across a karaoke word: the accent overlay
/// is masked so it is fully revealed left of <c>progress</c> and hidden to its right,
/// with a small feathered edge to avoid a hard step.
/// </summary>
public class ProgressToSweepMaskConverter : IValueConverter
{
    public static readonly ProgressToSweepMaskConverter Instance = new();

    private const double Feather = 0.06;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = value switch
        {
            double d => d,
            float f => f,
            _ => 0.0,
        };
        if (raw < 0) raw = 0;
        else if (raw > 1) raw = 1;

        // Mild ease-out shape — slight lead at the start, smooth tail. Stronger curves
        // make the sweep visibly accelerate then decelerate, which reads as stuttering.
        var progress = 1.0 - Math.Pow(1.0 - raw, 1.25);

        var lo = Math.Max(0.0, progress - Feather);
        var hi = Math.Min(1.0, progress + Feather);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), lo),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), hi),
            },
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
