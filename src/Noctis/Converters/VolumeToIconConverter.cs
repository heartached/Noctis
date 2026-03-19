using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Converts volume level (0-100) to the appropriate speaker StreamGeometry icon.
/// Returns a Geometry for use with PathIcon.Data binding.
/// </summary>
public class VolumeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int volume)
            return TryGetIcon("SpeakerHighIcon");

        return volume switch
        {
            0 => TryGetIcon("SpeakerZeroIcon"),
            <= 33 => TryGetIcon("SpeakerLowIcon"),
            _ => TryGetIcon("SpeakerHighIcon")
        };
    }

    private static object? TryGetIcon(string key)
    {
        if (Application.Current?.TryGetResource(key, null, out var resource) == true)
            return resource;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
