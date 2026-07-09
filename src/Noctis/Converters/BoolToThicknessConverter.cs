using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// Converts a boolean to a Thickness margin.
/// Parameter format: "trueLeft,trueTop,trueRight,trueBottom;falseLeft,falseTop,falseRight,falseBottom"
/// </summary>
public class BoolToThicknessConverter : IValueConverter
{
    public static readonly BoolToThicknessConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string paramStr)
            return new Thickness(0);

        var parts = paramStr.Split(';');
        if (parts.Length != 2) return new Thickness(0);

        // Invariant parse: parameters are XAML literals like "8,0,8,0" — the
        // OS culture (comma-decimal locales) must not change how they read.
        var values = boolValue ? parts[0] : parts[1];
        var nums = values.Split(',');
        if (nums.Length == 4 &&
            double.TryParse(nums[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) &&
            double.TryParse(nums[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) &&
            double.TryParse(nums[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) &&
            double.TryParse(nums[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            return new Thickness(l, t, r, b);
        }

        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
