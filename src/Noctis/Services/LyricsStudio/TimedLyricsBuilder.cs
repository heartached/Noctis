using System.Text;
using System.Text.RegularExpressions;

namespace Noctis.Services.LyricsStudio;

/// <summary>
/// Serialises aligned lines to the two formats the lyrics page reads: plain LRC
/// (<c>[mm:ss.xx]text</c>) and enhanced LRC with inline word tags
/// (<c>[mm:ss.xx]&lt;mm:ss.xx&gt;word …&lt;mm:ss.xx&gt;</c>, the syntax
/// <see cref="EnhancedLrcParser"/> accepts, trailing tag = end of the last word).
/// </summary>
public static partial class TimedLyricsBuilder
{
    public static string FormatTimestamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    public static string BuildLrc(IEnumerable<AlignedLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in Ordered(lines))
            sb.Append('[').Append(FormatTimestamp(line.Start)).Append(']').Append(line.Text).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    public static string BuildElrc(IEnumerable<AlignedLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in Ordered(lines))
            sb.Append('[').Append(FormatTimestamp(line.Start)).Append(']').Append(BuildElrcBody(line)).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>One line as ELRC writes it after its <c>[mm:ss.xx]</c>; a line with no word timings is its plain text.</summary>
    public static string BuildElrcBody(AlignedLine line)
    {
        if (line.Words.Count == 0) return line.Text;
        var sb = new StringBuilder();
        for (var i = 0; i < line.Words.Count; i++)
        {
            var w = line.Words[i];
            sb.Append('<').Append(FormatTimestamp(w.Start)).Append('>').Append(w.Text.Trim());
            if (i + 1 < line.Words.Count) sb.Append(' ');
        }
        return sb.Append('<').Append(FormatTimestamp(line.Words[^1].End)).Append('>').ToString();
    }

    /// <summary>A hand-typed time further than this outside its line is taken for a typo (a slipped minute digit).</summary>
    private static readonly TimeSpan EditSlack = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultWordSpan = TimeSpan.FromMilliseconds(420);

    [GeneratedRegex(@"<([^<>]*)>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"^\d{1,3}:[0-5]\d(?:[.:]\d{1,3})?$")]
    private static partial Regex TagTime();

    /// <summary>
    /// Reads a hand-edited <see cref="BuildElrcBody"/> back into timed words, through
    /// <see cref="EnhancedLrcParser"/>. Text before the first tag starts at
    /// <paramref name="lineStart"/>; untagged words after a tag share the time up to the next
    /// tag evenly; the trailing tag is the last word's end (else <paramref name="lineEnd"/>).
    /// A body with no tags returns true with null <paramref name="words"/>: it is plain text.
    /// False, with a reason, when a tag is not a time, the times run backwards, or a time
    /// lands far outside the line.
    /// </summary>
    public static bool TryParseElrcBody(string body, TimeSpan lineStart, TimeSpan lineEnd,
        out IReadOnlyList<AlignedWord>? words, out string? error)
    {
        words = null;
        error = null;
        body ??= string.Empty;
        // A tag the parser would not read stays in the text: refuse it rather than keep "<00:3a.00>" as a word.
        foreach (Match tag in AnyTag().Matches(body))
        {
            var inner = tag.Groups[1].Value;
            if (TagTime().IsMatch(inner) || !inner.Any(c => char.IsDigit(c) || c == ':')) continue;
            error = $"“{tag.Value}” isn't a time · write it as <mm:ss.xx>";
            return false;
        }
        if (!EnhancedLrcParser.ContainsWordTags(body)) return true;

        var (_, timings) = EnhancedLrcParser.ParseLine(EnhancedLrcParser.TagLeadingText(body, lineStart));
        if (timings is not { Count: > 0 })
        {
            error = "No words between the times";
            return false;
        }

        if (lineEnd < lineStart) lineEnd = lineStart;
        var low = lineStart - EditSlack;
        var high = lineEnd + EditSlack;
        var result = new List<AlignedWord>();
        for (var i = 0; i < timings.Count; i++)
        {
            var t = timings[i];
            var tokens = t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) continue;
            // As in the review: a word ends where the next one starts; the last at the end tag.
            var end = i + 1 < timings.Count ? timings[i + 1].Start
                : t.End ?? (lineEnd > t.Start ? lineEnd : t.Start + DefaultWordSpan * tokens.Length);
            if (end < t.Start)
            {
                error = $"Times go backwards after “{tokens[^1]}”";
                return false;
            }
            if (t.Start < low || t.Start > high || end > high)
            {
                var far = t.Start < low || t.Start > high ? t.Start : end;
                error = $"<{FormatTimestamp(far)}> is far from this line ({FormatTimestamp(lineStart)}–{FormatTimestamp(lineEnd)})";
                return false;
            }
            // Words typed without a tag of their own split the tagged word's span evenly.
            var slice = (end - t.Start) / tokens.Length;
            for (var k = 0; k < tokens.Length; k++)
                result.Add(new AlignedWord(tokens[k], t.Start + slice * k, k + 1 < tokens.Length ? t.Start + slice * (k + 1) : end));
        }
        words = result;
        return true;
    }

    public static string BuildPlain(IEnumerable<AlignedLine> lines) =>
        string.Join('\n', Ordered(lines).Select(l => l.Text));

    /// <summary>
    /// TTML in Apple Music's lyrics layout, for players that read TTML and LRC but not ELRC
    /// (Discord, nutf!xx: Light Cone): one &lt;p&gt; per line, one &lt;span&gt; per timed word with
    /// a space between spans (the word boundary <see cref="TtmlParser"/> and Apple's own files
    /// use), clock times hh:mm:ss.fff. A line without word timings is its text; itunes:timing
    /// is "Word" when any line has words, else "Line". Ad-libs go in Apple's background-vocal
    /// span (ttm:role="x-bg") by the rules the ELRC reader applies to the same text
    /// (<see cref="EnhancedLrcParser.FoldBackgroundLines"/>): a parenthesised run inside a line
    /// is that line's background, and a fully parenthesised word-timed line joins the line
    /// before it — so the .ttml shows "(Yeah)" as the small row under the line, as the .elrc
    /// does, instead of inline at full size.
    /// </summary>
    public static string BuildTtml(IEnumerable<AlignedLine> lines)
    {
        var paragraphs = new List<TtmlParagraph>();
        foreach (var line in Ordered(lines))
        {
            if (line.Words.Count > 0 && paragraphs.Count > 0 && IsFullyParenthesized(line.Text))
            {
                paragraphs[^1].Background.AddRange(line.Words);
                continue;
            }
            var (main, background) = SplitInlineBackground(line.Words);
            paragraphs.Add(new TtmlParagraph(line, main, background));
        }

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"")
          .Append(" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\"")
          .Append(" itunes:timing=\"").Append(paragraphs.Any(p => p.Line.Words.Count > 0) ? "Word" : "Line").Append("\">\n");
        sb.Append("  <head><metadata/></head>\n");
        if (paragraphs.Count == 0)
            return sb.Append("  <body/>\n</tt>").ToString();

        var ends = paragraphs.Select(p => p.End).ToList();
        sb.Append("  <body>\n    <div begin=\"").Append(FormatTtmlTime(paragraphs[0].Line.Start))
          .Append("\" end=\"").Append(FormatTtmlTime(ends.Max())).Append("\">\n");
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var (line, main, background) = paragraphs[i];
            sb.Append("      <p begin=\"").Append(FormatTtmlTime(line.Start)).Append("\" end=\"").Append(FormatTtmlTime(ends[i]))
              .Append("\" itunes:key=\"L").Append(i + 1).Append("\">");
            if (line.Words.Count == 0)
                sb.Append(XmlText(line.Text.Trim()));
            AppendWordSpans(sb, main);
            if (background.Count > 0)
            {
                if (main.Count > 0) sb.Append(' ');
                sb.Append("<span ttm:role=\"x-bg\">");
                AppendWordSpans(sb, background);
                sb.Append("</span>");
            }
            sb.Append("</p>\n");
        }
        return sb.Append("    </div>\n  </body>\n</tt>").ToString();
    }

    /// <summary>One TTML &lt;p&gt;: the line, its main words and its background (ad-lib) words.</summary>
    private sealed record TtmlParagraph(AlignedLine Line, List<AlignedWord> Main, List<AlignedWord> Background)
    {
        /// <summary>The line's end, its last word's end, or the end of a background folded into it; never before its start.</summary>
        public TimeSpan End
        {
            get
            {
                var end = Line.End;
                if (Line.Words.Count > 0 && Line.Words[^1].End > end) end = Line.Words[^1].End;
                if (Background.Count > 0 && Background[^1].End > end) end = Background[^1].End;
                return end < Line.Start ? Line.Start : end;
            }
        }
    }

    private static void AppendWordSpans(StringBuilder sb, IReadOnlyList<AlignedWord> words)
    {
        for (var k = 0; k < words.Count; k++)
        {
            var w = words[k];
            if (k > 0) sb.Append(' ');
            sb.Append("<span begin=\"").Append(FormatTtmlTime(w.Start)).Append("\" end=\"")
              .Append(FormatTtmlTime(w.End < w.Start ? w.Start : w.End)).Append("\">")
              .Append(XmlText(w.Text.Trim())).Append("</span>");
        }
    }

    /// <summary>"(…)" with no other parenthesis inside: the ELRC reader's background-line test.</summary>
    private static bool IsFullyParenthesized(string text)
    {
        text = text.Trim();
        if (text.Length < 2 || text[0] is not ('(' or '（') || text[^1] is not (')' or '）')) return false;
        return !text[1..^1].Any(c => c is '(' or ')' or '（' or '）');
    }

    /// <summary>
    /// Parenthesised word runs out of a line, as the ELRC reader splits them: the line must
    /// keep a main word, and an unmatched "(" leaves the whole line alone.
    /// </summary>
    private static (List<AlignedWord> Main, List<AlignedWord> Background) SplitInlineBackground(IReadOnlyList<AlignedWord> words)
    {
        var main = new List<AlignedWord>();
        var background = new List<AlignedWord>();
        var inRun = false;
        foreach (var w in words)
        {
            var visible = w.Text.Trim();
            if (!inRun && visible.Length > 0 && visible[0] is '(' or '（') inRun = true;
            if (inRun)
            {
                background.Add(w);
                if (visible.Length > 0 && visible[^1] is ')' or '）') inRun = false;
            }
            else main.Add(w);
        }
        return inRun || main.Count == 0 || background.Count == 0
            ? (words.ToList(), new List<AlignedWord>())
            : (main, background);
    }

    public static string FormatTtmlTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private static string XmlText(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static IEnumerable<AlignedLine> Ordered(IEnumerable<AlignedLine> lines) =>
        lines.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Start);
}
