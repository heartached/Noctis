using System.Globalization;
using System.Text;

namespace Noctis.Services.LyricsStudio;

/// <summary>A word the speech model heard, with its time span and confidence (0–1).</summary>
public sealed record RecognizedWord(string Text, TimeSpan Start, TimeSpan End, float Probability);

/// <summary>A lyric word with the time it is sung.</summary>
public sealed record AlignedWord(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// One lyric line placed on the timeline. <see cref="Confidence"/> is the share of the
/// line's words that were actually heard (0–1); <see cref="Interpolated"/> lines had no
/// anchor at all and were placed between their neighbours.
/// </summary>
public sealed record AlignedLine(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    IReadOnlyList<AlignedWord> Words,
    double Confidence,
    bool Interpolated);

/// <summary>
/// Places known lyric lines onto the timeline of what the speech model heard: a monotonic
/// sequence alignment (Needleman–Wunsch with fuzzy word similarity) turns recognised words
/// into anchors; words the model missed are spread between anchors, whole lines it missed
/// are spread between neighbouring lines. Pure and deterministic.
/// </summary>
public static class LyricsAligner
{
    private const double GapPenalty = -0.7;
    private const double AnchorThreshold = 0.45;
    private const double DefaultSecondsPerWord = 0.42;
    private static readonly TimeSpan MinLineGap = TimeSpan.FromMilliseconds(10);

    public static IReadOnlyList<AlignedLine> Align(
        IReadOnlyList<string> lines,
        IReadOnlyList<RecognizedWord> recognized,
        TimeSpan? totalDuration = null)
    {
        var cleanLines = lines.Select(l => (l ?? string.Empty).Trim()).Where(l => l.Length > 0).ToList();
        if (cleanLines.Count == 0) return Array.Empty<AlignedLine>();

        // Lyric tokens, flattened with their line/word index; normalised form drives matching.
        var lyricTokens = new List<(int Line, int Word, string Raw, string Norm)>();
        var lineWords = new List<string[]>();
        for (var li = 0; li < cleanLines.Count; li++)
        {
            var words = cleanLines[li].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            lineWords.Add(words);
            for (var wi = 0; wi < words.Length; wi++)
                lyricTokens.Add((li, wi, words[wi], Normalize(words[wi])));
        }
        var alignable = lyricTokens.Select((t, idx) => (t, idx)).Where(x => x.t.Norm.Length > 0).ToList();

        var heard = recognized
            .Where(w => w is not null && w.End >= w.Start)
            .Select(w => (Word: w, Norm: Normalize(w.Text)))
            .Where(x => x.Norm.Length > 0)
            .OrderBy(x => x.Word.Start)
            .ToList();

        // token index → recognised word (anchor)
        var anchors = new Dictionary<int, (RecognizedWord Word, double Sim)>();
        if (alignable.Count > 0 && heard.Count > 0)
            foreach (var (tokenIdx, wordIdx, sim) in AlignSequences(alignable.Select(a => a.t.Norm).ToList(), heard.Select(h => h.Norm).ToList()))
                anchors[alignable[tokenIdx].idx] = (heard[wordIdx].Word, sim);

        // Per line: anchored words, then interpolate the rest.
        var result = new AlignedLine?[cleanLines.Count];
        var tokenOffset = 0;
        for (var li = 0; li < cleanLines.Count; li++)
        {
            var words = lineWords[li];
            var lineAnchors = new (RecognizedWord Word, double Sim)?[words.Length];
            var simSum = 0.0;
            var anchorCount = 0;
            for (var wi = 0; wi < words.Length; wi++)
            {
                if (anchors.TryGetValue(tokenOffset + wi, out var a))
                {
                    lineAnchors[wi] = a;
                    simSum += a.Sim;
                    anchorCount++;
                }
            }
            tokenOffset += words.Length;
            if (anchorCount == 0) continue; // placed in the second pass

            var timed = InterpolateWords(words, lineAnchors);
            var confidence = words.Length == 0 ? 0 : Math.Clamp(simSum / words.Length, 0, 1);
            result[li] = new AlignedLine(cleanLines[li], timed[0].Start, timed[^1].End, timed, confidence, Interpolated: false);
        }

        FillUnanchoredLines(result, lineWords, totalDuration, heard.Count > 0 ? heard[^1].Word.End : (TimeSpan?)null);
        EnforceMonotonic(result);
        return result.Select(r => r!).ToList();
    }

    // ── Sequence alignment ────────────────────────────────────────────────────

    /// <summary>Returns (lyricIndex, heardIndex, similarity) pairs on the best monotonic path.</summary>
    internal static List<(int Lyric, int Heard, double Sim)> AlignSequences(IReadOnlyList<string> lyric, IReadOnlyList<string> heard)
    {
        var n = lyric.Count;
        var m = heard.Count;
        var score = new double[n + 1, m + 1];
        var move = new byte[n + 1, m + 1]; // 1 = diag, 2 = up (skip lyric), 3 = left (skip heard)
        for (var i = 1; i <= n; i++) { score[i, 0] = i * GapPenalty; move[i, 0] = 2; }
        for (var j = 1; j <= m; j++) { score[0, j] = j * GapPenalty; move[0, j] = 3; }

        var sims = new double[n, m];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var sim = Similarity(lyric[i - 1], heard[j - 1]);
                sims[i - 1, j - 1] = sim;
                var matchScore = sim >= 0.8 ? 2.0 + sim
                    : sim >= 0.6 ? 1.0
                    : sim >= AnchorThreshold ? 0.2
                    : -1.5;
                var diag = score[i - 1, j - 1] + matchScore;
                var up = score[i - 1, j] + GapPenalty;
                var left = score[i, j - 1] + GapPenalty;
                if (diag >= up && diag >= left) { score[i, j] = diag; move[i, j] = 1; }
                else if (up >= left) { score[i, j] = up; move[i, j] = 2; }
                else { score[i, j] = left; move[i, j] = 3; }
            }
        }

        var pairs = new List<(int, int, double)>();
        int ci = n, cj = m;
        while (ci > 0 || cj > 0)
        {
            var mv = move[ci, cj];
            if (mv == 1)
            {
                var sim = sims[ci - 1, cj - 1];
                if (sim >= AnchorThreshold) pairs.Add((ci - 1, cj - 1, sim));
                ci--; cj--;
            }
            else if (mv == 2) ci--;
            else cj--;
        }
        pairs.Reverse();
        return pairs;
    }

    // ── Word / line interpolation ─────────────────────────────────────────────

    private static List<AlignedWord> InterpolateWords(string[] words, (RecognizedWord Word, double Sim)?[] anchors)
    {
        var count = words.Length;
        var starts = new TimeSpan?[count];
        var ends = new TimeSpan?[count];
        for (var i = 0; i < count; i++)
        {
            if (anchors[i] is { } a)
            {
                starts[i] = a.Word.Start;
                ends[i] = a.Word.End > a.Word.Start ? a.Word.End : a.Word.Start + TimeSpan.FromMilliseconds(120);
            }
        }

        // Leading unanchored words: back off from the first anchor.
        var first = Array.FindIndex(starts, s => s.HasValue);
        var last = Array.FindLastIndex(starts, s => s.HasValue);
        var perWord = TimeSpan.FromSeconds(DefaultSecondsPerWord);
        for (var i = first - 1; i >= 0; i--)
        {
            ends[i] = starts[i + 1];
            var s = ends[i]!.Value - perWord;
            starts[i] = s < TimeSpan.Zero ? TimeSpan.Zero : s;
        }
        // Trailing unanchored words: run on from the last anchor.
        for (var i = last + 1; i < count; i++)
        {
            starts[i] = ends[i - 1];
            ends[i] = starts[i]!.Value + perWord;
        }
        // Interior gaps: spread evenly between the surrounding anchors.
        var i0 = first;
        while (i0 < last)
        {
            var next = Array.FindIndex(starts, i0 + 1, s => s.HasValue);
            var gap = next - i0 - 1;
            if (gap > 0)
            {
                var from = ends[i0]!.Value;
                var to = starts[next]!.Value;
                if (to < from) to = from;
                var slice = (to - from) / (gap + 0);
                for (var k = 1; k <= gap; k++)
                {
                    starts[i0 + k] = from + slice * (k - 1);
                    ends[i0 + k] = from + slice * k;
                }
            }
            i0 = next;
        }

        var list = new List<AlignedWord>(count);
        for (var i = 0; i < count; i++)
        {
            var s = starts[i] ?? TimeSpan.Zero;
            var e = ends[i] ?? s;
            if (e < s) e = s;
            list.Add(new AlignedWord(words[i], s, e));
        }
        // Words never overlap their successor.
        for (var i = 0; i + 1 < list.Count; i++)
            if (list[i].End > list[i + 1].Start) list[i] = list[i] with { End = list[i + 1].Start };
        return list;
    }

    private static void FillUnanchoredLines(AlignedLine?[] result, List<string[]> lineWords, TimeSpan? totalDuration, TimeSpan? lastHeardEnd)
    {
        var n = result.Length;
        var i = 0;
        while (i < n)
        {
            if (result[i] is not null) { i++; continue; }
            var j = i;
            while (j < n && result[j] is null) j++;
            // Unanchored block [i, j)
            var prevEnd = i > 0 ? result[i - 1]!.End : (TimeSpan?)null;
            var nextStart = j < n ? result[j]!.Start : (TimeSpan?)null;
            var blockWords = 0;
            for (var k = i; k < j; k++) blockWords += Math.Max(1, lineWords[k].Length);

            TimeSpan from, to;
            var perWord = TimeSpan.FromSeconds(DefaultSecondsPerWord);
            if (prevEnd.HasValue && nextStart.HasValue)
            {
                from = prevEnd.Value;
                to = nextStart.Value > from ? nextStart.Value : from;
            }
            else if (nextStart.HasValue)
            {
                to = nextStart.Value;
                var span = perWord * blockWords;
                from = to - span < TimeSpan.Zero ? TimeSpan.Zero : to - span;
            }
            else if (prevEnd.HasValue)
            {
                from = prevEnd.Value;
                var end = totalDuration ?? lastHeardEnd;
                to = end.HasValue && end.Value > from ? end.Value : from + perWord * blockWords;
            }
            else
            {
                // Nothing heard at all: spread every line across the track (or a default pace).
                from = TimeSpan.Zero;
                to = totalDuration ?? lastHeardEnd ?? perWord * blockWords;
            }

            var cursor = from;
            var totalSpan = to - from;
            for (var k = i; k < j; k++)
            {
                var words = lineWords[k];
                var share = blockWords == 0 ? totalSpan : totalSpan * Math.Max(1, words.Length) / blockWords;
                var lineStart = cursor;
                var lineEnd = cursor + share;
                var timed = new List<AlignedWord>(words.Length);
                var wordShare = words.Length == 0 ? share : share / words.Length;
                for (var w = 0; w < words.Length; w++)
                    timed.Add(new AlignedWord(words[w], lineStart + wordShare * w, lineStart + wordShare * (w + 1)));
                result[k] = new AlignedLine(string.Join(' ', words), lineStart, lineEnd, timed, 0, Interpolated: true);
                cursor = lineEnd;
            }
            i = j;
        }
    }

    private static void EnforceMonotonic(AlignedLine?[] result)
    {
        for (var i = 1; i < result.Length; i++)
        {
            var prev = result[i - 1]!;
            var cur = result[i]!;
            var minStart = prev.Start + MinLineGap;
            if (cur.Start < minStart)
            {
                var shift = minStart - cur.Start;
                var words = cur.Words.Select(w => new AlignedWord(w.Text, w.Start + shift, w.End + shift)).ToList();
                result[i] = cur with { Start = minStart, End = cur.End + shift < minStart ? minStart : cur.End + shift, Words = words };
            }
        }
    }

    // ── Text normalisation & similarity ───────────────────────────────────────

    /// <summary>Lower-case letters/digits only, diacritics folded, so "Héllo," and "hello" match.</summary>
    internal static string Normalize(string? word)
    {
        if (string.IsNullOrEmpty(word)) return string.Empty;
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>1 − normalised Levenshtein distance, with a small bonus for a shared prefix (sung words often trail off).</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var max = Math.Max(a.Length, b.Length);
        var dist = Levenshtein(a, b);
        var sim = 1.0 - (double)dist / max;
        if (max >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)))
            sim = Math.Max(sim, 0.75);
        return Math.Clamp(sim, 0, 1);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>
/// Turns a bare transcript into lyric lines when the track has no lyrics to align against:
/// breaks on long pauses, sentence punctuation and a maximum line length.
/// </summary>
public static class TranscriptLines
{
    public static IReadOnlyList<AlignedLine> Group(
        IReadOnlyList<RecognizedWord> words,
        int maxWordsPerLine = 8,
        TimeSpan? gapBreak = null)
    {
        var gap = gapBreak ?? TimeSpan.FromSeconds(1.0);
        maxWordsPerLine = Math.Clamp(maxWordsPerLine, 2, 32);
        var lines = new List<AlignedLine>();
        var current = new List<RecognizedWord>();

        void Flush()
        {
            if (current.Count == 0) return;
            var text = string.Join(' ', current.Select(w => w.Text.Trim()).Where(t => t.Length > 0));
            if (text.Length > 0)
            {
                var timed = current.Select(w => new AlignedWord(w.Text.Trim(), w.Start, w.End)).ToList();
                var confidence = current.Average(w => Math.Clamp(w.Probability, 0f, 1f));
                lines.Add(new AlignedLine(text, current[0].Start, current[^1].End, timed, confidence, Interpolated: false));
            }
            current.Clear();
        }

        foreach (var w in words.Where(w => w is not null && !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Start))
        {
            if (current.Count > 0)
            {
                var prev = current[^1];
                var pause = w.Start - prev.End;
                var sentenceEnd = prev.Text.TrimEnd().EndsWith('.') || prev.Text.TrimEnd().EndsWith('?') || prev.Text.TrimEnd().EndsWith('!');
                if (current.Count >= maxWordsPerLine || pause > gap || (sentenceEnd && current.Count >= 3))
                    Flush();
            }
            current.Add(w);
        }
        Flush();
        return lines;
    }
}
