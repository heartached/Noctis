using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Parses the body of an enhanced ("A2") LRC line — the text that follows the
/// <c>[mm:ss.xx]</c> line timestamp — into plain text plus optional per-word
/// karaoke timings, using inline <c>&lt;mm:ss.xx&gt;</c> word tags.
///
/// Example body: <c>&lt;00:05.41&gt;Hello &lt;00:05.90&gt;world&lt;00:06.40&gt;</c>
/// A trailing tag with no following text marks the end time of the final word.
/// </summary>
public static partial class EnhancedLrcParser
{
    // Inline word tag: <mm:ss.xx>, <mm:ss:xx> or <mm:ss>. Captures the time body.
    [GeneratedRegex(@"<(\d{1,3}:\d{2}(?:[.:]\d{1,3})?)>")]
    private static partial Regex WordTagRegex();

    /// <summary>True when the body contains at least one inline word tag.</summary>
    public static bool ContainsWordTags(string? body) =>
        !string.IsNullOrEmpty(body) && WordTagRegex().IsMatch(body);

    /// <summary>
    /// Splits an enhanced-LRC line body into display text and per-word timings.
    /// Returns (plainText, null) when the body carries no inline word tags.
    /// </summary>
    public static (string Text, List<WordTiming>? Words) ParseLine(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return (string.Empty, null);

        var matches = WordTagRegex().Matches(body);
        if (matches.Count == 0)
            return (body.Trim(), null);

        var words = new List<WordTiming>();

        for (int i = 0; i < matches.Count; i++)
        {
            var tag = matches[i];
            var start = ParseTagTime(tag.Groups[1].Value);
            if (start == null) continue;

            var textStart = tag.Index + tag.Length;
            var textEnd = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var text = body[textStart..textEnd];

            // A tag with no following text is the end marker for the previous word.
            if (string.IsNullOrEmpty(text))
            {
                if (words.Count > 0)
                {
                    var last = words[^1];
                    words[^1] = new WordTiming { Text = last.Text, Start = last.Start, End = start };
                }
                continue;
            }

            // Preserve any text that appeared before the first tag by folding it
            // into the first word so no characters are dropped.
            if (words.Count == 0 && tag.Index > 0)
                text = body[..tag.Index] + text;

            TimeSpan? end = i + 1 < matches.Count ? ParseTagTime(matches[i + 1].Groups[1].Value) : null;
            words.Add(new WordTiming { Text = text, Start = start.Value, End = end });
        }

        var plain = WordTagRegex().Replace(body, "").Trim();
        return (plain, words.Count > 0 ? MergeSyllables(words) : null);
    }

    /// <summary>
    /// Joins syllable-level timings (adjacent segments with no whitespace between
    /// them, e.g. "tal" + "king ") into whole words, so the karaoke WrapPanel can
    /// never insert a line break mid-word. The merged segment spans first.Start →
    /// last.End, which the sweep still crosses continuously. CJK scripts carry no
    /// spaces at all, so those boundaries are left unmerged — per-character cells
    /// wrap fine typographically, and merging would build one unwrappable mega-cell.
    /// </summary>
    public static List<WordTiming> MergeSyllables(List<WordTiming> words)
    {
        if (words.Count < 2) return words;

        var merged = new List<WordTiming>(words.Count) { words[0] };
        for (int i = 1; i < words.Count; i++)
        {
            var prev = merged[^1];
            var cur = words[i];
            if (prev.Text.Length > 0 && cur.Text.Length > 0
                && IsJoinable(prev.Text[^1]) && IsJoinable(cur.Text[0]))
            {
                merged[^1] = new WordTiming
                {
                    Text = prev.Text + cur.Text,
                    Start = prev.Start,
                    End = cur.End,
                };
            }
            else
            {
                merged.Add(cur);
            }
        }
        return merged;
    }

    // Letters/digits/apostrophes/hyphens below the CJK blocks join into one word;
    // whitespace, punctuation and CJK characters keep segments separate.
    private static bool IsJoinable(char c) =>
        (char.IsLetterOrDigit(c) || c is '\'' or '’' or '-') && c < '⺀';

    /// <summary>Strips inline word tags from an enhanced-LRC body (display fallback).</summary>
    public static string StripWordTags(string? body) =>
        string.IsNullOrEmpty(body) ? string.Empty : WordTagRegex().Replace(body, "");

    private static TimeSpan? ParseTagTime(string value)
    {
        var inner = value.Replace(',', '.');
        var parts = inner.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return null;

        if (!int.TryParse(parts[0], out var minutes)) return null;

        if (parts.Length == 2)
        {
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return null;
            return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }

        if (!int.TryParse(parts[1], out var wholeSeconds)) return null;
        if (!int.TryParse(parts[2], out var fractionalUnit)) return null;

        var divisor = Math.Pow(10, parts[2].Length);
        return TimeSpan.FromMinutes(minutes) +
               TimeSpan.FromSeconds(wholeSeconds + fractionalUnit / divisor);
    }
}
