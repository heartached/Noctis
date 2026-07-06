using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Maps a lyric line's blur radius to its Button.Effect, returning null at radius 0
/// instead of a zero-radius BlurEffect. Any effect — even a no-op blur — forces the
/// line to rasterize into an intermediate surface clipped to its layout bounds, which
/// cut off the active line's word swell and held-note glow at the line edge
/// (the "invisible wall" artifact).
/// </summary>
public class BlurRadiusToEffectConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value switch
        {
            double d => d,
            float f => f,
            _ => 0.0,
        };
        return radius <= 0 ? null : new BlurEffect { Radius = radius };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
