using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Formats a track's DateAdded for the playlist "Added" column:
/// "Jul 16" within the current year, "Jul 2025" otherwise.
/// </summary>
public sealed class DateAddedDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime added) return string.Empty;
        var local = added.ToLocalTime();
        return local.Year == DateTime.Now.Year ? local.ToString("MMM d") : local.ToString("MMM yyyy");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when a DateAdded falls within the last 7 days (drives the NEW chip).</summary>
public sealed class RecentlyAddedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime added && (DateTime.UtcNow - added.ToUniversalTime()).TotalDays < 7;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
