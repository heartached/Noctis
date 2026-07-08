using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Builds the karaoke sweep as a text <b>Foreground</b> brush from
/// <c>[0]=Progress (0..1)</c> and <c>[1]=the lyrics foreground brush</c>: the word's
/// accent overlay is painted fully left of <c>progress</c> and transparent to its
/// right, with a small feathered edge.
///
/// Painted as Foreground rather than OpacityMask on purpose: PushOpacityMask makes the
/// compositor render through an intermediate layer, and the sibling held-note glow
/// (an Effect visual) got composited inside that layer — multiplied by the sweep
/// gradient and hard-clipped at the overlay's bounds, which drew a cut-off box around
/// emphasis words. A gradient foreground paints the same wipe with no layer at all.
/// </summary>
public class ProgressToSweepForegroundConverter : IMultiValueConverter
{
    private const double Feather = 0.06;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = values.Count > 0
            ? values[0] switch
            {
                double d => d,
                float f => f,
                _ => 0.0,
            }
            : 0.0;
        var fg = values.Count > 1 ? values[1] as IBrush : null;
        var color = (fg as ISolidColorBrush)?.Color ?? Colors.White;

        // Fully hidden / fully shown short-circuits (no sliver at 0, no dim edge at 1).
        if (raw <= 0) return Brushes.Transparent;
        if (raw >= 1) return fg ?? Brushes.White;

        // The reveal edge sits exactly at `raw` and travels 0→1 linearly, so with
        // contiguous word timings the sweep flows across word boundaries at constant
        // velocity. The feather WIDTH shrinks to zero at the boundaries: soft
        // mid-word, no bright sliver before the word starts, no pop on completion.
        var feather = Math.Min(Feather, Math.Min(raw, 1.0 - raw));
        var lo = raw - feather;
        var hi = raw + feather;

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(color, lo),
                // Same RGB at alpha 0 so the feather fades without darkening.
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), hi),
            },
        };
    }
}
