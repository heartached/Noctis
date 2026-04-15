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

        var values = boolValue ? parts[0] : parts[1];
        var nums = values.Split(',');
        if (nums.Length == 4 &&
            double.TryParse(nums[0], out var l) &&
            double.TryParse(nums[1], out var t) &&
            double.TryParse(nums[2], out var r) &&
            double.TryParse(nums[3], out var b))
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
