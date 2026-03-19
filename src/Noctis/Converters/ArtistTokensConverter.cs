using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Converters;

/// <summary>
/// Converts an artist string (e.g. "A &amp; B feat. C") into an array of
/// <see cref="ArtistTokenItem"/> for display as individually clickable links.
/// </summary>
public class ArtistTokensConverter : IValueConverter
{
    public static readonly ArtistTokensConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string artist || string.IsNullOrWhiteSpace(artist))
            return Array.Empty<ArtistTokenItem>();

        var tokens = Track.ParseArtistTokens(artist);
        if (tokens.Length == 0)
            tokens = new[] { artist };

        return tokens
            .Select((name, i) => new ArtistTokenItem(name, IsLast: i == tokens.Length - 1))
            .ToArray();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
