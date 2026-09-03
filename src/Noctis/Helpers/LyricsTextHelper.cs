using System.Text;
using System.Text.RegularExpressions;
using Noctis.Services;

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

    // Every timing tag the shift must move: line tags [mm:ss.xx] and inline word tags
    // <mm:ss.xx>. Captures the bracket so the replacement keeps the tag's kind.
    private static readonly Regex AnyTimeTagRegex =
        new(@"(?<open>[\[<])(?<min>\d{1,3}):(?<sec>\d{2})(?:[.:](?<frac>\d{1,3}))?(?<close>[\]>])", RegexOptions.Compiled);

    /// <summary>
    /// GitHub #57: moves every line and word timestamp in <paramref name="lrc"/> by
    /// <paramref name="offset"/> (negative = earlier). Times are clamped at zero so a
    /// large negative shift can't produce a tag the parser rejects. Untimed lines and
    /// metadata tags like [ar:...] are untouched; tags are re-emitted as mm:ss.xx.
    /// </summary>
    public static string ShiftAllTimestamps(string? lrc, TimeSpan offset)
    {
        if (string.IsNullOrEmpty(lrc) || offset == TimeSpan.Zero) return lrc ?? string.Empty;

        return AnyTimeTagRegex.Replace(lrc, m =>
        {
            var minutes = int.Parse(m.Groups["min"].Value);
            var seconds = int.Parse(m.Groups["sec"].Value);
            var fracText = m.Groups["frac"].Value;
            var millis = fracText.Length switch
            {
                0 => 0,
                1 => int.Parse(fracText) * 100,
                2 => int.Parse(fracText) * 10,
                _ => int.Parse(fracText[..3]),
            };
            var time = new TimeSpan(0, 0, minutes, seconds, millis) + offset;
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            return $"{m.Groups["open"].Value}{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}{m.Groups["close"].Value}";
        });
    }

    public static string StripTimestamps(string? lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent)) return string.Empty;

        var lines = lrcContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var plainLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) { plainLines.Add(""); continue; }

            var untimed = TimestampRegex.Replace(trimmed, "");
            // Duet voice markers ("v1:"/"v2:"/"v3:") ride the timestamp prefix —
            // layout syntax, not lyric text; plain derivations must not show them.
            // Reference check: Replace returns the same instance when nothing
            // matched, i.e. the line carried no timestamp and keeps its text.
            if (!ReferenceEquals(untimed, trimmed))
                untimed = EnhancedLrcParser.StripVoiceMarker(untimed).Body.TrimStart();
            var text = WordTagRegex.Replace(untimed, "");

            if (text.StartsWith('[') && text.Contains(':')) continue;

            if (!string.IsNullOrWhiteSpace(text))
                plainLines.Add(text);
        }

        return string.Join(Environment.NewLine, plainLines).Trim();
    }
}
