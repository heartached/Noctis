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
/// <remarks>
/// The alignment alone maximises matched words, so an ad-lib "yeah", a chorus sung five times
/// or a Whisper loop can pull a whole line to the wrong place. Between the alignment and the
/// timing, anchors are therefore checked line by line (benchmark 09-24: 41 songs with
/// hand-made timings, Whisper Base and Medium, tune and held-out halves):
/// identical lines take the heard occurrences whose spacing fits the words sung in between;
/// words stamped in a DTW collapse, anchors far from the rest of their line and lines held
/// only by isolated common words ("yeah", "the") do not time a line; and a line whose
/// anchors leave the unheard lines around it no time to be sung is let go and interpolated.
/// A second pass then re-aligns with a small preference for matches near where each line's
/// neighbours from the first pass put it, which settles near-ties between two heard copies.
/// </remarks>
public static class LyricsAligner
{
    private const double GapPenalty = -0.7;
    private const double AnchorThreshold = 0.45;
    private const double DefaultSecondsPerWord = 0.42;
    private static readonly TimeSpan MinLineGap = TimeSpan.FromMilliseconds(10);

    /// <summary>Consecutive heard words starting closer together than this are a DTW collapse, not speech.</summary>
    private static readonly TimeSpan CollapseStep = TimeSpan.FromMilliseconds(60);
    private const int CollapseRun = 3;

    /// <summary>Two anchors of one line further apart than this (plus <see cref="SplitPerWord"/> per word between) belong to different sung lines.</summary>
    private const double SplitSlackSeconds = 3.0;
    private const double SplitPerWord = 1.2;

    /// <summary>Fastest plausible singing, seconds per word, when checking that unheard lines still fit between anchored ones.</summary>
    private const double MinSecondsPerWord = 0.2;

    /// <summary>A heard word, normalised, with its index in the time-ordered list; <see cref="Unreliable"/>: its start time is not to be trusted.</summary>
    private sealed class Heard
    {
        public required RecognizedWord Word { get; init; }
        public required string Norm { get; init; }
        public required int Index { get; init; }
        public bool Unreliable { get; set; }
    }

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
        var heard = PrepareHeard(recognized);

        // Two passes: the second breaks near-ties between equally good matches in favour of
        // the one nearest where the first pass's neighbours put the line.
        var first = AlignPass(cleanLines, lineWords, lyricTokens, heard, totalDuration, expected: null);
        return LineWindows.Fit(AlignPass(cleanLines, lineWords, lyricTokens, heard, totalDuration, ExpectedTokenTimes(first, lineWords)), totalDuration);
    }

    /// <summary>
    /// Where each lyric token would be sung judging only by the lines around it: its line is
    /// interpolated (by word count) between the nearest anchored lines before and after it,
    /// words 0.3 s apart. NaN for lines whose text occurs more than once — which copy takes
    /// which occurrence is <see cref="ReassignRepeatedLines"/>' job. KICK OUT (benchmark): a
    /// line heard twice, at its place and again 5.5 s later over another line, took the later
    /// copy and was 5.6 s late; the expectation picks the first.
    /// </summary>
    private static double[] ExpectedTokenTimes(IReadOnlyList<AlignedLine> first, List<string[]> lineWords)
    {
        var n = first.Count;
        var keys = lineWords.Select(w => string.Join(' ', w.Select(Normalize).Where(x => x.Length > 0))).ToArray();
        var copies = keys.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var wordsOf = lineWords.Select(w => Math.Max(1, w.Length)).ToArray();
        var expected = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var p = i - 1;
            while (p >= 0 && first[p].Interpolated) p--;
            var q = i + 1;
            while (q < n && first[q].Interpolated) q++;
            var e = double.NaN;
            if (copies[keys[i]] == 1)
            {
                if (p >= 0 && q < n)
                {
                    double before = 0, all = 0;
                    for (var k = p + 1; k < q; k++) { all += wordsOf[k]; if (k < i) before += wordsOf[k]; }
                    var from = first[p].End.TotalSeconds;
                    e = from + (first[q].Start.TotalSeconds - from) * (all > 0 ? before / all : 0);
                }
                else if (q < n)
                {
                    double words = 0;
                    for (var k = i; k < q; k++) words += wordsOf[k];
                    e = first[q].Start.TotalSeconds - words * DefaultSecondsPerWord;
                }
                else if (p >= 0)
                {
                    double words = 0;
                    for (var k = p + 1; k < i; k++) words += wordsOf[k];
                    e = first[p].End.TotalSeconds + words * DefaultSecondsPerWord;
                }
            }
            for (var k = 0; k < lineWords[i].Length; k++) expected.Add(e + k * 0.3);
        }
        return expected.ToArray();
    }

    /// <summary>Score lost per second a match lies from its expected time (beyond 1 s, at most 10 s): a tie-breaker, not a pull.</summary>
    private const double ExpectedTimePrior = 0.2;

    private static IReadOnlyList<AlignedLine> AlignPass(
        List<string> cleanLines,
        List<string[]> lineWords,
        List<(int Line, int Word, string Raw, string Norm)> lyricTokens,
        List<Heard> heard,
        TimeSpan? totalDuration,
        double[]? expected)
    {
        var alignable = lyricTokens.Select((t, idx) => (t, idx)).Where(x => x.t.Norm.Length > 0).ToList();

        // token index → heard word (anchor)
        var anchors = new Dictionary<int, (Heard H, double Sim)>();
        if (alignable.Count > 0 && heard.Count > 0)
        {
            Func<int, int, double>? bias = null;
            if (expected is not null)
                bias = (i, j) =>
                {
                    var e = expected[alignable[i].idx];
                    return double.IsNaN(e) ? 0 : -ExpectedTimePrior * Math.Min(10, Math.Max(0, Math.Abs(heard[j].Word.Start.TotalSeconds - e) - 1));
                };
            foreach (var (tokenIdx, wordIdx, sim) in AlignSequences(alignable.Select(a => a.t.Norm).ToList(), heard.Select(h => h.Norm).ToList(), bias))
                anchors[alignable[tokenIdx].idx] = (heard[wordIdx], sim);
        }
        ReassignRepeatedLines(lineWords, anchors);

        // Per line: the anchors that may time it.
        var kept = new (Heard H, double Sim)?[cleanLines.Count][];
        var evidence = new double[cleanLines.Count];
        var tokenOffset = 0;
        for (var li = 0; li < cleanLines.Count; li++)
        {
            var words = lineWords[li];
            var raw = new (Heard H, double Sim)?[words.Length];
            for (var wi = 0; wi < words.Length; wi++)
                if (anchors.TryGetValue(tokenOffset + wi, out var a)) raw[wi] = a;
            tokenOffset += words.Length;
            kept[li] = FilterLineAnchors(words, raw, dropWeakLine: true, out evidence[li]);
        }
        KeepLinesThatFit(lineWords, kept, evidence, totalDuration);

        // Anchored lines: anchored words, then interpolate the rest.
        var result = new AlignedLine?[cleanLines.Count];
        var timing = new ((RecognizedWord Word, double Sim)?[] Anchors, TimeSpan?[] PullBack)?[cleanLines.Count];
        // Words the model missed before a line's first heard word (and after its last) are
        // spread at the song's own pace: at a fixed 0.42 s/word a rap line whose first two
        // words were missed started ~0.4 s early (benchmark 09-29, dev: lines > 1 s off 83 → 70).
        var pace = AnchorPace(kept);
        for (var li = 0; li < cleanLines.Count; li++)
        {
            var words = lineWords[li];
            var lineAnchors = new (RecognizedWord Word, double Sim)?[words.Length];
            var simSum = 0.0;
            var anchorCount = 0;
            for (var wi = 0; wi < words.Length; wi++)
            {
                if (kept[li][wi] is not { } k) continue;
                lineAnchors[wi] = (k.H.Word, k.Sim);
                simSum += k.Sim;
                anchorCount++;
            }
            if (anchorCount == 0) continue; // placed by FillUnanchoredLines

            var pullBack = PullBackStarts(kept[li], heard);
            var timed = InterpolateWords(words, lineAnchors, knownStart: null, pullBack, pace: pace);
            var confidence = words.Length == 0 ? 0 : Math.Clamp(simSum / words.Length, 0, 1);
            result[li] = new AlignedLine(cleanLines[li], timed[0].Start, timed[^1].End, timed, confidence, Interpolated: false);
            timing[li] = (lineAnchors, pullBack);
        }

        FillUnanchoredLines(result, lineWords, totalDuration, heard.Count > 0 ? heard[^1].Word.End : (TimeSpan?)null);
        var placed = result.Select(r => r!.Start).ToArray();
        EnforceMonotonic(result);

        // Words the model missed after a line's last heard word run on at a default pace; where
        // that reaches the next line, they share the room up to its start instead (line starts
        // stay as placed; lines EnforceMonotonic moved are left to LineWindows.Fit). Final pass
        // only: the first pass's line ends feed ExpectedTokenTimes as they were tuned.
        for (var li = 0; expected is not null && li + 1 < cleanLines.Count; li++)
        {
            if (timing[li] is not { } t || result[li] is not { } line || line.Start != placed[li]) continue;
            var next = result[li + 1]!.Start;
            if (line.End <= next || next <= line.Start) continue;
            var timed = InterpolateWords(lineWords[li], t.Anchors, knownStart: null, t.PullBack, knownEnd: next, pace: pace);
            result[li] = line with { Start = timed[0].Start, End = timed[^1].End, Words = timed };
        }
        return result.Select(r => r!).ToList();
    }

    /// <summary>
    /// Alignment when each line's start is already known (upgrading a line-level LRC): every
    /// line is matched only against the words heard inside its own window — from its start
    /// to the next line's start, with a little padding — so a repeated chorus cannot be
    /// pulled to the wrong verse, and a line the model missed is spread inside its window
    /// instead of between whatever neighbours happened to anchor. The file's stamp stays the
    /// line's start unless its first word was heard close to it: words before the first heard
    /// one are spread from the stamp, and within the window a match near where the word should
    /// fall wins over an equally good one further away.
    /// </summary>
    public static IReadOnlyList<AlignedLine> AlignWithinLines(
        IReadOnlyList<string> lines,
        IReadOnlyList<TimeSpan> lineStarts,
        IReadOnlyList<RecognizedWord> recognized,
        TimeSpan? totalDuration = null)
    {
        if (lineStarts.Count != lines.Count) return Align(lines, recognized, totalDuration);
        var pad = TimeSpan.FromMilliseconds(600);
        var perWord = TimeSpan.FromSeconds(DefaultSecondsPerWord);
        var heard = PrepareHeard(recognized);

        var result = new AlignedLine?[lines.Count];
        var timing = new (string[] Words, (RecognizedWord Word, double Sim)?[] Anchors, TimeSpan?[] PullBack)?[lines.Count];
        for (var li = 0; li < lines.Count; li++)
        {
            var text = (lines[li] ?? string.Empty).Trim();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var start = lineStarts[li];
            var nextStart = li + 1 < lines.Count ? lineStarts[li + 1] : (totalDuration is { } d && d > start ? d : start + perWord * Math.Max(1, words.Length));
            if (nextStart <= start) nextStart = start + perWord * Math.Max(1, words.Length);

            // Pad only backwards: a line may start a touch before its stamp, but anything at
            // or after the next stamp belongs to the next line (repeated choruses!).
            var lo = start - pad < TimeSpan.Zero ? TimeSpan.Zero : start - pad;
            var hi = nextStart;
            var window = heard.Where(h => h.Word.Start >= lo && h.Word.Start < hi).ToList();

            var raw = new (Heard H, double Sim)?[words.Length];
            if (words.Length > 0 && window.Count > 0)
            {
                var norms = words.Select(Normalize).ToList();
                // Where word k should fall: spread from the stamp at a natural pace (bounded by
                // the window). Deviating costs a little, so a repeated word ("I want it, I got
                // it") takes the occurrence at its own place instead of a later one.
                var span = Math.Min((nextStart - start).TotalSeconds, Math.Max(1, words.Length) * WithinLinePace);
                double Bias(int k, int j) => -WithinLineTimePrior * Math.Abs(window[j].Word.Start.TotalSeconds - (start.TotalSeconds + span * k / Math.Max(1, words.Length)));
                foreach (var (lyricIdx, heardIdx, sim) in AlignSequences(norms, window.Select(w => w.Norm).ToList(), Bias))
                {
                    if (norms[lyricIdx].Length == 0) continue;
                    raw[lyricIdx] = (window[heardIdx], sim);
                }
            }
            var kept = FilterLineAnchors(words, raw, dropWeakLine: true, out _);
            // The stamp times the line: a first-word anchor far from it is a wrong match or a
            // DTW slip, not a reason to move the line.
            if (kept.Length > 0 && kept[0] is { } first && (first.H.Word.Start - start).Duration() > StampTolerance)
                kept[0] = null;

            var lineAnchors = new (RecognizedWord Word, double Sim)?[words.Length];
            var simSum = 0.0;
            var anchorCount = 0;
            for (var wi = 0; wi < words.Length; wi++)
            {
                if (kept[wi] is not { } k) continue;
                lineAnchors[wi] = (k.H.Word, k.Sim);
                simSum += k.Sim;
                anchorCount++;
            }

            if (anchorCount > 0)
            {
                var pullBack = PullBackStarts(kept, heard);
                var timed = InterpolateWords(words, lineAnchors, knownStart: start, pullBack);
                var confidence = Math.Clamp(simSum / words.Length, 0, 1);
                result[li] = new AlignedLine(text, timed[0].Start, timed[^1].End, timed, confidence, Interpolated: false);
                timing[li] = (words, lineAnchors, pullBack);
            }
            else
            {
                // Nothing heard: keep the line where the file had it and spread the words at a
                // natural pace, at most up to the next line (not across a long instrumental after
                // it: benchmark 09-29, word starts p90 0.40 → 0.34 s held-out, 0.27 → 0.25 s dev).
                var lineSpan = nextStart - start;
                if (perWord * Math.Max(1, words.Length) < lineSpan) lineSpan = perWord * Math.Max(1, words.Length);
                var slice = words.Length == 0 ? lineSpan : lineSpan / Math.Max(1, words.Length);
                var timed = new List<AlignedWord>(words.Length);
                for (var w = 0; w < words.Length; w++)
                    timed.Add(new AlignedWord(words[w], start + slice * w, start + slice * (w + 1)));
                result[li] = new AlignedLine(text, start, timed.Count > 0 ? timed[^1].End : nextStart, timed, 0, Interpolated: true);
            }
        }

        var placed = result.Select(r => r!.Start).ToArray();
        EnforceMonotonic(result);

        // The next line may start before its stamp (its first word was heard early): words that
        // would run into it share the room up to its actual start — the unheard ones after the
        // line's last heard word, or every word of a line nothing was heard of.
        for (var li = 0; li + 1 < lines.Count; li++)
        {
            var line = result[li]!;
            var next = result[li + 1]!.Start;
            if (line.End <= next || next <= line.Start || line.Start != placed[li]) continue;
            if (timing[li] is { } t)
            {
                var timed = InterpolateWords(t.Words, t.Anchors, knownStart: lineStarts[li], t.PullBack, knownEnd: next);
                result[li] = line with { Start = timed[0].Start, End = timed[^1].End, Words = timed };
            }
            else if (line.Words.Count > 0)
            {
                var slice = (next - line.Start) / line.Words.Count;
                var spread = line.Words.Select((w, k) => new AlignedWord(w.Text, line.Start + slice * k, line.Start + slice * (k + 1))).ToList();
                result[li] = line with { End = spread[^1].End, Words = spread };
            }
        }
        return LineWindows.Fit(result.Select(r => r!).ToList(), totalDuration);
    }

    /// <summary>Seconds per word used to predict where a word of a stamped line falls.</summary>
    private const double WithinLinePace = 0.45;
    /// <summary>Score lost per second a stamped line's match lies from where its word should fall.</summary>
    private const double WithinLineTimePrior = 0.3;
    /// <summary>A stamped line's first word heard further than this from the stamp keeps the stamp.</summary>
    private static readonly TimeSpan StampTolerance = TimeSpan.FromSeconds(1);

    // ── Heard words ───────────────────────────────────────────────────────────

    /// <summary>
    /// Time-ordered, normalised heard words. Marks DTW collapses: a run of words stamped at
    /// (almost) the same instant — the alignment path jumped, so only the run's last word is
    /// roughly in place and the others' real times are unknown (earlier).
    /// </summary>
    private static List<Heard> PrepareHeard(IReadOnlyList<RecognizedWord> recognized)
    {
        var list = new List<Heard>(recognized.Count);
        foreach (var w in recognized.Where(w => w is not null && w.End >= w.Start).OrderBy(w => w.Start))
        {
            var norm = Normalize(w.Text);
            if (norm.Length > 0) list.Add(new Heard { Word = w, Norm = norm, Index = list.Count });
        }
        var i = 0;
        while (i < list.Count)
        {
            var j = i + 1;
            while (j < list.Count && list[j].Word.Start - list[j - 1].Word.Start <= CollapseStep) j++;
            if (j - i >= CollapseRun)
                for (var k = i; k < j - 1; k++) list[k].Unreliable = true;
            i = j;
        }
        return list;
    }

    // ── Line anchor checks ────────────────────────────────────────────────────

    /// <summary>
    /// The anchors that may time a line: drops words from a DTW collapse, then anchors far
    /// from the rest of the line (split into clusters where consecutive anchors are further
    /// apart than the words between them could take; the cluster with the most evidence
    /// stays). With <paramref name="dropWeakLine"/>, a line held only by isolated common words
    /// (no content word, no two words heard in a row, not every word heard) loses its anchors:
    /// a lone "yeah" is as likely an ad-lib elsewhere as this line's.
    /// </summary>
    private static (Heard H, double Sim)?[] FilterLineAnchors(string[] words, (Heard H, double Sim)?[] raw, bool dropWeakLine, out double evidence)
    {
        var kept = ((Heard H, double Sim)?[])raw.Clone();
        for (var wi = 0; wi < words.Length; wi++)
            if (kept[wi] is { } a && a.H.Unreliable) kept[wi] = null;

        var idx = Enumerable.Range(0, words.Length).Where(w => kept[w] is not null).ToList();
        if (idx.Count >= 2)
        {
            var clusters = new List<List<int>> { new() { idx[0] } };
            for (var k = 1; k < idx.Count; k++)
            {
                var gap = (kept[idx[k]]!.Value.H.Word.Start - kept[idx[k - 1]]!.Value.H.Word.End).TotalSeconds;
                if (gap > SplitSlackSeconds + SplitPerWord * (idx[k] - idx[k - 1] - 1)) clusters.Add(new());
                clusters[^1].Add(idx[k]);
            }
            if (clusters.Count > 1)
            {
                var best = clusters.OrderByDescending(c => c.Sum(w => Evidence(words[w], kept[w]!.Value.Sim))).ThenBy(c => c[0]).First();
                foreach (var c in clusters)
                    if (c != best)
                        foreach (var w in c) kept[w] = null;
            }
        }

        evidence = 0;
        for (var wi = 0; wi < words.Length; wi++)
            if (kept[wi] is { } a) evidence += Evidence(words[wi], a.Sim);

        if (dropWeakLine && kept.Any(k => k is not null))
        {
            var pinned = true;
            var content = false;
            var pair = false;
            for (var wi = 0; wi < words.Length; wi++)
            {
                if (kept[wi] is not { } a)
                {
                    if (Normalize(words[wi]).Length > 0) pinned = false;
                    continue;
                }
                if (Informativeness(Normalize(words[wi])) >= 1 && a.Sim >= 0.6) content = true;
                if (wi > 0 && kept[wi - 1] is { } b && b.H.Index == a.H.Index - 1 && a.Sim >= 0.8 && b.Sim >= 0.8) pair = true;
            }
            if (!content && !pair && !pinned)
                Array.Clear(kept);
        }
        return kept;
    }

    /// <summary>How much a matched word says about where its line is: its informativeness times the match quality.</summary>
    private static double Evidence(string lyricWord, double sim)
    {
        var quality = sim >= 0.8 ? sim : sim >= 0.6 ? 0.5 * sim : 0;
        return Informativeness(Normalize(lyricWord)) * quality;
    }

    /// <summary>
    /// Identical lyric lines (a hook sung five times) score the same whichever of them takes
    /// a heard occurrence, so the sequence alignment hands the occurrences out arbitrarily —
    /// in practice to the last of them (benchmark: three heard copies of a five-times hook went
    /// to copies 3–5, copies 1–2 were squeezed before them, 5–8 s off). Every matched line
    /// is an occurrence at a time; here an occurrence may move to another line with the same
    /// text, choosing (DP in time order) the assignment whose gaps best fit the number of words
    /// sung in between. A move must not make the singing implausibly fast, and the result must
    /// fit clearly better than the alignment's own choice.
    /// </summary>
    private static void ReassignRepeatedLines(List<string[]> lineWords, Dictionary<int, (Heard H, double Sim)> anchors)
    {
        const double LeadPerWord = 0.3;   // estimate of a line's start from its first heard word
        const double MinFit = 0.5;        // a moved occurrence may not come faster than half the expected time
        const double MinGain = 0.5;       // total log-fit improvement required to rewrite
        var n = lineWords.Count;
        var offset = new int[n + 1];
        for (var li = 0; li < n; li++) offset[li + 1] = offset[li] + lineWords[li].Length;
        var keys = lineWords.Select(w => string.Join(' ', w.Select(Normalize).Where(x => x.Length > 0))).ToArray();

        // Occurrences in line order (= time order: the alignment is monotonic).
        var occLine = new List<int>();
        var occTime = new List<double>();
        var occAnchors = new List<(int Word, Heard H, double Sim)[]>();
        for (var li = 0; li < n; li++)
        {
            var list = new List<(int, Heard, double)>();
            for (var wi = 0; wi < lineWords[li].Length; wi++)
                if (anchors.TryGetValue(offset[li] + wi, out var a)) list.Add((wi, a.H, a.Sim));
            if (list.Count == 0) continue;
            occLine.Add(li);
            occAnchors.Add(list.ToArray());
            occTime.Add(list[0].Item2.Word.Start.TotalSeconds - list[0].Item1 * LeadPerWord);
        }
        var k = occLine.Count;
        if (k < 2) return;

        var byKey = new Dictionary<string, List<int>>();
        for (var li = 0; li < n; li++)
        {
            if (keys[li].Length == 0) continue;
            if (!byKey.TryGetValue(keys[li], out var same)) byKey[keys[li]] = same = new List<int>();
            same.Add(li);
        }
        var cands = new List<int>[k];
        var anyChoice = false;
        for (var q = 0; q < k; q++)
        {
            var own = occLine[q];
            cands[q] = byKey.TryGetValue(keys[own], out var same) && same.Count > 1 ? same : new List<int> { own };
            anyChoice |= cands[q].Count > 1;
        }
        if (!anyChoice) return;

        // The song's own pace: seconds per word between consecutive occurrences.
        var rates = new List<double>();
        for (var q = 1; q < k; q++)
        {
            var r = (occTime[q] - occTime[q - 1]) / Math.Max(1, offset[occLine[q]] - offset[occLine[q - 1]]);
            if (r > 0.1 && r < 1.5) rates.Add(r);
        }
        rates.Sort();
        var pace = rates.Count >= 3 ? rates[rates.Count / 2] : DefaultSecondsPerWord;
        double Expected(int l0, int l1) => Math.Max(0.2, (offset[l1] - offset[l0]) * pace);
        double Fit(int l0, double t0, int l1, double t1) => Math.Abs(Math.Log(Math.Max(0.05, t1 - t0) / Expected(l0, l1)));

        var cost = new double[k][];
        var from = new int[k][];
        for (var q = 0; q < k; q++)
        {
            cost[q] = new double[cands[q].Count];
            from[q] = new int[cands[q].Count];
            for (var c = 0; c < cands[q].Count; c++)
            {
                cost[q][c] = q == 0 ? 0 : double.MaxValue;
                from[q][c] = -1;
                if (q == 0) continue;
                var l1 = cands[q][c];
                for (var p = 0; p < cands[q - 1].Count; p++)
                {
                    var l0 = cands[q - 1][p];
                    if (l0 >= l1 || cost[q - 1][p] == double.MaxValue) continue;
                    var moved = l0 != occLine[q - 1] || l1 != occLine[q];
                    if (moved && occTime[q] - occTime[q - 1] < MinFit * Expected(l0, l1)) continue;
                    var v = cost[q - 1][p] + Fit(l0, occTime[q - 1], l1, occTime[q]);
                    // Ties keep the alignment's own choice.
                    if (v < cost[q][c] - 1e-9 || (Math.Abs(v - cost[q][c]) < 1e-9 && l0 == occLine[q - 1])) { cost[q][c] = v; from[q][c] = p; }
                }
            }
        }
        var bestC = -1;
        var bestV = double.MaxValue;
        for (var c = 0; c < cands[k - 1].Count; c++)
            if (cost[k - 1][c] < bestV - 1e-9 || (Math.Abs(cost[k - 1][c] - bestV) < 1e-9 && cands[k - 1][c] == occLine[k - 1])) { bestV = cost[k - 1][c]; bestC = c; }
        if (bestC < 0) return;
        var assign = new int[k];
        for (int q = k - 1, c = bestC; q >= 0; c = from[q][c], q--) assign[q] = cands[q][c];
        if (Enumerable.Range(0, k).All(q => assign[q] == occLine[q])) return;
        var original = 0.0;
        for (var q = 1; q < k; q++) original += Fit(occLine[q - 1], occTime[q - 1], occLine[q], occTime[q]);
        if (bestV > original - MinGain) return;

        for (var q = 0; q < k; q++)
            foreach (var (wi, _, _) in occAnchors[q]) anchors.Remove(offset[occLine[q]] + wi);
        for (var q = 0; q < k; q++)
            foreach (var (wi, h, sim) in occAnchors[q]) anchors[offset[assign[q]] + wi] = (h, sim);
    }

    /// <summary>
    /// Which anchored lines to trust: one whose anchors leave the unheard lines before or after
    /// it too little time to be sung at all (benchmark: an outro line heard once, taken by the
    /// last of five identical lines, the other four squeezed into 0.03 s) is more likely
    /// the wrong occurrence than right. DP over the anchored lines in order, maximising the
    /// evidence kept minus one point per second of missing room; dropped lines are interpolated.
    /// </summary>
    private static void KeepLinesThatFit(List<string[]> lineWords, (Heard H, double Sim)?[][] kept, double[] evidence, TimeSpan? totalDuration)
    {
        const double CostPerSecond = 1.0;
        var n = kept.Length;
        var idx = new List<int>();
        var st = new double[n];
        var en = new double[n];
        for (var li = 0; li < n; li++)
        {
            var a = kept[li];
            var f = Array.FindIndex(a, x => x is not null);
            if (f < 0) continue;
            var l = Array.FindLastIndex(a, x => x is not null);
            st[li] = a[f]!.Value.H.Word.Start.TotalSeconds - f * MinSecondsPerWord;
            en[li] = Math.Max(a[l]!.Value.H.Word.End.TotalSeconds, a[l]!.Value.H.Word.Start.TotalSeconds + 0.1) + (lineWords[li].Length - 1 - l) * MinSecondsPerWord;
            idx.Add(li);
        }
        if (idx.Count == 0) return;
        var need = new double[n + 1]; // prefix sums of the shortest time each line can take
        for (var li = 0; li < n; li++) need[li + 1] = need[li] + Math.Max(1, lineWords[li].Length) * MinSecondsPerWord;
        double Need(int fromLine, int toLine) => need[toLine] - need[fromLine];
        var end = totalDuration?.TotalSeconds ?? double.MaxValue;

        var best = new double[idx.Count];
        var back = new int[idx.Count];
        for (var q = 0; q < idx.Count; q++)
        {
            var lq = idx[q];
            var bestV = evidence[lq] - CostPerSecond * Math.Max(0, Need(0, lq) - st[lq]);
            var bestP = -1;
            for (var p = 0; p < q; p++)
            {
                var lp = idx[p];
                var shortage = Math.Max(0, Need(lp + 1, lq) - (st[lq] - en[lp]));
                var v = best[p] + evidence[lq] - CostPerSecond * shortage;
                if (v > bestV + 1e-9 || (Math.Abs(v - bestV) < 1e-9 && bestP >= 0 && p > bestP)) { bestV = v; bestP = p; }
            }
            best[q] = bestV;
            back[q] = bestP;
        }
        var bestEnd = -1;
        var bestTotal = double.MinValue;
        for (var q = 0; q < idx.Count; q++)
        {
            var v = best[q] - CostPerSecond * Math.Max(0, Need(idx[q] + 1, n) - (end - en[idx[q]]));
            if (v >= bestTotal) { bestTotal = v; bestEnd = q; }
        }
        var keep = new bool[n];
        for (var q = bestEnd; q >= 0; q = back[q]) keep[idx[q]] = true;
        foreach (var li in idx)
            if (!keep[li]) kept[li] = new (Heard H, double Sim)?[lineWords[li].Length];
    }

    // ── Word informativeness ──────────────────────────────────────────────────

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "yeah", "yea", "ya", "yah", "yuh", "oh", "ooh", "oo", "ohh", "ooo", "oooh", "ah", "ahh", "aah", "ay", "ayy", "ayyy", "aye",
        "uh", "uhh", "huh", "um", "mm", "mmm", "hmm", "hm", "la", "na", "da", "woah", "whoa", "wo", "woo", "hey", "ha", "hah",
        "eh", "yo", "ho", "hoo", "skrrt", "skrt",
    };

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "i", "im", "ive", "ill", "id", "you", "youre", "youve", "youll", "me", "my", "mine", "we", "were", "us", "our",
        "it", "its", "is", "am", "are", "was", "be", "been", "to", "of", "in", "on", "at", "and", "or", "but", "so", "that", "this",
        "for", "with", "he", "she", "her", "him", "his", "they", "them", "their", "do", "dont", "did", "no", "not", "what", "all",
        "just", "like", "got", "get", "can", "cant", "when", "up", "down", "now", "know", "if", "your", "yall", "as", "by", "from",
        "out", "too", "how", "who", "then", "there", "cause", "cuz", "gon", "gonna", "wanna", "aint", "go", "say", "said", "one",
        "baby", "girl", "yes", "let", "lets", "make", "way", "back", "see", "tell", "come", "here",
        "de", "el", "que", "y", "en", "mi", "tu", "te", "lo", "se", "un", "una", "es", "yo", "con", "por", "pa", "para", "le",
        "los", "las", "si", "ya", "del", "al", "e", "o",
    };

    /// <summary>1 for a content word, less for common words (0.4) and vocables like "yeah", "ooh", "lalala" (0.15).</summary>
    private static double Informativeness(string norm)
    {
        if (norm.Length == 0) return 0;
        if (Fillers.Contains(norm) || IsRepeatedVocable(norm)) return 0.15;
        if (CommonWords.Contains(norm) || (norm.Length <= 2 && norm.All(c => c < 128))) return 0.4;
        return 1.0;
    }

    private static readonly string[] VocableUnits = { "la", "na", "da", "oh", "ooh", "ah", "ha", "yeah", "woah", "whoa", "ayy", "ay", "eh", "uh", "mm", "hm", "hey", "ooo", "oo", "o" };

    /// <summary>"lalala", "ohoh", "nanana", "yeahyeah": one vocable repeated.</summary>
    private static bool IsRepeatedVocable(string norm)
    {
        foreach (var unit in VocableUnits)
        {
            if (norm.Length < unit.Length * 2 || norm.Length % unit.Length != 0) continue;
            var repeated = true;
            for (var k = 0; k < norm.Length && repeated; k += unit.Length)
                repeated = string.CompareOrdinal(norm, k, unit, 0, unit.Length) == 0;
            if (repeated) return true;
        }
        return false;
    }

    // ── Sequence alignment ────────────────────────────────────────────────────

    /// <summary>
    /// Returns (lyricIndex, heardIndex, similarity) pairs on the best monotonic path.
    /// <paramref name="bias"/> (lyric, heard) is added to every match score when given.
    /// </summary>
    internal static List<(int Lyric, int Heard, double Sim)> AlignSequences(IReadOnlyList<string> lyric, IReadOnlyList<string> heard, Func<int, int, double>? bias = null)
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
                if (bias is not null && sim >= AnchorThreshold) matchScore += bias(i - 1, j - 1);
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

    /// <summary>Smaller gaps between two heard words are left alone (the start moves by less than this).</summary>
    private static readonly TimeSpan PullBackMinGap = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Per word, where its start may move back to, or null. Whisper's DTW start of a word is
    /// capped to 0.3 s before its end (right for a line's first word after a rest); mid-line,
    /// a word stamped to start after the previous heard word ended was sung from that end
    /// (benchmark: gap 0.5–1 s → median start error 0.14 s from the previous end vs 0.74 s
    /// from the stamp). Only when the heard word just before is the previous lyric word's anchor.
    /// </summary>
    private static TimeSpan?[] PullBackStarts((Heard H, double Sim)?[] kept, List<Heard> heard)
    {
        var result = new TimeSpan?[kept.Length];
        for (var i = 1; i < kept.Length; i++)
        {
            if (kept[i] is not { } a || kept[i - 1] is not { } b || b.H.Index != a.H.Index - 1) continue;
            var end = b.H.Word.End > b.H.Word.Start ? b.H.Word.End : b.H.Word.Start + TimeSpan.FromMilliseconds(120);
            if (a.H.Word.Start - end > PullBackMinGap) result[i] = end;
        }
        return result;
    }

    /// <summary>
    /// Seconds per word between consecutive heard words of one line (both matched, heard one
    /// after the other): the song's own singing pace, median, within 0.18–0.6 s. Null (the
    /// 0.42 s default) with fewer than 12 such pairs.
    /// </summary>
    private static TimeSpan? AnchorPace((Heard H, double Sim)?[][] kept)
    {
        var gaps = new List<double>();
        foreach (var line in kept)
            for (var wi = 1; wi < line.Length; wi++)
                if (line[wi] is { } b && line[wi - 1] is { } a && b.H.Index == a.H.Index + 1)
                    gaps.Add((b.H.Word.Start - a.H.Word.Start).TotalSeconds);
        if (gaps.Count < 12) return null;
        gaps.Sort();
        return TimeSpan.FromSeconds(Math.Clamp(gaps[gaps.Count / 2], 0.18, 0.6));
    }

    /// <param name="knownEnd">Where the line's window ends: unheard words after the last heard one that would run past it at <paramref name="pace"/> share the room up to it.</param>
    /// <param name="pace">Seconds per unheard word before the first / after the last heard one (default 0.42 s).</param>
    private static List<AlignedWord> InterpolateWords(string[] words, (RecognizedWord Word, double Sim)?[] anchors, TimeSpan? knownStart, TimeSpan?[] pullBack, TimeSpan? knownEnd = null, TimeSpan? pace = null)
    {
        var count = words.Length;
        var starts = new TimeSpan?[count];
        var ends = new TimeSpan?[count];
        for (var i = 0; i < count; i++)
        {
            if (anchors[i] is { } a)
            {
                starts[i] = pullBack[i] is { } pb && pb < a.Word.Start ? pb : a.Word.Start;
                ends[i] = a.Word.End > a.Word.Start ? a.Word.End : a.Word.Start + TimeSpan.FromMilliseconds(120);
            }
        }

        var first = Array.FindIndex(starts, s => s.HasValue);
        var last = Array.FindLastIndex(starts, s => s.HasValue);
        var perWord = pace ?? TimeSpan.FromSeconds(DefaultSecondsPerWord);
        if (knownStart is { } ks && first > 0 && starts[first]!.Value > ks)
        {
            // Leading words the model missed: spread from the known line start to the first anchor.
            var slice = (starts[first]!.Value - ks) / first;
            for (var i = 0; i < first; i++) { starts[i] = ks + slice * i; ends[i] = ks + slice * (i + 1); }
        }
        else
        {
            // Leading unanchored words: back off from the first anchor.
            for (var i = first - 1; i >= 0; i--)
            {
                ends[i] = starts[i + 1];
                var s = ends[i]!.Value - perWord;
                starts[i] = s < TimeSpan.Zero ? TimeSpan.Zero : s;
            }
        }
        // Trailing unanchored words: run on from the last anchor (inside the known end, when given).
        var trailing = count - last - 1;
        var trailingPace = perWord;
        if (trailing > 0 && knownEnd is { } ke && ends[last]!.Value + perWord * trailing > ke && ke > ends[last]!.Value)
            trailingPace = (ke - ends[last]!.Value) / trailing;
        for (var i = last + 1; i < count; i++)
        {
            starts[i] = ends[i - 1];
            ends[i] = starts[i]!.Value + trailingPace;
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
                // The left anchor may end after the right one starts (overlapping or
                // zero-length heard words padded to 120 ms): spread from the right anchor's
                // start instead, or the gap words would start after it.
                if (from > to) from = to;
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

    /// <summary>Seconds per word from one line's start to the next, measured on the song's own anchored lines.</summary>
    private static TimeSpan SongPace(AlignedLine?[] result, List<string[]> lineWords)
    {
        var rates = new List<double>();
        for (var i = 0; i + 1 < result.Length; i++)
        {
            if (result[i] is null || result[i + 1] is null) continue;
            var r = (result[i + 1]!.Start - result[i]!.Start).TotalSeconds / Math.Max(1, lineWords[i].Length);
            if (r >= 0.1 && r <= 1.5) rates.Add(r);
        }
        if (rates.Count < 3) return TimeSpan.FromSeconds(DefaultSecondsPerWord);
        rates.Sort();
        return TimeSpan.FromSeconds(Math.Clamp(rates[rates.Count / 2], 0.25, 0.8));
    }

    private static void FillUnanchoredLines(AlignedLine?[] result, List<string[]> lineWords, TimeSpan? totalDuration, TimeSpan? lastHeardEnd)
    {
        // Lines before the first anchor (or after the last) are sung at the song's own pace.
        var perWord = SongPace(result, lineWords);
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
/// The contract of a saved ELRC, applied to every path's output (aligned, upgraded, transcribed):
/// lines in order, each starting at least <see cref="MinLineGap"/> after the one before; every word
/// inside its own line's window — from the line's start to the next line's start (the last line:
/// to the song's end) — in order and never overlapping the next word; the line's end tag (its last
/// word's end) no later than the next line's start. Benchmark 09-29 (v1.5.7, 47 songs): words ran
/// into the next line in 46 of 47 aligned songs (694 tags) and all 47 upgraded ones (972) — trailing
/// words the model missed run on at a default pace, and a heard word's end overlaps the next line's
/// first word. Words past the window are pulled back just enough to leave each of them
/// <see cref="MinWordSpan"/> (less only when the window is shorter than that per word) and share the
/// room from the last word that stays evenly; words inside are untouched. Line starts are never
/// moved, except to keep them in order and inside the song.
/// </summary>
public static class LineWindows
{
    /// <summary>Line starts closer than this are pushed apart (the written stamps are 10 ms).</summary>
    public static readonly TimeSpan MinLineGap = TimeSpan.FromMilliseconds(10);

    /// <summary>Shortest span a word pulled back into its window is given, when the window has the room.</summary>
    public static readonly TimeSpan MinWordSpan = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<AlignedLine> Fit(IReadOnlyList<AlignedLine> lines, TimeSpan? songEnd = null)
    {
        var n = lines.Count;
        if (n == 0) return lines;
        var end = songEnd is { } se && se > TimeSpan.Zero ? se : (TimeSpan?)null;

        // Line starts: never negative, in order, and (when the song's length is known) inside it.
        var starts = new TimeSpan[n];
        for (var i = 0; i < n; i++)
        {
            var s = lines[i].Start < TimeSpan.Zero ? TimeSpan.Zero : lines[i].Start;
            if (i > 0 && s < starts[i - 1] + MinLineGap) s = starts[i - 1] + MinLineGap;
            starts[i] = s;
        }
        if (end is { } e)
        {
            var limit = e - MinLineGap;
            for (var i = n - 1; i >= 0; i--)
            {
                if (starts[i] > limit) starts[i] = limit < TimeSpan.Zero ? TimeSpan.Zero : limit;
                limit = starts[i] - MinLineGap;
            }
        }

        var result = new List<AlignedLine>(n);
        for (var i = 0; i < n; i++)
        {
            var line = lines[i];
            TimeSpan? cap = i + 1 < n ? starts[i + 1] : end;
            result.Add(FitLine(line, starts[i], cap));
        }
        return result;
    }

    private static AlignedLine FitLine(AlignedLine line, TimeSpan start, TimeSpan? cap)
    {
        var m = line.Words.Count;
        if (m == 0)
        {
            var lineEnd = line.End < start ? start : line.End;
            if (cap is { } c0 && lineEnd > c0) lineEnd = c0 < start ? start : c0;
            return line with { Start = start, End = lineEnd };
        }

        // A line moved as a whole (kept in order / inside the song) takes its words with it.
        var shift = start - line.Start;
        var st = new TimeSpan[m];
        var en = new TimeSpan[m];
        for (var j = 0; j < m; j++)
        {
            var s = line.Words[j].Start + shift;
            var e = line.Words[j].End + shift;
            // Every word at or after the line's start and the previous word's start; an end never before its start.
            var low = j == 0 ? start : st[j - 1];
            if (s < low) s = low;
            if (e < s) e = s;
            st[j] = s;
            en[j] = e;
        }
        for (var j = 0; j + 1 < m; j++)
            if (en[j] > st[j + 1]) en[j] = st[j + 1];

        if (cap is { } c)
        {
            if (c < start) c = start;
            var room = c - start;
            var min = MinWordSpan * m <= room ? MinWordSpan : room / m;
            // First word without room left for itself and the words after it at the minimum span.
            var k = -1;
            for (var j = 0; j < m; j++)
                if (st[j] > c - min * (m - j)) { k = j; break; }
            if (k >= 0)
            {
                var from = start;
                if (k > 0)
                {
                    // The block starts where the last word that stays ends, but leaves the block its room.
                    from = en[k - 1] < c - min * (m - k) ? en[k - 1] : c - min * (m - k);
                    if (from < st[k - 1]) from = st[k - 1];
                    en[k - 1] = from;
                }
                var slice = (c - from) / (m - k);
                for (var j = k; j < m; j++)
                {
                    st[j] = from + slice * (j - k);
                    en[j] = j + 1 < m ? from + slice * (j - k + 1) : c;
                }
            }
            else if (en[m - 1] > c)
                en[m - 1] = c;
        }

        var words = new List<AlignedWord>(m);
        for (var j = 0; j < m; j++)
            words.Add(new AlignedWord(line.Words[j].Text, st[j], en[j]));
        return line with { Start = start, End = en[m - 1], Words = words };
    }
}

/// <summary>
/// Turns a bare transcript into lyric lines when the track has no lyrics to align against:
/// breaks on long pauses, sentence punctuation and a maximum line length.
/// </summary>
public static class TranscriptLines
{
    private static readonly char[] TagDelimiters = { '<', '>' };

    public static IReadOnlyList<AlignedLine> Group(
        IReadOnlyList<RecognizedWord> words,
        int maxWordsPerLine = 8,
        TimeSpan? gapBreak = null,
        TimeSpan? totalDuration = null)
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

        // "<" and ">" delimit ELRC word tags: a heard "<" (benchmark 09-30, one transcript) made the
        // saved line read back with a word too many. They are no lyric text; words of nothing else are dropped.
        var clean = words.Where(w => w is not null && w.Text is not null)
            .Select(w => w.Text.IndexOfAny(TagDelimiters) < 0 ? w : w with { Text = new string(w.Text.Where(c => c is not '<' and not '>').ToArray()) })
            .Where(w => !string.IsNullOrWhiteSpace(w.Text));
        foreach (var w in clean.OrderBy(w => w.Start))
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
        return LineWindows.Fit(lines, totalDuration);
    }
}
