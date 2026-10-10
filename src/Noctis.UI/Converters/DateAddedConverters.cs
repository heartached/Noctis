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

/// <summary>
/// A playlist row's added date: when the track joined THIS playlist, else its library DateAdded.
/// Values: [0] track id (Guid), [1] library DateAdded, [2] the playlist's id→date map,
/// [3] (NEW badge only) the Settings switch. Parameter "display" → the "Added" text; "new" →
/// whether the NEW badge shows (last 7 days, and the switch on).
/// </summary>
public sealed class PlaylistAddedConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[1] is not DateTime library) return parameter as string == "new" ? false : string.Empty;
        var added = values[0] is Guid id && values.Count > 2 && values[2] is IReadOnlyDictionary<Guid, DateTime> map
                    && map.TryGetValue(id, out var stamped)
            ? stamped
            : library;

        if (parameter as string == "new")
        {
            var enabled = values.Count < 4 || values[3] is not bool on || on;
            return enabled && (DateTime.UtcNow - added.ToUniversalTime()).TotalDays < 7;
        }
        var local = added.ToLocalTime();
        return local.Year == DateTime.Now.Year ? local.ToString("MMM d") : local.ToString("MMM yyyy");
    }
}

/// <summary>True when a DateAdded falls within the last 7 days (drives the NEW chip).</summary>
public sealed class RecentlyAddedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime added && (DateTime.UtcNow - added.ToUniversalTime()).TotalDays < 7;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
