using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Converts a boolean to a FontWeight.
/// Parameter format: "falseWeight;trueWeight" (e.g., "Normal;Bold")
/// </summary>
public class BoolToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue)
            return FontWeight.Normal;

        if (parameter is string paramStr)
        {
            var parts = paramStr.Split(';');
            if (parts.Length == 2)
            {
                var falseWeight = ParseWeight(parts[0]);
                var trueWeight = ParseWeight(parts[1]);
                return boolValue ? trueWeight : falseWeight;
            }
        }

        return boolValue ? FontWeight.Bold : FontWeight.Normal;
    }

    private static FontWeight ParseWeight(string name) => name.Trim() switch
    {
        "Thin" => FontWeight.Thin,
        "Light" => FontWeight.Light,
        "Normal" or "Regular" => FontWeight.Normal,
        "Medium" => FontWeight.Medium,
        "SemiBold" => FontWeight.SemiBold,
        "Bold" => FontWeight.Bold,
        "ExtraBold" => FontWeight.ExtraBold,
        "Black" => FontWeight.Black,
        _ => FontWeight.Normal
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
