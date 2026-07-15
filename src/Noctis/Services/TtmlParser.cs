using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Parses TTML lyrics (Apple Music style and generic TTML subtitles) into
/// <see cref="LyricLine"/> objects, preserving optional per-word/syllable timing.
///
/// Each &lt;p&gt; becomes a line (begin/end → line timestamps). Timed &lt;span&gt;
/// children become karaoke words; whitespace text nodes between spans mark word
/// boundaries (adjacent spans with no whitespace are syllables and get merged).
/// Untimed wrapper spans (e.g. Apple background vocals, ttm:role="x-bg") are
/// recursed into so their timed descendants still contribute.
/// </summary>
public static class TtmlParser
{
    /// <summary>Cheap discriminator — TTML documents open with a &lt;tt&gt; root element.</summary>
    public static bool LooksLikeTtml(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var idx = content.IndexOf("<tt", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 && idx < 512;
    }

    /// <summary>
    /// Parses TTML content. Returns (lines, plainText) where plainText is the lines
    /// joined for the Unsync tab. Returns nulls when the content is malformed.
    /// </summary>
    public static (List<LyricLine>? Lines, string? Plain) Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (null, null);

        XDocument doc;
        try
        {
            // DTD stays prohibited (XDocument.Parse default) — sidecars are untrusted input.
            // PreserveWhitespace keeps the whitespace-only text nodes between <span>s;
            // they are the word boundaries (spans with none between are syllables).
            doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return (null, null);
        }

        var root = doc.Root;
        if (root == null || !LocalNameIs(root, "tt")) return (null, null);

        var lines = new List<LyricLine>();
        foreach (var p in root.Descendants().Where(e => LocalNameIs(e, "p")))
        {
            var line = ParseLine(p);
            if (line != null)
                lines.Add(line);
        }

        if (lines.Count == 0) return (null, null);

        lines.Sort((a, b) => Nullable.Compare(a.Timestamp, b.Timestamp));

        var plain = string.Join('\n', lines.Select(l => l.Text));
        return (lines, plain);
    }

    private static LyricLine? ParseLine(XElement p)
    {
        var start = ParseTime(p.Attribute("begin")?.Value);
        var end = ParseTime(p.Attribute("end")?.Value);
        if (end.HasValue && start.HasValue && end < start) end = null;

        var text = new StringBuilder();
        var words = new List<WordTiming>();
        CollectContent(p, text, words);

        var lineText = text.ToString().Trim();
        if (lineText.Length == 0) return null;

        var line = new LyricLine
        {
            Timestamp = start,
            EndTimestamp = end,
            Text = lineText,
        };

        if (words.Count > 0)
        {
            // Trailing spaces are preserved on words except the final one (Lyricsfile
            // convention) — markup whitespace before </p> shouldn't widen the last cell.
            var lastWord = words[^1];
            if (lastWord.Text.TrimEnd() is { Length: > 0 } trimmed && trimmed != lastWord.Text)
            {
                words[^1] = new WordTiming { Text = trimmed, Start = lastWord.Start, End = lastWord.End };
            }

            // Backfill missing End times from the next word's Start, and the final word
            // from the line end (falls back to last word's Start + 300ms as a minimum).
            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].End.HasValue) continue;
                TimeSpan fallback;
                if (i + 1 < words.Count)
                    fallback = words[i + 1].Start;
                else if (end.HasValue)
                    fallback = end.Value;
                else
                    fallback = words[i].Start + TimeSpan.FromMilliseconds(300);

                words[i] = new WordTiming
                {
                    Text = words[i].Text,
                    Start = words[i].Start,
                    End = fallback,
                };
            }

            line.Words = EnhancedLrcParser.MergeSyllables(words);
        }

        return line;
    }

    /// <summary>
    /// Walks a line's content in document order. Timed spans append a word; whitespace
    /// between spans is attached to the previous word's tail (Lyricsfile convention:
    /// trailing spaces preserved) so syllable merging keeps word boundaries intact.
    /// </summary>
    private static void CollectContent(XElement parent, StringBuilder text, List<WordTiming> words)
    {
        foreach (var node in parent.Nodes())
        {
            switch (node)
            {
                case XText t:
                    AppendText(t.Value, text, words);
                    break;

                case XElement el when LocalNameIs(el, "br"):
                    AppendText(" ", text, words);
                    break;

                case XElement el when LocalNameIs(el, "span"):
                    var begin = ParseTime(el.Attribute("begin")?.Value);
                    if (begin.HasValue && !el.Elements().Any())
                    {
                        var wordText = el.Value;
                        if (wordText.Length > 0)
                        {
                            text.Append(wordText);
                            words.Add(new WordTiming
                            {
                                Text = wordText,
                                Start = begin.Value,
                                End = ParseTime(el.Attribute("end")?.Value) is { } e && e >= begin ? e : null,
                            });
                        }
                    }
                    else
                    {
                        // Untimed or wrapper span (e.g. background vocals) — recurse.
                        CollectContent(el, text, words);
                    }
                    break;
            }
        }
    }

    private static void AppendText(string value, StringBuilder text, List<WordTiming> words)
    {
        if (value.Length == 0) return;

        // Collapse whitespace runs (markup indentation/newlines) to a single space.
        var isWhitespace = string.IsNullOrWhiteSpace(value);
        if (isWhitespace)
        {
            if (text.Length > 0 && text[^1] != ' ')
                text.Append(' ');
        }
        else
        {
            text.Append(value);
        }

        // Whitespace after a timed word marks the word boundary; keep it on the word's
        // tail so MergeSyllables won't join across it. Non-whitespace inter-span text
        // (rare) also rides along rather than becoming an untimed orphan cell.
        if (words.Count > 0)
        {
            var last = words[^1];
            var tail = isWhitespace
                ? (last.Text.EndsWith(' ') ? string.Empty : " ")
                : value;
            if (tail.Length == 0) return;
            words[^1] = new WordTiming
            {
                Text = last.Text + tail,
                Start = last.Start,
                End = last.End,
            };
        }
    }

    private static bool LocalNameIs(XElement e, string name) =>
        string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a TTML time expression. Supports clock-time ("mm:ss.fff", "hh:mm:ss.fff")
    /// and offset-time ("7.5s", "1500ms", "2m", "1.5h", bare seconds). Frame/tick
    /// metrics ("25f", "10t") are rare in lyrics TTML and return null.
    /// </summary>
    internal static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            // hh:mm:ss:frames (4 parts) is frame-based — unsupported.
            if (parts.Length is < 2 or > 3) return null;

            double hours = 0, minutes, seconds;
            if (parts.Length == 3)
            {
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out hours)) return null;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) return null;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return null;
            }
            else
            {
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) return null;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return null;
            }

            var total = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return total >= TimeSpan.Zero ? total : null;
        }

        // Offset-time: number + optional metric suffix.
        double multiplierMs;
        string numberPart;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 1;
            numberPart = value[..^2];
        }
        else if (value.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 3_600_000;
            numberPart = value[..^1];
        }
        else if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 60_000;
            numberPart = value[..^1];
        }
        else if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            multiplierMs = 1000;
            numberPart = value[..^1];
        }
        else if (value.EndsWith("f", StringComparison.OrdinalIgnoreCase)
              || value.EndsWith("t", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else
        {
            // Bare number — treat as seconds (seen in the wild, not spec-strict).
            multiplierMs = 1000;
            numberPart = value;
        }

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        var result = TimeSpan.FromMilliseconds(n * multiplierMs);
        return result >= TimeSpan.Zero ? result : null;
    }
}
