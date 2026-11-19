using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Localization;

namespace Noctis.Converters;

/// <summary>
/// The words after a count that sits in its own control ("[25] tracks, sorted by", "3 plays"),
/// in the plural form the language takes for that count: <see cref="Loc.Plural"/> on the key
/// named by the ConverterParameter. Used as a MultiBinding of the count and the key's own
/// <c>Loc.Instance[key]</c> binding, so a language switch re-reads it like a <c>{loc:T}</c> label.
/// A single fixed word read "11 أغانٍ" or "2 треков": right for some numbers only.
/// </summary>
public sealed class PluralWordConverter : IMultiValueConverter
{
    public static readonly PluralWordConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string key) return null;
        long count = values.Count > 0 ? values[0] switch
        {
            int i => i,
            long l => l,
            decimal d => (long)d,
            double x => (long)x,
            _ => 0,
        } : 0;
        return Loc.Plural(key, count);
    }
}
