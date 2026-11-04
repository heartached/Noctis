using System.Text.RegularExpressions;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Line-synced and enhanced (word-tag, "A2") LRC → <see cref="LyricLine"/>. Moved verbatim
/// from the desktop LyricsViewModel so the phone parses sidecars and embedded LRC with the
/// same rules: global [offset:], metadata tags skipped, "[bg: …]" background lines,
/// multi-timestamp lines, duet voice markers, background fold and a line cap.
/// </summary>
public static partial class LrcParser
{
    /// <summary>
    /// Upper bound on lines produced from one file. An oversized or hostile sidecar (a 1 MB
    /// .lrc, or one line carrying thousands of stacked [mm:ss.xx] tags, since each tag emits
    /// its own LyricLine) would otherwise build tens of thousands of controls in one UI pass.
    /// </summary>
    public const int MaxLines = 3000;

    private const string BgLinePrefix = "[bg:";

    /// <summary>True when <paramref name="text"/> carries at least one [mm:ss.xx] stamp.</summary>
    public static bool ContainsTimestamp(string? text) =>
        !string.IsNullOrEmpty(text) && LrcTimestampRegex().IsMatch(text);

    /// <summary>
    /// If the first synced lyric starts after 2 seconds, inserts a "…" placeholder
    /// at timestamp zero. This matches Apple Music's "waiting for lyrics" behavior
    /// during intros — the placeholder becomes the active line until the first
    /// real lyric is reached.
    /// </summary>
    public static void InsertIntroPlaceholderIfNeeded(List<LyricLine> lines)
    {
        var firstSynced = lines.FirstOrDefault(l => l.IsSynced);
        if (firstSynced?.Timestamp != null && firstSynced.Timestamp.Value.TotalSeconds > 2)
        {
            lines.Insert(0, new LyricLine
            {
                Timestamp = TimeSpan.Zero,
                Text = "...",
                IsIntroPlaceholder = true
            });
        }
    }

    /// <summary>
    /// Shortest instrumental gap (from the sung end of everything before it to the next
    /// line's start) that gets a dots row. Long enough that a breath between phrases never
    /// does (slow songs measured 4-5 s start-to-start with ~1 s of silence), short enough
    /// for a real instrumental break.
    /// </summary>
    public static readonly TimeSpan InterludeMinGap = TimeSpan.FromSeconds(6);

    /// <summary>The dots appear this long after the last line's sung end, so that line's
    /// final word finishes its sweep before the dots take the focus (a line without words
    /// activates <c>80 ms + half the line glide</c> early, see LyricsTimeline).</summary>
    public static readonly TimeSpan InterludeDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// Inserts a "..." placeholder (<see cref="LyricLine.IsInterlude"/>) into every gap of at
    /// least <see cref="InterludeMinGap"/> between the known sung end of the lines so far and
    /// the next synced line, as Apple Music and Joy Tunes show dots through an instrumental
    /// break. Only where an end is known (word-timed, ELRC end tags, TTML): a plain LRC line
    /// has only a start, so a long gap could just be a long sung line. Idempotent.
    /// </summary>
    public static void InsertInterludePlaceholders(List<LyricLine> lines)
    {
        TimeSpan? sungEnd = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsIntroPlaceholder || line.Timestamp is not { } start) continue;
            if (sungEnd is { } end && start - end >= InterludeMinGap
                && !(i > 0 && lines[i - 1].IsInterlude))
            {
                lines.Insert(i, new LyricLine
                {
                    Timestamp = end + InterludeDelay,
                    Text = "...",
                    IsIntroPlaceholder = true,
                    IsInterlude = true,
                });
                i++;
            }
            // Lines are sorted by start, not end: an earlier, longer line can still be
            // singing over this one, so the gap counts from the latest end so far. A line
            // with no known end leaves the gap unknown until a line with one comes along.
            var lineEnd = LyricsTimeline.SungEnd(line);
            sungEnd = lineEnd is { } e ? (sungEnd is { } prev && prev > e ? prev : e) : null;
        }
    }

    /// <summary>
    /// Splits a long lyric line into balanced halves at the word boundary closest to the midpoint.
    /// Recursively applies to each half if still too long. Produces clean, cinematic two-line wraps.
    /// </summary>
    public static string SoftWrap(string text, int maxWidth = 25)
    {
        // Strip exotic Unicode (NBSP, separators, zero-width, replacement) that render
        // as empty boxes; this is the common funnel for every displayed lyric line.
        text = LyricsTextHelper.CleanDisplayText(text);
        if (text.Length <= maxWidth) return text;

        // Find the space closest to the midpoint for two balanced halves
        var mid = text.Length / 2;
        int bestSpace = -1;
        var bestDist = int.MaxValue;

        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != ' ') continue;
            var dist = Math.Abs(i - mid);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestSpace = i;
            }
        }

        if (bestSpace <= 0) return text;

        // Single split only — never more than 2 lines per lyric
        var line1 = text[..bestSpace];
        var line2 = text[(bestSpace + 1)..];

        // If either half is still too long for the active font size, split it too
        if (line1.Length > maxWidth)
            line1 = SoftWrap(line1, maxWidth);
        if (line2.Length > maxWidth)
            line2 = SoftWrap(line2, maxWidth);

        return line1 + "\n" + line2;
    }

    /// <summary>Splits plain lyrics into display lines, bounded by <see cref="MaxLines"/>.</summary>
    public static string[] SplitPlain(string text)
    {
        var split = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        return split.Length <= MaxLines ? split : split[..MaxLines];
    }

    public static List<LyricLine> Parse(string content)
    {
        var lines = new List<LyricLine>();
        var rawLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var offsetMs = ParseLrcOffsetMilliseconds(rawLines);

        // "[bg: …]" lines attach to a main line instead of adding one, but still
        // spend the line budget so a file of them cannot run unbounded.
        var bgLines = 0;

        // Unwrapped display text per line: a same-timestamp companion becomes a
        // translation/romanization layer, which wraps itself (SoftWrap's "\n" would stay).
        var rawText = new Dictionary<LyricLine, string>();

        foreach (var rawLine in rawLines)
        {
            if (lines.Count + bgLines >= MaxLines) break;

            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Offset is handled once globally before parsing timestamps.
            if (OffsetTagRegex().IsMatch(trimmed))
                continue;

            // Skip metadata tags like [ar:Artist], [ti:Title], [al:Album], [offset:], [length:]
            if (MetadataTagRegex().IsMatch(trimmed))
                continue;

            // iTunes/Gramophone background vocal: "[bg: <t>word <t>word<t>]" — a line with
            // no [mm:ss.xx] stamp that belongs to the main line directly above it in the
            // file. Attach it here, in file order: the timestamp sort below would otherwise
            // push it (as an "unsynced" line) to the very end of the song, and its raw
            // "[bg: <00:36.938>(Ah, …]" text would render there as a lyric.
            if (trimmed.StartsWith(BgLinePrefix, StringComparison.Ordinal))
            {
                var lastMain = lines.LastOrDefault(l => l.Timestamp.HasValue);
                if (lastMain != null)
                    AttachBackgroundLine(lastMain, trimmed, offsetMs);
                bgLines++;
                continue;
            }

            // Extract all timestamps from the line
            var matches = LrcTimestampRegex().Matches(trimmed);
            if (matches.Count > 0)
            {
                // Get the text after all timestamps. For enhanced ("A2") LRC this
                // body carries inline <mm:ss.xx> word tags, which we split into
                // per-word karaoke timings and strip from the displayed text.
                var lastMatch = matches[^1];
                var body = trimmed[(lastMatch.Index + lastMatch.Length)..];

                // Duet voice marker ("v1:"/"v2:"/"v3:", Gramophone syntax) sits between
                // the timestamp block and any word tags; strip it before word parsing
                // so it never reaches display text.
                var (unvoiced, voice) = EnhancedLrcParser.StripVoiceMarker(body);
                // "[t]word <t>word" ELRC: the untagged first word is timed by the line stamp.
                if (matches.Count == 1 && ParseLrcTimestamp(matches[0].Value) is { } lineStamp)
                    unvoiced = EnhancedLrcParser.TagLeadingText(unvoiced, lineStamp);
                var (text, words) = EnhancedLrcParser.ParseLine(unvoiced);

                // Skip empty timestamp lines — LRC files often end with
                // [03:24.00] (no text) as an end marker. If parsed, this empty
                // line becomes the "active" line and deactivates the previous
                // real lyric, making lyrics appear to stop early.
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Word timings are absolute; attaching them to a multi-timestamp
                // (compressed) line would misalign the later occurrences, so only
                // carry word-level data when the line has a single timestamp.
                var lineWords = matches.Count == 1 ? words : null;

                // Create a LyricLine for each timestamp (handles multi-timestamp lines)
                foreach (Match match in matches)
                {
                    if (lines.Count + bgLines >= MaxLines) break;

                    var timestamp = ParseLrcTimestamp(match.Value);
                    if (timestamp.HasValue)
                    {
                        var adjusted = timestamp.Value + TimeSpan.FromMilliseconds(offsetMs);
                        if (adjusted < TimeSpan.Zero)
                            adjusted = TimeSpan.Zero;

                        var line = new LyricLine
                        {
                            Timestamp = adjusted,
                            Text = SoftWrap(text),
                            Voice = voice
                        };

                        if (lineWords != null)
                        {
                            var shifted = offsetMs == 0 ? lineWords : ShiftWords(lineWords, offsetMs);
                            // End before Words: the Words setter computes held-note
                            // emphasis, and the last word's span needs the line end.
                            line.EndTimestamp = shifted[^1].End;
                            line.Words = shifted;
                        }

                        lines.Add(line);
                        rawText[line] = text;
                    }
                }
            }
            else
            {
                // No timestamp — add as unsynced line
                lines.Add(new LyricLine { Text = SoftWrap(trimmed) });
            }
        }

        // Sort by timestamp for synced lyrics. Stable (OrderBy) so lines sharing a
        // timestamp — e.g. an adlib synced to the same instant as its main line —
        // keep their file order, which the background fold below relies on.
        var sorted = lines
            .OrderBy(l => l.Timestamp == null ? 1 : 0)
            .ThenBy(l => l.Timestamp ?? TimeSpan.Zero)
            .ToList();
        lines.Clear();
        lines.AddRange(sorted);

        // Lines sharing one timestamp (original + romaji + translation, GitHub #116) are
        // one entry; before the fold so an adlib at the same instant still folds.
        MergeSameTimestampLines(lines, rawText);

        // Fold parenthesized adlib lines into the preceding line's background layer
        // (Apple Music-style background vocals).
        EnhancedLrcParser.FoldBackgroundLines(lines);

        // Backfill line EndTimestamp and last word End for synced lines so word emphasis and held notes
        // compute accurate durations instead of stalling at 0ms.
        for (int idx = 0; idx < lines.Count; idx++)
        {
            var l = lines[idx];
            if (!l.IsSynced) continue;
            if (l.EndTimestamp == null)
            {
                TimeSpan? nextStart = null;
                for (int nextIdx = idx + 1; nextIdx < lines.Count; nextIdx++)
                {
                    if (lines[nextIdx].Timestamp.HasValue && lines[nextIdx].Timestamp > l.Timestamp)
                    {
                        nextStart = lines[nextIdx].Timestamp;
                        break;
                    }
                }
                var maxHold = (l.Words is { Count: > 0 } lw ? lw[^1].Start : l.Timestamp.Value) + TimeSpan.FromSeconds(3);
                l.EndTimestamp = nextStart.HasValue && nextStart.Value < maxHold ? nextStart.Value : maxHold;
            }

            if (l.Words is { Count: > 0 } words && words[^1].End == null)
            {
                var copy = new List<WordTiming>(words);
                var last = copy[^1];
                copy[^1] = new WordTiming
                {
                    Text = last.Text,
                    Start = last.Start,
                    End = l.EndTimestamp ?? (last.Start + TimeSpan.FromSeconds(2)),
                    Syllables = last.Syllables,
                };
                l.Words = copy;
            }
        }

        return lines;
    }

    /// <summary>
    /// Folds the plain lines that share a timestamp with a line into that line (file order —
    /// the sort above is stable — so the first is the main line): they become its layers
    /// through <see cref="MapCompanionLines"/>. A companion is line-level text in the main
    /// line's voice; a word-timed line or one in another duet voice at the same instant is
    /// sung, not a layer, and stays its own line (overlapping vocals, a duet, Studio lines
    /// clamped to 0:00); a word-timed fully parenthesized line is still an adlib for
    /// <see cref="EnhancedLrcParser.FoldBackgroundLines"/>. A companion's "[bg: …]" vocal
    /// moves onto the main line; one repeating text already in the entry is dropped.
    /// </summary>
    private static void MergeSameTimestampLines(List<LyricLine> lines, Dictionary<LyricLine, string> rawText)
    {
        var merged = new List<LyricLine>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var end = i + 1;
            while (end < lines.Count && lines[i].Timestamp.HasValue && lines[end].Timestamp == lines[i].Timestamp)
                end++;
            if (end - i == 1)
            {
                merged.Add(lines[i++]);
                continue;
            }
            var pending = lines.GetRange(i, end - i);
            i = end;

            // Each pass takes the first remaining line as an entry and pulls its companions
            // out; sung lines left behind (another voice, word timing) start their own entry.
            while (pending.Count > 0)
            {
                var main = pending[0];
                pending.RemoveAt(0);
                merged.Add(main);
                // An adlib folds into the line before it and takes no layers itself.
                if (EnhancedLrcParser.IsBackgroundCandidate(main)) continue;

                var seen = new HashSet<string>(StringComparer.Ordinal) { Raw(main) };
                List<string>? companions = null;
                for (var k = 0; k < pending.Count; k++)
                {
                    var other = pending[k];
                    if (other.HasWords || other.Voice != main.Voice) continue;
                    pending.RemoveAt(k--);
                    if (other.HasBackgroundWords)
                        EnhancedLrcParser.AppendBackground(main, other.BackgroundWords!, other.BackgroundEndTimestamp);
                    var text = Raw(other);
                    if (seen.Add(text))
                        (companions ??= new List<string>()).Add(text);
                }

                if (companions != null)
                    (main.Transliteration, main.Translation) = MapCompanionLines(companions);
            }
        }

        lines.Clear();
        lines.AddRange(merged);

        string Raw(LyricLine l) =>
            LyricsTextHelper.CleanDisplayText(rawText.TryGetValue(l, out var raw) ? raw : l.Text).Trim();
    }

    /// <summary>
    /// How the extra lines of a same-timestamp LRC group map onto the display layers
    /// (GitHub #116), in file order: one extra line is the translation; two are the
    /// romanization then the translation (original, romaji, translation — the common
    /// layout); more than two keep the second as the romanization and stack the rest,
    /// one per row, as the translation so no text is dropped.
    /// </summary>
    public static (string? Transliteration, string? Translation) MapCompanionLines(IReadOnlyList<string> companions) =>
        companions.Count switch
        {
            0 => (null, null),
            1 => (null, companions[0]),
            _ => (companions[0], string.Join("\n", companions.Skip(1))),
        };

    /// <summary>
    /// The inverse of <see cref="MapCompanionLines"/>: the extra lines to write under a
    /// line's own at the same timestamp when it is serialized back to LRC, so its
    /// romanization and translation survive the round trip.
    /// </summary>
    public static List<string> CompanionLines(LyricLine line)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(line.Transliteration))
            result.Add(line.Transliteration.Trim());
        if (!string.IsNullOrWhiteSpace(line.Translation))
            result.AddRange(line.Translation.Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0));
        return result;
    }

    /// <summary>
    /// Parses a "[bg: …]" line body (prefix and closing bracket stripped) into the
    /// preceding main line's background layer. A body without word tags becomes one
    /// word starting at the main line's own timestamp so it still renders.
    /// </summary>
    private static void AttachBackgroundLine(LyricLine target, string trimmed, int offsetMs)
    {
        var body = trimmed[BgLinePrefix.Length..];
        if (body.EndsWith(']')) body = body[..^1];

        var (text, words) = EnhancedLrcParser.ParseLine(body);
        if (string.IsNullOrWhiteSpace(text)) return;

        List<WordTiming> bg = words != null
            ? (offsetMs == 0 ? words : ShiftWords(words, offsetMs))
            : [new WordTiming { Text = text, Start = target.Timestamp!.Value }];

        EnhancedLrcParser.AppendBackground(target, bg, bg[^1].End);
    }

    /// <summary>Applies the global LRC offset to absolute word timings, syllables included.</summary>
    private static List<WordTiming> ShiftWords(List<WordTiming> words, int offsetMs)
    {
        var delta = TimeSpan.FromMilliseconds(offsetMs);
        var shifted = new List<WordTiming>(words.Count);
        foreach (var w in words)
        {
            shifted.Add(new WordTiming
            {
                Text = w.Text,
                Start = Shift(w.Start),
                End = w.End.HasValue ? Shift(w.End.Value) : null,
                // Merged words keep their syllables' own clocks; dropping them made the
                // sweep cross a multi-syllable word at one even pace under an offset.
                Syllables = w.Syllables?.Select(s => s with
                {
                    Start = Shift(s.Start),
                    End = s.End.HasValue ? Shift(s.End.Value) : null,
                }).ToList(),
            });
        }
        return shifted;

        TimeSpan Shift(TimeSpan t) => t + delta < TimeSpan.Zero ? TimeSpan.Zero : t + delta;
    }

    /// <summary>The file's global [offset:±ms] (the first one), 0 when none: added to every line and word time.</summary>
    internal static int ParseLrcOffsetMilliseconds(string[] rawLines)
    {
        foreach (var rawLine in rawLines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var match = OffsetTagRegex().Match(trimmed);
            if (match.Success &&
                int.TryParse(match.Groups["offset"].Value, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    /// <summary>
    /// Parses a single LRC timestamp like [01:23.45] or [01:23] into a TimeSpan.
    /// </summary>
    private static TimeSpan? ParseLrcTimestamp(string timestamp)
    {
        // Remove brackets
        var inner = timestamp.Trim('[', ']').Replace(',', '.');
        var parts = inner.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return null;

        if (!int.TryParse(parts[0], out var minutes)) return null;

        if (parts.Length == 2)
        {
            // Seconds can be "23.45", "23,45", or "23"
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return null;

            return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }

        // Supports mm:ss:ff and mm:ss:fff variants.
        if (!int.TryParse(parts[1], out var wholeSeconds)) return null;
        if (!int.TryParse(parts[2], out var fractionalUnit)) return null;

        var divisor = Math.Pow(10, parts[2].Length);
        var fractionalSeconds = fractionalUnit / divisor;
        return TimeSpan.FromMinutes(minutes) +
               TimeSpan.FromSeconds(wholeSeconds + fractionalSeconds);
    }

    // Seconds may run past 59 ("[00:168.80]" = 2:48.80): some taggers never roll them into minutes,
    // and with exactly two digits every line from 1:40 on was dropped (121 library files, 4,349 lines).
    [GeneratedRegex(@"\[\d{1,3}:\d{2,3}(?:[.:]\d{1,3})?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"^\[(ar|ti|al|by|offset|re|ve|length|id):")]
    private static partial Regex MetadataTagRegex();

    [GeneratedRegex(@"^\[offset:(?<offset>[+-]?\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTagRegex();
}
