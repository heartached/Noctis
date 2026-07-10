using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// Clamps an opacity to a minimum floor. The lyrics panel uses it to keep
/// distant lines readable: the shared per-line opacity falloff is tuned for
/// the lyrics page's large type (where off-screen lines hit 0), but the
/// panel's smaller type fits many more lines in view.
/// Parameter: the floor (e.g., "0.3"). Zero stays zero only if no floor given.
/// </summary>
public class OpacityFloorConverter : IValueConverter
{
    public static readonly OpacityFloorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double opacity)
            return 1.0;

        // Invariant parse: the parameter is a XAML literal like "0.3" — the
        // OS culture (comma-decimal locales) must not change how it reads.
        if (parameter is string paramStr &&
            double.TryParse(paramStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var floor))
        {
            return Math.Max(opacity, floor);
        }

        return opacity;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
