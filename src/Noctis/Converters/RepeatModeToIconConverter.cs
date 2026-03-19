using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Converts RepeatMode enum to icon path or text representation.
/// </summary>
public class RepeatModeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RepeatMode mode)
        {
            return mode switch
            {
                RepeatMode.Off => "avares://Noctis/Assets/Icons/Rrepeat%20ICON.png",
                RepeatMode.All => "avares://Noctis/Assets/Icons/Rrepeat%20ICON.png",
                RepeatMode.One => "avares://Noctis/Assets/Icons/Rrepeat%20ICON.png",
                _ => "avares://Noctis/Assets/Icons/Rrepeat%20ICON.png"
            };
        }
        return "avares://Noctis/Assets/Icons/Rrepeat%20ICON.png";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
