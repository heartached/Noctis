using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Noctis.Converters;

/// <summary>
/// Maps a sidebar icon key (e.g. "HomeIcon") to a cached, pre-scaled <see cref="Bitmap"/>.
/// Icons are decoded once and downscaled to <see cref="ScaledSize"/>px with high-quality
/// interpolation so the OpacityMask rendering path gets a clean small bitmap instead of
/// having to down-sample the original 512x512 source at runtime.
/// </summary>
public class IconKeyToGeometryConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> IconMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HomeIcon"] = "avares://Noctis/Assets/Icons/Home%20ICON.png",
            ["SongsIcon"] = "avares://Noctis/Assets/Icons/Songs%20ICON.png",
            ["AlbumsIcon"] = "avares://Noctis/Assets/Icons/Albums%20ICON.png",
            ["ArtistsIcon"] = "avares://Noctis/Assets/Icons/Artists%20ICON.png",

            ["PlaylistsIcon"] = "avares://Noctis/Assets/Icons/Playlists%20ICON.png",
            ["FavoritesIcon"] = "avares://Noctis/Assets/Icons/Your%20Favorites%20ICON.png",
            ["SettingsIcon"] = "avares://Noctis/Assets/Icons/Settings%20ICON.png",
            // Fallback for smart playlist rows in sidebar.
            ["SmartPlaylistIcon"] = "avares://Noctis/Assets/Icons/Playlists%20ICON.png"
        };

    private static readonly ConcurrentDictionary<string, Bitmap?> BitmapCache = new();
    private const string FallbackUri = "avares://Noctis/Assets/Icons/Playlists%20ICON.png";

    /// <summary>
    /// Target pixel size for pre-scaled icons.
    /// 80px covers 20 logical px at up to 4× DPI scaling.
    /// </summary>
    private const int ScaledSize = 80;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Accept icon key from the bound value (sidebar NavItem) or from
        // ConverterParameter (static usage in settings / dashboard cards).
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key))
            key = parameter as string;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var uri = IconMap.TryGetValue(key, out var mapped) ? mapped : FallbackUri;

        return BitmapCache.GetOrAdd(uri, static u =>
        {
            try
            {
                var assetUri = new Uri(u);
                using var stream = AssetLoader.Open(assetUri);
                using var full = new Bitmap(stream);
                return full.CreateScaledBitmap(
                    new PixelSize(ScaledSize, ScaledSize),
                    BitmapInterpolationMode.HighQuality);
            }
            catch
            {
                return null;
            }
        });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
