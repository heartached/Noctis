using Avalonia.Data.Converters;
using Avalonia.Layout;
using System;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// Converts a boolean to HorizontalAlignment.
/// true = Left (expanded), false = Center (collapsed).
/// </summary>
public class BoolToHorizontalAlignmentConverter : IValueConverter
{
    public static readonly BoolToHorizontalAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? HorizontalAlignment.Left : HorizontalAlignment.Center;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
