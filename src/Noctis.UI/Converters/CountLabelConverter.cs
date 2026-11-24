using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Localization;

namespace Noctis.Converters;

/// <summary>
/// Formats a count with its noun, singular at exactly one: "1 playlist",
/// "2 playlists". The noun is the singular form, passed as ConverterParameter.
/// "song" and "playlist" read from the translations (Count.Songs / Count.Playlists, with each
/// language's plural forms): built in English, the sidebar said "4 songs" in every language.
/// As a multi-converter the first value is the count and the rest only re-run it: bind the
/// translation too (Loc.Instance[Count.Songs]) so a language change, including the one applied
/// at startup after the sidebar first drew, re-labels the count.
/// </summary>
public class CountLabelConverter : IValueConverter, IMultiValueConverter
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
        switch (noun)
        {
            case "song": return Loc.Plural("Count.Songs", count);
            case "playlist": return Loc.Plural("Count.Playlists", count);
        }
        return count == 1 ? $"{count} {noun}" : $"{count} {noun}s";
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => Convert(values.Count > 0 ? values[0] : null, targetType, parameter, culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
