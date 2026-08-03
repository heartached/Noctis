using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Grid column width for a hideable proportional column: <c>*</c> when shown, zero when
/// hidden.
/// <para>
/// The Songs list's optional columns are normally <c>Auto</c> with a fixed-width child,
/// so hiding the child collapses the column for free. That trick doesn't work for the
/// Artist and Album columns — they're <c>*</c> so they flex with the window, and a star
/// column keeps its share of the space whether or not anything is drawn in it, leaving a
/// gap where the hidden column used to be.
/// </para>
/// </summary>
public class BoolToColumnWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new GridLength(1, GridUnitType.Star) : new GridLength(0, GridUnitType.Pixel);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
