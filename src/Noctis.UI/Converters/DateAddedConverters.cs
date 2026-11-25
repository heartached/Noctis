using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// A playlist row's added date: when the track joined THIS playlist, else its library DateAdded.
/// Values: [0] track id (Guid), [1] library DateAdded, [2] the playlist's id→date map,
/// [3] (NEW badge only) the Settings switch, [4] (optional) how many days a track stays NEW
/// (Settings › Playlist page; 7 when absent). Parameter "display" → the "Added" text; "new" →
/// whether the NEW badge shows (within those days, and the switch on).
/// </summary>
public sealed class PlaylistAddedConverter : IMultiValueConverter
{
    /// <summary>The NEW badge's length when no setting reaches the converter.</summary>
    public const int DefaultNewDays = 7;

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
            var days = values.Count > 4 && values[4] is int d && d > 0 ? d : DefaultNewDays;
            return enabled && (DateTime.UtcNow - added.ToUniversalTime()).TotalDays < days;
        }
        var local = added.ToLocalTime();
        return local.Year == DateTime.Now.Year ? local.ToString("MMM d") : local.ToString("MMM yyyy");
    }
}
