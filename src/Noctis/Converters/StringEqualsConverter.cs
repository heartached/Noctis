using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Returns true when the bound string equals the string passed as ConverterParameter,
/// ignoring case. Sibling of <see cref="IntEqualsConverter"/>; used to check the active
/// entry in sort menus, where one bound field decides which of a dozen items is ticked.
/// </summary>
public class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string v && parameter is string p && string.Equals(v, p, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
