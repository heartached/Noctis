using System.Text;

namespace Noctis.Plugins.LyricMotion.Motion;

/// <summary>
/// Which way lyric text reads (ported from Lyric Motion's Flow.cs). A song is right to left when
/// most of its letters are; in such a song every line with a right-to-left letter is laid out from
/// the right (a Persian line stays Persian with an English word or two in it), elsewhere only a
/// line that is mostly right to left. Digits and punctuation count for neither side.
/// </summary>
public static class Script
{
    /// <summary>Counts the letters of <paramref name="text"/> by direction.</summary>
    public static void Count(string? text, ref int rightToLeft, ref int leftToRight)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (IsRightToLeft(rune.Value)) rightToLeft++;
            else leftToRight++;
        }
    }

    /// <summary>Whether a line is laid out from the right.</summary>
    public static bool FlowsRightToLeft(int rtl, int ltr, bool songRightToLeft) =>
        rtl > 0 && (songRightToLeft || rtl > ltr);

    /// <summary>Fills <paramref name="rightToLeft"/> for the song's lines.</summary>
    public static void Directions(IReadOnlyList<string> lines, Span<bool> rightToLeft)
    {
        var n = Math.Min(lines.Count, rightToLeft.Length);
        Span<int> rtl = n <= 512 ? stackalloc int[n] : new int[n];
        Span<int> ltr = n <= 512 ? stackalloc int[n] : new int[n];
        int songRtl = 0, songLtr = 0;
        for (var i = 0; i < n; i++)
        {
            int r = 0, l = 0;
            Count(lines[i], ref r, ref l);
            rtl[i] = r;
            ltr[i] = l;
            songRtl += r;
            songLtr += l;
        }
        var song = songRtl > songLtr;
        for (var i = 0; i < n; i++) rightToLeft[i] = FlowsRightToLeft(rtl[i], ltr[i], song);
    }

    // Hebrew, Arabic (with Persian, Urdu, Pashto, Kurdish), Syriac, Thaana, N'Ko, Samaritan,
    // Mandaic and the older right-to-left scripts, with their presentation forms, plus Adlam and
    // the other supplementary right-to-left blocks.
    public static bool IsRightToLeft(int v) =>
        (v >= 0x0590 && v <= 0x08FF) || (v >= 0xFB1D && v <= 0xFDFF) || (v >= 0xFE70 && v <= 0xFEFC)
        || (v >= 0x10800 && v <= 0x10FFF) || (v >= 0x1E800 && v <= 0x1EFFF);
}
