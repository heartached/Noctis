using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Builds the Add-to-playlist command parameter for track context menus.
/// </summary>
public sealed class TrackPlaylistCommandParameterConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var track = values.Count > 0 ? values[0] as Track : null;
        var playlist = values.Count > 1 ? values[1] as Playlist : null;
        var usePlaylistOnly = values.Count > 2 && values[2] is bool flag && flag;

        if (playlist == null)
            return null;

        if (usePlaylistOnly)
            return playlist;

        return new object[] { track!, playlist };
    }
}
