using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Joins metadata segments with " · ", skipping empty strings and non-positive
/// numbers so missing tags (no genre, no year) don't leave stray leading or
/// doubled separator dots. An optional ConverterParameter names a unit for the
/// last segment ("track" → "1 track" / "24 tracks").
/// </summary>
public class MetadataLineConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            var text = values[i] switch
            {
                null or UnsetValueType => null,
                string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
                int n => n > 0 ? n.ToString(culture) : null,
                uint n => n > 0 ? n.ToString(culture) : null,
                var v => v.ToString(),
            };
            if (string.IsNullOrEmpty(text)) continue;

            if (i == values.Count - 1 && parameter is string unit && unit.Length > 0)
                text = text == "1" ? $"{text} {unit}" : $"{text} {unit}s";

            parts.Add(text);
        }
        return string.Join(" · ", parts);
    }
}
