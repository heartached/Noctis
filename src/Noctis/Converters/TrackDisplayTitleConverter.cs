using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Converts Track to its display title (always the track title, never the album name).
/// </summary>
public class TrackDisplayTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Track track) return string.Empty;

        return string.IsNullOrWhiteSpace(track.Title) ? "Unknown" : track.Title;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
