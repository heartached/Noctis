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

    private static IEnumerable<AlignedLine> Ordered(IEnumerable<AlignedLine> lines) =>
        lines.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Start);
}
