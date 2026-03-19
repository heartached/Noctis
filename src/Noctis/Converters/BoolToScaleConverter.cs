using Avalonia.Data.Converters;
using Avalonia.Media.Transformation;
using System;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// Converts a boolean to a scale transform for Apple Music-style lyrics scaling.
/// Active line scales up slightly, inactive stays at 1.0.
/// </summary>
public class BoolToScaleConverter : IValueConverter
{
    public static readonly BoolToScaleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool isActive)
            return TransformOperations.Parse("scale(1.0)");

        return isActive
            ? TransformOperations.Parse("scale(1.03)")
            : TransformOperations.Parse("scale(1.0)");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
