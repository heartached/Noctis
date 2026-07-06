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
        // Fully hidden / fully shown short-circuits (no sliver at 0, no dim edge at 1).
        if (raw <= 0) return Brushes.Transparent;
        if (raw >= 1) return Brushes.White;

        // The reveal edge sits exactly at `raw` and travels 0→1 linearly, so with
        // contiguous word timings the sweep flows across word boundaries at constant
        // velocity. (An earlier version drove the edge across an extended -F..1+F
        // range to hide the feather at the ends — that parked the edge off-glyph for
        // ~10% of every word's duration and read as a stall at each word boundary.)
        // Instead, the feather WIDTH shrinks to zero at the boundaries: soft mid-word,
        // no bright sliver before the word starts, no pop when it completes.
        var feather = Math.Min(Feather, Math.Min(raw, 1.0 - raw));
        var lo = raw - feather;
        var hi = raw + feather;

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
