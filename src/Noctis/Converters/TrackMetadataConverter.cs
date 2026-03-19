using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Converts Track metadata to formatted display text.
/// For singles (albums ending with "- Single"), shows just the album name.
/// For regular albums, shows "Artist — Album".
/// </summary>
public class TrackMetadataConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return string.Empty;

        var artist = values[0] as string ?? string.Empty;
        var album = values[1] as string ?? string.Empty;

        // Check if this is a single (album name ends with "- Single")
        if (album.EndsWith("- Single", StringComparison.OrdinalIgnoreCase))
        {
            // For singles, show just the album name which already includes "- Single"
            return album;
        }

        // For regular albums, show "Artist — Album"
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(artist))
            return album;
        if (string.IsNullOrWhiteSpace(album))
            return artist;

        return $"{artist} — {album}";
    }
}
