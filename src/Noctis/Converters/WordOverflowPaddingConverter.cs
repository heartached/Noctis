using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Right-padding for a karaoke word's dimmed base layer.
///
/// A line-final word (no trailing space — e.g. "Classic", "invested") renders its last
/// glyph flush against the TextBlock's advance-width bounds. While the base layer is
/// dimmed on the active line (<c>Opacity &lt; 1</c>) Avalonia composites it through a
/// bounds-clipped layer, which shears that last glyph — the "invisible wall" at the end
/// of the word. A few px of right padding widens the bounds so the glyph fits.
///
/// Mid-line words already carry a trailing space (their last glyph is not at the edge),
/// so they get no padding and stay tightly spaced. The padding on a line-final word is
/// trailing whitespace with nothing after it, so it costs no visible spacing anywhere.
/// </summary>
public class WordOverflowPaddingConverter : IValueConverter
{
    private const double RightPad = 24;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        var lineFinal = !string.IsNullOrEmpty(text) && !char.IsWhiteSpace(text[^1]);
        return lineFinal ? new Thickness(0, 0, RightPad, 0) : default(Thickness);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
