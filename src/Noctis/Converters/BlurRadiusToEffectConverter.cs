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
    // Cached by quantised radius. Unlike the sweep-gradient converter, a BlurEffect here
    // carries no per-line state — two lines at the same radius are interchangeable — so
    // one shared instance per radius is safe even though the converter itself is an
    // x:Key singleton. Scroll and track change re-evaluate this for every realized line,
    // and it allocated a fresh effect each time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, BlurEffect> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value switch
        {
            double d => d,
            float f => f,
            _ => 0.0,
        };
        if (radius <= 0) return null;

        // Tenths are far finer than the eye resolves on a blur, and bound the cache.
        var key = (int)Math.Round(radius * 10);
        return _cache.GetOrAdd(key, k => new BlurEffect { Radius = k / 10.0 });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
