using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// MultiBinding converter: returns true when the row track id (values[0])
/// equals the current track id (values[1]). Returns false if either is null.
/// </summary>
public sealed class TrackIsCurrentConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;
        var rowId = values[0] as string;
        var currentId = values[1] as string;
        if (string.IsNullOrEmpty(rowId) || string.IsNullOrEmpty(currentId)) return false;
        return string.Equals(rowId, currentId, StringComparison.Ordinal);
    }
}
