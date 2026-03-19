using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// Converts a hex color string (e.g., "#FF5733") to a SolidColorBrush.
/// </summary>
public class HexColorToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hexColor && !string.IsNullOrWhiteSpace(hexColor))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(hexColor));
            }
            catch
            {
                // Return default gray color if parsing fails
                return new SolidColorBrush(Color.Parse("#808080"));
            }
        }

        return new SolidColorBrush(Color.Parse("#808080"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush brush)
        {
            return brush.Color.ToString();
        }

        return "#808080";
    }
}
