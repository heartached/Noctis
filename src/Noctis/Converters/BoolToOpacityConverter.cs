using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// Converts a boolean value to an opacity value.
/// Parameter format: "falseOpacity;trueOpacity" (e.g., "0.4;1.0")
/// </summary>
public class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue)
            return 1.0;

        // Parse parameter "falseOpacity;trueOpacity"
        if (parameter is string paramStr)
        {
            var parts = paramStr.Split(';');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var falseOpacity) &&
                double.TryParse(parts[1], out var trueOpacity))
            {
                return boolValue ? trueOpacity : falseOpacity;
            }
        }

        // Default: 0.4 when false, 1.0 when true
        return boolValue ? 1.0 : 0.4;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
