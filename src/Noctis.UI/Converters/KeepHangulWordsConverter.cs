using System;
using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Display-only: puts a WORD JOINER (U+2060) between adjacent Hangul syllables so a wrapped
/// Korean line breaks at its spaces. Unicode line breaking allows a break between any two
/// Hangul syllables, so Avalonia split words across lines ("따 / 라", seen on the phone's
/// lyrics, 2026-10-05); Korean is set word by word, as Apple Music shows it. Text without two
/// adjacent syllables is returned unchanged (the same instance).
/// </summary>
public sealed class KeepHangulWordsConverter : IValueConverter
{
    public const char WordJoiner = '\u2060';

    public static KeepHangulWordsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text ? Apply(text) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text ? text.Replace(WordJoiner.ToString(), string.Empty, StringComparison.Ordinal) : value;

    public static string Apply(string text)
    {
        var first = -1;
        for (var i = 1; i < text.Length; i++)
            if (IsSyllable(text[i - 1]) && IsSyllable(text[i])) { first = i; break; }
        if (first < 0) return text;

        var sb = new StringBuilder(text.Length + text.Length / 2);
        sb.Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
        {
            if (IsSyllable(text[i - 1]) && IsSyllable(text[i])) sb.Append(WordJoiner);
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private static bool IsSyllable(char c) => c is >= '가' and <= '힣';
}
