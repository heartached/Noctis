using System.Text;
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

    /// <summary>
    /// Upper bound on timed words in one line, and in one line's background row. Each
    /// word realizes its own karaoke cell (~7 controls) on the UI thread, so a hostile
    /// line carrying hundreds of thousands of word tags must not reach the view. No real
    /// lyric line comes close.
    /// </summary>
    internal const int MaxWordsPerLine = 512;

    /// <summary>True when the body contains at least one inline word tag.</summary>
    public static bool ContainsWordTags(string? body) =>
        !string.IsNullOrEmpty(body) && WordTagRegex().IsMatch(body);

    /// <summary>
    /// Strips an iTunes/Gramophone duet voice marker from a line body — the text directly
    /// after the <c>[mm:ss.xx]</c> timestamp block. Accepts <c>v&lt;digits&gt;:</c>,
    /// lowercase only, at most one leading space, and before any inline word tag
    /// (Gramophone's syntax, widened to any id: Apple TTML exports carry agents such as
    /// "v1000"/"v2000" for sampled clips, and those must never surface as lyric text).
    /// "v2:" is <see cref="LyricVoice.Voice2"/>, "v3:" <see cref="LyricVoice.Group"/>;
    /// "v1:" and every other id map to <see cref="LyricVoice.Default"/> — the normal
    /// left-aligned layout. Anything else (uppercase, extra spaces, mid-line) is lyric text.
    /// </summary>
    public static (string Body, LyricVoice Voice) StripVoiceMarker(string? body)
    {
        if (string.IsNullOrEmpty(body)) return (string.Empty, LyricVoice.Default);

        var i = body[0] == ' ' ? 1 : 0;
        if (i < body.Length && body[i] == 'v')
        {
            var j = i + 1;
            while (j < body.Length && char.IsAsciiDigit(body[j])) j++;
            if (j > i + 1 && j < body.Length && body[j] == ':')
            {
                var voice = body[(i + 1)..j] switch
                {
                    "2" => LyricVoice.Voice2,
                    "3" => LyricVoice.Group,
                    _ => LyricVoice.Default,
                };
                return (body[(j + 1)..], voice);
            }
        }

        return (body, LyricVoice.Default);
    }

    /// <summary>
    /// A common ELRC variant leaves the first word untagged because the line stamp already
    /// times it: <c>[00:12.34]Hello &lt;00:12.80&gt;world</c>. Tags that leading text with
    /// <paramref name="lineStart"/> so it becomes its own word, instead of being folded into
    /// the next tagged word (which would start it late and glue the two words together).
    /// Bodies that start with a tag, or carry no tags, come back unchanged.
    /// </summary>
    public static string TagLeadingText(string body, TimeSpan lineStart)
    {
        if (string.IsNullOrEmpty(body)) return body ?? string.Empty;
        var first = WordTagRegex().Match(body);
        if (!first.Success || first.Index == 0 || string.IsNullOrWhiteSpace(body[..first.Index])) return body;
        return $"<{(int)lineStart.TotalMinutes:00}:{lineStart.Seconds:00}.{lineStart.Milliseconds:000}>{body}";
    }

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

            // Too many words to be a real karaoke line: keep the text, drop word timing.
            if (words.Count >= MaxWordsPerLine)
                return (StripWordTags(body).Trim(), null);

            // Preserve any text that appeared before the first tag by folding it
            // into the first word so no characters are dropped.
            if (words.Count == 0 && tag.Index > 0)
                text = body[..tag.Index] + text;

            TimeSpan? end = i + 1 < matches.Count ? ParseTagTime(matches[i + 1].Groups[1].Value) : null;
            words.Add(new WordTiming { Text = text, Start = start.Value, End = end });
        }

        var plain = WordTagRegex().Replace(body, "").Trim();
        var normalized = ShiftLeadingSpaces(words);
        return (plain, normalized.Count > 0 ? MergeSyllables(normalized) : null);
    }

    /// <summary>
    /// Moves each token's leading whitespace onto the previous token's tail. The sweep
    /// overlay trims trailing whitespace (<see cref="WordTiming.SweepText"/>) but must
    /// keep leading whitespace for glyph alignment, so "&lt;t&gt;word&lt;t&gt; word"
    /// files stall the sweep on invisible space at the start of every word's window.
    /// Tokens left empty are dropped; their time span becomes a plain gap. The first
    /// token is trimmed to match the Trim()ed display text.
    /// </summary>
    private static List<WordTiming> ShiftLeadingSpaces(List<WordTiming> words)
    {
        var shifted = new List<WordTiming>(words.Count);
        foreach (var w in words)
        {
            var text = w.Text;
            var lead = 0;
            while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;

            if (lead > 0)
            {
                if (shifted.Count > 0)
                {
                    var prev = shifted[^1];
                    shifted[^1] = new WordTiming
                    {
                        Text = prev.Text + text[..lead],
                        Start = prev.Start,
                        End = prev.End,
                    };
                }
                text = text[lead..];
            }

            if (text.Length > 0)
                shifted.Add(new WordTiming { Text = text, Start = w.Start, End = w.End });
        }
        return shifted;
    }

    /// <summary>
    /// Joins syllable-level timings (adjacent segments with no whitespace between
    /// them, e.g. "tal" + "king ") into whole words, so the karaoke WrapPanel can
    /// never insert a line break mid-word. The merged segment spans first.Start →
    /// last.End, and keeps each source syllable in <see cref="WordTiming.Syllables"/>
    /// so the sweep can still advance on the syllables' own clocks. CJK scripts carry
    /// no spaces at all, so those boundaries are left unmerged — per-character cells
    /// wrap fine typographically, and merging would build one unwrappable mega-cell.
    /// </summary>
    public static List<WordTiming> MergeSyllables(List<WordTiming> words)
    {
        if (words.Count < 2) return words;

        var merged = new List<WordTiming>(words.Count) { words[0] };
        // Parallel to `merged`; null until a word actually absorbs a second syllable.
        var parts = new List<List<WordSyllable>?>(words.Count) { null };
        // Parallel to `parts`: the joined text, materialized once below. Concatenating
        // per syllable would be quadratic in the length of a joinable run.
        var texts = new List<StringBuilder?>(words.Count) { null };
        var tail = words[0]; // last segment absorbed into merged[^1]

        for (int i = 1; i < words.Count; i++)
        {
            var cur = words[i];
            if (tail.Text.Length > 0 && cur.Text.Length > 0
                && IsJoinable(tail.Text[^1]) && IsJoinable(cur.Text[0]))
            {
                var head = merged[^1];
                var segments = parts[^1] ??= [new WordSyllable(head.Start, head.End, head.Text.Length)];
                segments.Add(new WordSyllable(cur.Start, cur.End, cur.Text.Length));
                (texts[^1] ??= new StringBuilder(head.Text)).Append(cur.Text);
            }
            else
            {
                merged.Add(cur);
                parts.Add(null);
                texts.Add(null);
            }
            tail = cur;
        }

        for (int i = 0; i < merged.Count; i++)
        {
            if (parts[i] is not { } segments) continue;
            var text = texts[i]!.ToString();
            merged[i] = new WordTiming
            {
                Text = text,
                Start = merged[i].Start,
                End = segments[^1].End,
                Syllables = TrimTrailingSpace(segments, text),
            };
        }

        return merged;
    }

    /// <summary>
    /// Discounts the merged word's trailing whitespace from the final syllable's
    /// character count. The sweep overlay renders <see cref="WordTiming.SweepText"/>
    /// (trailing space stripped), so counting that space would stretch the syllable
    /// weights over glyphs that are never painted. Only the last syllable can carry
    /// it — an inner one ending in whitespace would not have been joinable.
    /// </summary>
    private static List<WordSyllable> TrimTrailingSpace(List<WordSyllable> segments, string mergedText)
    {
        var trailing = mergedText.Length - mergedText.TrimEnd().Length;
        if (trailing == 0) return segments;

        var last = segments[^1];
        segments[^1] = last with { Length = Math.Max(0, last.Length - trailing) };
        return segments;
    }

    // Letters/digits/apostrophes/hyphens below the CJK blocks join into one word;
    // whitespace, punctuation and CJK characters keep segments separate.
    private static bool IsJoinable(char c) =>
        (char.IsLetterOrDigit(c) || c is '\'' or '’' or '-') && c < '⺀';

    /// <summary>
    /// Folds fully-parenthesized word-timed lines — the common ELRC convention for
    /// Apple Music background vocals / adlibs — into the preceding synced line's
    /// <see cref="LyricLine.BackgroundWords"/> layer, so they render as a smaller
    /// karaoke row under the main line instead of taking a full line slot.
    /// Call after the line list is sorted by timestamp. Conservative: lines with any
    /// parenthesis inside the outer pair (e.g. "(Hey) yeah (hey)") are left alone.
    /// </summary>
    public static void FoldBackgroundLines(List<LyricLine> lines)
    {
        LyricLine? lastMain = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (lastMain != null && IsBackgroundCandidate(line))
            {
                var words = line.Words!;
                var bgEnd = line.EndTimestamp ?? words[^1].End;
                AppendBackground(lastMain, words, bgEnd);
                lines.RemoveAt(i);
                i--;
                continue;
            }

            // Adlibs embedded inline — "late at night (I will wait)" — split the
            // parenthesized run(s) out of the main words into the background layer.
            if (line.HasWords)
                ExtractInlineBackground(line);

            if (line.Timestamp.HasValue && !line.IsIntroPlaceholder)
                lastMain = line;
        }
    }

    /// <summary>
    /// Adds a word-timed background vocal to <paramref name="target"/>, joining onto an
    /// existing one with a space seam. <paramref name="bgEnd"/> bounds the last word.
    /// A part that would grow the row past <see cref="MaxWordsPerLine"/> is dropped.
    /// </summary>
    public static void AppendBackground(LyricLine target, IReadOnlyList<WordTiming> words, TimeSpan? bgEnd)
    {
        if ((target.BackgroundWords?.Count ?? 0) + words.Count > MaxWordsPerLine)
            return;

        if (target.HasBackgroundWords)
        {
            // Joined adlib parts share one row; make sure the seam keeps a space
            // so the WrapPanel cells don't run together.
            var merged = new List<WordTiming>(target.BackgroundWords!);
            var seam = merged[^1];
            if (!seam.Text.EndsWith(' '))
                merged[^1] = new WordTiming { Text = seam.Text + " ", Start = seam.Start, End = seam.End };
            merged.AddRange(words);
            target.BackgroundEndTimestamp = bgEnd ?? target.BackgroundEndTimestamp;
            target.BackgroundWords = merged;
        }
        else
        {
            target.BackgroundEndTimestamp = bgEnd;
            target.BackgroundWords = new List<WordTiming>(words);
        }
    }

    /// <summary>
    /// Splits parenthesized word runs — "(word … word)" — out of a line's word list
    /// into its background layer. The line must keep at least one main word (fully
    /// parenthesized lines are the fold case instead). An unmatched open paren
    /// abandons the whole extraction so lop-sided text is never mangled.
    /// </summary>
    private static void ExtractInlineBackground(LyricLine line)
    {
        var words = line.Words!;
        List<WordTiming>? main = null;
        List<WordTiming>? bg = null;
        var inRun = false;

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var visible = w.Text.Trim();
            if (!inRun && visible.Length > 0 && visible[0] is '(' or '（')
            {
                if (main == null)
                {
                    main = new List<WordTiming>(words.Take(i));
                    bg = new List<WordTiming>();
                }
                inRun = true;
            }

            if (inRun)
            {
                bg!.Add(w);
                if (visible.Length > 0 && visible[^1] is ')' or '）')
                    inRun = false;
            }
            else
            {
                main?.Add(w);
            }
        }

        // No runs, an unmatched open paren, or nothing left for the main row → leave alone.
        if (main == null || inRun || main.Count == 0 || bg!.Count == 0)
            return;

        line.Words = main;
        AppendBackground(line, bg, bg[^1].End ?? line.EndTimestamp);
    }

    private static bool IsBackgroundCandidate(LyricLine line)
    {
        if (!line.HasWords || !line.Timestamp.HasValue) return false;
        var text = line.Text.Trim();
        if (text.Length < 2) return false;
        var open = text[0] is '(' or '（';
        var close = text[^1] is ')' or '）';
        if (!open || !close) return false;
        var inner = text[1..^1];
        return !inner.Any(c => c is '(' or ')' or '（' or '）');
    }

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
