using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Localization;

namespace Noctis.Mobile.Views;

/// <summary>
/// A bound value put into a UI string: <c>Converter={x:Static views:LocFormat.Text},
/// ConverterParameter=Mobile.Key</c> is <c>Loc.T(key, value)</c>; <see cref="Count"/> reads the
/// value as a count and picks the language's plural form (<see cref="Loc.Plural"/>).
/// The XAML StringFormat it replaces could only be English.
/// </summary>
public sealed class LocFormat : IValueConverter
{
    public static readonly LocFormat Text = new(false);
    public static readonly LocFormat Count = new(true);

    private readonly bool _plural;
    private LocFormat(bool plural) => _plural = plural;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string key) return value;
        if (_plural)
            return Loc.Plural(key, value is IConvertible c ? c.ToInt64(CultureInfo.InvariantCulture) : 0);
        return Loc.T(key, value ?? string.Empty);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
