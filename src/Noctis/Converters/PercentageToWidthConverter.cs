using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Converts a percentage (0.0-1.0) to a width value for bar charts.
/// ConverterParameter specifies the maximum width (e.g., "400").
/// </summary>
public class PercentageToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double percentage && parameter is string maxWidthStr
            && double.TryParse(maxWidthStr, CultureInfo.InvariantCulture, out var maxWidth))
        {
            return Math.Max(0, percentage * maxWidth);
        }
        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
