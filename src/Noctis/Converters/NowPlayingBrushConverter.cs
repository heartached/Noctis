using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Multi-value converter that highlights the currently playing track row.
/// Inputs: [0] = row Track.Id (Guid), [1] = the player's current track id (Guid?).
/// ConverterParameter = a resource key (e.g. "NowPlayingRowBackground" or
/// "AccentTextBrush"). When the ids match, returns that themed brush resolved
/// live from app resources; otherwise UnsetValue (no fill / default text).
/// Used to mark the now-playing row in flat track lists (Folders, Songs).
/// </summary>
public class NowPlayingBrushConverter : IMultiValueConverter
{
    /// <summary>True iff both values are the same non-empty Guid.</summary>
    public static bool IsMatch(object? a, object? b)
        => a is Guid ga && b is Guid gb && ga != Guid.Empty && ga == gb;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && IsMatch(values[0], values[1]) && parameter is string key &&
            Application.Current?.TryGetResource(key, null, out var res) == true && res is IBrush brush)
        {
            return brush;
        }

        return AvaloniaProperty.UnsetValue;
    }
}
