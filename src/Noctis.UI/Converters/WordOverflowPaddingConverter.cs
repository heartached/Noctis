using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Right-padding for a karaoke word cell, bound to the cell's <see cref="WordTiming"/>.
///
/// A line-final word (no trailing space — e.g. "Classic", "invested") renders its last
/// glyph flush against the TextBlock's advance-width bounds. While the base layer is
/// dimmed on the active line (<c>Opacity &lt; 1</c>) Avalonia composites it through a
/// bounds-clipped layer, which shears that last glyph — the "invisible wall" at the end
/// of the word. A few px of right padding widens the bounds so the glyph fits.
///
/// Only the line's last word is padded (<see cref="WordTiming.IsLastInLine"/>): the pad
/// is trailing whitespace with nothing after it, so it costs no visible spacing. A word
/// in mid-line gets none, whether or not it ends in a space: CJK tokens carry no spaces
/// at all, and padding every token that lacked one drew "想 い 眠 り に つく 君 の" for
/// 想い眠りにつく君の (live 10-19), a punctuation token apart from its word likewise.
/// </summary>
public class WordOverflowPaddingConverter : IValueConverter
{
    private const double RightPad = 24;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        PaddingFor(value as WordTiming);

    /// <summary>The cell padding for <paramref name="word"/> (also what <see cref="Controls.KaraokeRestingWords"/> lays out with).</summary>
    public static Thickness PaddingFor(WordTiming? word)
    {
        var lineFinal = word is { IsLastInLine: true, Text: { Length: > 0 } text }
                        && !char.IsWhiteSpace(text[^1]);
        return lineFinal ? new Thickness(0, 0, RightPad, 0) : default(Thickness);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
