using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Services;

namespace Noctis.Converters;

/// <summary>
/// Converts a file path string to an Avalonia Bitmap for display in Image controls.
/// Delegates to the shared ArtworkCache for LRU caching and thumbnail-size decode.
/// Note: This converter loads synchronously (cache hit = instant, miss = blocks UI).
/// For virtualized lists, prefer the CachedImage control which loads asynchronously.
/// </summary>
public class ArtworkPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return null;

        // Try cache first (instant, no I/O)
        var cached = ArtworkCache.TryGet(path);
        if (cached != null)
            return cached;

        // Synchronous load + cache
        return ArtworkCache.LoadAndCache(path);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
