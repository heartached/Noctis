using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// MultiValueConverter that compares two Guid values for equality.
/// Pass ConverterParameter="negate" to invert the result.
/// Used to highlight the currently playing track in album detail view.
/// </summary>
public class GuidEqualsConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool result = false;

        if (values.Count >= 2 && values[0] is Guid id1)
        {
            if (values[1] is Guid id2)
                result = id1 == id2;
        }

        if (parameter is string s && s == "negate")
            result = !result;

        return result;
    }
}
