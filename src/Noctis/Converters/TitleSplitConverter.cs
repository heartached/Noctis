using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Splits a title into "everything but the last word" (parameter <c>head</c>) and "the
/// last word" (parameter <c>last</c>). A wrapping title that ends with an inline badge
/// (the explicit "E") keeps the badge glued to the last word by rendering that word and
/// the badge together inside one InlineUIContainer: the line breaker then can't orphan
/// the badge onto a line of its own, which is what happened with long album names.
/// </summary>
public sealed class TitleSplitConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? string.Empty).TrimEnd();
        var wantLast = string.Equals(parameter as string, "last", StringComparison.OrdinalIgnoreCase);
        var (head, last) = Split(text);
        return wantLast ? last : head;
    }

    /// <summary>Head keeps its trailing space so the two parts read as one string when adjacent.</summary>
    public static (string Head, string Last) Split(string text)
    {
        if (string.IsNullOrEmpty(text)) return (string.Empty, string.Empty);
        var cut = text.LastIndexOf(' ');
        if (cut < 0) return (string.Empty, text);
        return (text[..(cut + 1)], text[(cut + 1)..]);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
