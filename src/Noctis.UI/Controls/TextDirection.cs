using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// Which way a single line of text reads, by its first strong character (Unicode's rule for a
/// paragraph, UAX #9 P2/P3): "¿Cuál Es Tu Plan?" and "(Remix) …" read left to right from their
/// first letter, an Arabic title right to left. Laid out in a right-to-left screen without this,
/// a Latin title's leading neutrals ("¿", "(", "#", "...") resolved right to left and moved to
/// its other end. Null when the text has no strong character ("3:42", "♪", "").
/// </summary>
public static class TextDirection
{
    public static FlowDirection? FirstStrong(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;   // digits, punctuation, symbols, marks: weak or neutral
            return IsRightToLeftLetter(c) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        return null;
    }

    /// <summary>In a right-to-left script's blocks (Hebrew, Arabic, Syriac, Thaana, N'Ko and their
    /// presentation forms); callers ask it of letters only.</summary>
    public static bool IsRightToLeftLetter(char c) =>
        c is >= '\u0590' and <= '\u08FF'
        or >= '\uFB1D' and <= '\uFDFF'
        or >= '\uFE70' and <= '\uFEFF';
}
