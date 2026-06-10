using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Builds the quick-rate command parameter for track context menus:
/// wraps the bound <see cref="Track"/> with the star count given in ConverterParameter.
/// </summary>
public sealed class TrackRatingParameterConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Track track)
            return null;

        return int.TryParse(parameter?.ToString(), out var rating)
            ? new TrackRatingParameter(track, rating)
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
