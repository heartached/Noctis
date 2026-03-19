using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Converts shuffle boolean to icon path.
/// </summary>
public class BoolToShuffleIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isShuffled && isShuffled)
            return "avares://Noctis/Assets/Icons/Shuffle%20ICON.png";

        return "avares://Noctis/Assets/Icons/Shuffle%20ICON.png";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
