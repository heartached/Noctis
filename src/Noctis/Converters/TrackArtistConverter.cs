using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Converts Track to its Artist name for display.
/// Returns empty string if track is null or artist is not set.
/// </summary>
public class TrackArtistConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Track track) return string.Empty;

        return string.IsNullOrWhiteSpace(track.Artist) ? string.Empty : track.Artist;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
