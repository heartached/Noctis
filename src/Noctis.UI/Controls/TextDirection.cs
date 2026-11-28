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

    /// <summary>
    /// Which lyric lines read right to left, when no lyrics effect decides it (the rule Lyric
    /// Motion's Script.Directions uses): a song is right to left when most of its letters are, and
    /// then every line with a right-to-left letter is (a Persian line keeps an English word or
    /// two); in any other song only a line that is mostly right to left. Digits and punctuation
    /// count for neither side.
    /// </summary>
    public static void LineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft)
    {
        var n = Math.Min(lines.Count, rightToLeft.Length);
        var rtl = new int[n];
        var ltr = new int[n];
        int songRtl = 0, songLtr = 0;
        for (var i = 0; i < n; i++)
        {
            foreach (var c in lines[i] ?? "")
            {
                if (!char.IsLetter(c)) continue;
                if (IsRightToLeftLetter(c)) rtl[i]++;
                else ltr[i]++;
            }
            songRtl += rtl[i];
            songLtr += ltr[i];
        }
        var song = songRtl > songLtr;
        for (var i = 0; i < n; i++) rightToLeft[i] = rtl[i] > 0 && (song || rtl[i] > ltr[i]);
    }

    /// <summary>In a right-to-left script's blocks (Hebrew, Arabic, Syriac, Thaana, N'Ko and their
    /// presentation forms); callers ask it of letters only.</summary>
    public static bool IsRightToLeftLetter(char c) => Noctis.Helpers.RightToLeftScript.IsLetter(c);
}
