using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Converts a boolean mute state to a volume icon path.
/// true (muted) to muted icon, false to volume icon.
/// </summary>
public class BoolToMuteIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isMuted && isMuted)
            return "avares://Noctis/Assets/Icons/Volume%20ICON.png";

        return "avares://Noctis/Assets/Icons/Volume%20ICON.png";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
