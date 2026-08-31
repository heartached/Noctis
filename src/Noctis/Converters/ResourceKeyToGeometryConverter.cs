using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Resolves a StreamGeometry resource key from Assets/Icons.axaml ("KeyboardIcon") to the
/// geometry itself, so an ItemsControl can bind PathIcon.Data to a string. Unlike
/// <see cref="IconKeyToGeometryConverter"/> (which maps to PNG bitmaps) this is vector.
/// </summary>
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public static ResourceKeyToGeometryConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        if (Application.Current is { } app && app.TryFindResource(key, out var resource) && resource is Geometry g)
            return g;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
