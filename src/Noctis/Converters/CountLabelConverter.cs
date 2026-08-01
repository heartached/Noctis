using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Formats a count with its noun, singular at exactly one: "1 playlist",
/// "2 playlists". The noun is the singular form, passed as ConverterParameter.
/// </summary>
public class CountLabelConverter : IValueConverter
{
    public static readonly CountLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };
        var noun = parameter as string ?? string.Empty;
        return count == 1 ? $"{count} {noun}" : $"{count} {noun}s";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
