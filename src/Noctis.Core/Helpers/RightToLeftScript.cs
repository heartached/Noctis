namespace Noctis.Helpers;

/// <summary>
/// The right-to-left scripts' letters (Hebrew, Arabic, Syriac, Thaana, N'Ko and their
/// presentation forms), for models that decide a text's direction without Avalonia. The UI's
/// TextDirection asks the same question.
/// </summary>
public static class RightToLeftScript
{
    /// <summary>In a right-to-left script's blocks; callers ask it of letters only.</summary>
    public static bool IsLetter(char c) =>
        c is >= '֐' and <= 'ࣿ'
        or >= 'יִ' and <= '﷿'
        or >= 'ﹰ' and <= '﻿';

    /// <summary>Whether a text's first letter (UAX #9 P2) is right to left; false when it has
    /// no letter at all.</summary>
    public static bool StartsRightToLeft(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (char.IsLetter(c)) return IsLetter(c);
        return false;
    }
}
