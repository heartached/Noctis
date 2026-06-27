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
/// ConverterParameter = a resource key, optionally "matchKey|elseKey". On a
/// match returns the matchKey brush (resolved live from app resources for the
/// active theme); otherwise the elseKey brush if one is given, else UnsetValue.
/// An elseKey is required for the inherited TextElement.Foreground: its unset
/// default is black, which otherwise leaks onto recycled (virtualized) rows.
/// Used to mark the now-playing row in flat track lists (Folders, Songs).
/// </summary>
public class NowPlayingBrushConverter : IMultiValueConverter
{
    /// <summary>True iff both values are the same non-empty Guid.</summary>
    public static bool IsMatch(object? a, object? b)
        => a is Guid ga && b is Guid gb && ga != Guid.Empty && ga == gb;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool match = values.Count >= 2 && IsMatch(values[0], values[1]);

        // parameter is "matchKey" or "matchKey|elseKey".
        var keys = (parameter as string)?.Split('|');
        string? key = keys is { Length: > 0 }
            ? (match ? keys[0] : keys.Length > 1 ? keys[1] : null)
            : null;

        if (!string.IsNullOrEmpty(key) &&
            Application.Current?.TryGetResource(key, null, out var res) == true && res is IBrush brush)
        {
            return brush;
        }

        return AvaloniaProperty.UnsetValue;
    }
}
