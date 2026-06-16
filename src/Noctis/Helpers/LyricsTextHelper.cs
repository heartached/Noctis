using System.Text;
using System.Text.RegularExpressions;

namespace Noctis.Helpers;

public static class LyricsTextHelper
{
    /// <summary>
    /// Normalizes lyric text for display: exotic Unicode spaces (NBSP, en/em/thin
    /// spaces, etc.) become regular spaces, line/paragraph separators become newlines,
    /// and zero-width / soft-hyphen / replacement characters are dropped. These show
    /// up as empty "tofu" boxes when a font has no glyph for them — common in embedded
    /// (iTunes/Musixmatch) and online lyrics.
    /// </summary>
    public static string CleanDisplayText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            int c = ch;

            // Zero-width space/joiner, BOM/ZWNBSP, soft hyphen, replacement char — drop.
            if (c == 0x200B || c == 0x200C || c == 0x200D ||
                c == 0xFEFF || c == 0x00AD || c == 0xFFFD)
                continue;

            // Unicode line / paragraph separators -> real newline.
            if (c == 0x2028 || c == 0x2029)
            {
                sb.Append('\n');
                continue;
            }

            // Non-breaking and other exotic Unicode spaces -> normal space.
            // U+2000..U+200A: en/em/thin/hair spaces and friends.
            if (c == 0x00A0 || c == 0x202F || c == 0x205F || c == 0x3000 ||
                (c >= 0x2000 && c <= 0x200A))
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static readonly Regex TimestampRegex =
        new(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]\s*", RegexOptions.Compiled);

    // Inline enhanced-LRC ("A2") word tags, e.g. <00:05.41>.
    private static readonly Regex WordTagRegex =
        new(@"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    public static bool ContainsTimestamps(string? text) =>
        !string.IsNullOrWhiteSpace(text) && TimestampRegex.IsMatch(text);

    public static string StripTimestamps(string? lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent)) return string.Empty;

        var lines = lrcContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var plainLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) { plainLines.Add(""); continue; }

            var text = WordTagRegex.Replace(TimestampRegex.Replace(trimmed, ""), "");

            if (text.StartsWith('[') && text.Contains(':')) continue;

            if (!string.IsNullOrWhiteSpace(text))
                plainLines.Add(text);
        }

        return string.Join(Environment.NewLine, plainLines).Trim();
    }
}
