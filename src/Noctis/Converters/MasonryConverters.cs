using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// GitHub #259: an album grid that Masonry replaces gets no items while Masonry is on, so its
/// hidden ItemsControl does not build a second set of tiles. Values: [0] Masonry on (bool),
/// [1] the album list.
/// </summary>
public sealed class UnlessMasonryConverter : IMultiValueConverter
{
    public static readonly UnlessMasonryConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[0] is false ? values[1] : null;
}
