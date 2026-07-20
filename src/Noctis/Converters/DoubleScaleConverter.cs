using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Multiplies a double by the converter parameter. Used to derive the background-vocal
/// row's font size from the inherited lyrics font size, so it tracks the responsive
/// size set from code-behind instead of a hardcoded value.
/// </summary>
public class DoubleScaleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var input = value switch
        {
            double d => d,
            float f => (double)f,
            int i => i,
            _ => 0.0,
        };
        var factor = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? p
            : 1.0;
        return input * factor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
