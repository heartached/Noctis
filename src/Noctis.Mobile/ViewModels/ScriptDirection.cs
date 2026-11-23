using Avalonia.Media;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Which way a text reads, by its letters rather than by the phone's language: the lyrics of an
/// Arabic song read right to left in any UI, and an English song's lyrics stay left to right on
/// an Arabic phone. The letters of the right-to-left scripts (Hebrew, Arabic, Syriac, Thaana,
/// N'Ko and their presentation forms) are counted against every other letter; more of them makes
/// it right to left. Digits, punctuation and symbols do not count, so "♪" or "3:42" decide nothing.
/// </summary>
public static class ScriptDirection
{
    /// <summary>RightToLeft when most letters of <paramref name="texts"/> are of a right-to-left
    /// script; LeftToRight otherwise, also when there are no letters at all.</summary>
    public static FlowDirection Of(IEnumerable<string?> texts)
    {
        long rtl = 0, ltr = 0;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var c in text)
            {
                if (!char.IsLetter(c)) continue;
                if (IsRightToLeftLetter(c)) rtl++;
                else ltr++;
            }
        }
        return rtl > ltr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    internal static bool IsRightToLeftLetter(char c) => Noctis.Controls.TextDirection.IsRightToLeftLetter(c);
}
