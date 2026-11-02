namespace Noctis.Plugins.Chords.Analysis;

/// <summary>Knobs of the recognizer. The defaults are what the synthetic accuracy tests were measured with.</summary>
public sealed record ChordRecognizerOptions
{
    /// <summary>Look for 7, maj7 and m7 chords as well as major and minor.</summary>
    public bool Sevenths { get; init; }

    /// <summary>Harmonics each spectral peak is credited back to (HPCP).</summary>
    public int Harmonics { get; init; } = 4;

    /// <summary>Frames averaged on each side before matching (1 = ±1 frame, ~0.28 s in all).</summary>
    public int SmoothRadius { get; init; } = 1;

    /// <summary>What a chord change costs in the Viterbi path, in units of per-frame similarity.</summary>
    public double ChangePenalty { get; init; } = 0.9;

    /// <summary>How much the bass note agreeing with the chord's root adds to its score.</summary>
    public double BassWeight { get; init; } = 0.12;

    /// <summary>Taken off seventh chords' scores, so a plain triad is not called a seventh.</summary>
    public double SeventhPenalty { get; init; } = 0.035;

    /// <summary>Added to chords that belong to the estimated key (a second pass).</summary>
    public double InKeyBonus { get; init; } = 0.02;

    /// <summary>Frames quieter than this fraction of the song's loud frames (95th percentile) are "no chord".</summary>
    public double SilenceRatio { get; init; } = 0.03;
}

/// <summary>
/// Chord estimate from a <see cref="Chromagram"/>: each frame's (lightly smoothed, normalized)
/// pitch-class profile is scored against binary chord templates (cosine similarity, plus a bass
/// bonus for the root), then the best chord sequence is found with Viterbi decoding, where every
/// change of chord costs <see cref="ChordRecognizerOptions.ChangePenalty"/>; that keeps a single
/// odd frame from flipping the chord. A first pass estimates the key (Krumhansl–Kessler profiles
/// against the song's summed chroma, nudged toward the most-heard tonic chord); a second pass gives
/// chords of that key a small bonus.
/// </summary>
public static class ChordRecognizer
{
    // Krumhansl–Kessler key profiles (probe-tone ratings), tonic first.
    private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    public static IReadOnlyList<Chord> Vocabulary(bool sevenths)
    {
        var list = new List<Chord> { Chord.NoChord };
        var qualities = sevenths
            ? new[] { ChordQuality.Major, ChordQuality.Minor, ChordQuality.Dominant7, ChordQuality.Major7, ChordQuality.Minor7 }
            : new[] { ChordQuality.Major, ChordQuality.Minor };
        foreach (var q in qualities)
            for (var r = 0; r < 12; r++) list.Add(new Chord(r, q));
        return list;
    }

    public static ChordAnalysis Recognize(Chromagram chroma, ChordRecognizerOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ChordRecognizerOptions();
        var vocab = Vocabulary(options.Sevenths);
        var n = chroma.Count;
        if (n == 0) return new ChordAnalysis(Array.Empty<ChordSegment>(), null, chroma.Duration, chroma.TuningCents, options.Sevenths);

        var silent = SilentFrames(chroma, options.SilenceRatio);
        var (treble, bass) = Normalize(chroma, options.SmoothRadius);
        var templates = vocab.Select(Template).ToArray();
        ct.ThrowIfCancellationRequested();

        // Per-frame scores, without the key.
        var scores = new double[n][];
        for (var f = 0; f < n; f++)
        {
            var row = new double[vocab.Count];
            for (var s = 0; s < vocab.Count; s++)
            {
                var chord = vocab[s];
                if (chord.IsNone) { row[s] = silent[f] ? 1 : 0; continue; }
                if (silent[f]) { row[s] = 0; continue; }
                double dot = 0;
                var t = templates[s];
                for (var p = 0; p < 12; p++) dot += treble[f][p] * t[p];
                var score = dot + options.BassWeight * bass[f][chord.Root];
                if (chord.Quality is ChordQuality.Dominant7 or ChordQuality.Major7 or ChordQuality.Minor7) score -= options.SeventhPenalty;
                row[s] = score;
            }
            scores[f] = row;
        }

        var path = Viterbi(scores, options.ChangePenalty, ct);
        var key = EstimateKey(chroma, silent, vocab, path);
        if (key is { } k && options.InKeyBonus > 0)
        {
            var diatonic = Diatonic(k);
            for (var f = 0; f < n; f++)
                for (var s = 0; s < vocab.Count; s++)
                    if (!silent[f] && diatonic.Contains(vocab[s].Triad)) scores[f][s] += options.InKeyBonus;
            path = Viterbi(scores, options.ChangePenalty, ct);
            key = EstimateKey(chroma, silent, vocab, path) ?? key;
        }
        var segments = Segments(chroma, vocab, path, scores);
        return new ChordAnalysis(segments, key, chroma.Duration, chroma.TuningCents, options.Sevenths);
    }

    /// <summary>A unit-length template: the chord's notes at 1 (a seventh at 0.8).</summary>
    internal static double[] Template(Chord chord)
    {
        var t = new double[12];
        if (chord.IsNone) return t;
        var notes = chord.Notes;
        for (var i = 0; i < notes.Length; i++) t[notes[i]] += i == 3 ? 0.8 : 1.0;
        var norm = Math.Sqrt(t.Sum(v => v * v));
        for (var p = 0; p < 12; p++) t[p] /= norm;
        return t;
    }

    private static bool[] SilentFrames(Chromagram chroma, double ratio)
    {
        var sorted = chroma.Energy.ToArray();
        Array.Sort(sorted);
        var loud = sorted.Length == 0 ? 0 : sorted[(int)Math.Floor(0.95 * (sorted.Length - 1))];
        var threshold = Math.Max(1e-4, loud * ratio);
        var silent = new bool[chroma.Count];
        for (var f = 0; f < chroma.Count; f++)
            silent[f] = chroma.Energy[f] < threshold || chroma.Treble[f].Sum() <= 0;
        return silent;
    }

    /// <summary>Smoothed over ±<paramref name="radius"/> frames; treble to unit length, bass to max 1.</summary>
    private static (double[][] Treble, double[][] Bass) Normalize(Chromagram chroma, int radius)
    {
        var n = chroma.Count;
        var treble = new double[n][];
        var bass = new double[n][];
        for (var f = 0; f < n; f++)
        {
            var t = new double[12];
            var b = new double[12];
            for (var g = Math.Max(0, f - radius); g <= Math.Min(n - 1, f + radius); g++)
                for (var p = 0; p < 12; p++)
                {
                    t[p] += chroma.Treble[g][p];
                    b[p] += chroma.Bass[g][p];
                }
            var norm = Math.Sqrt(t.Sum(v => v * v));
            if (norm > 0) for (var p = 0; p < 12; p++) t[p] /= norm;
            var max = b.Max();
            if (max > 0) for (var p = 0; p < 12; p++) b[p] /= max;
            treble[f] = t;
            bass[f] = b;
        }
        return (treble, bass);
    }

    /// <summary>Best state sequence: maximizes the summed scores minus <paramref name="penalty"/> per change.</summary>
    internal static int[] Viterbi(double[][] scores, double penalty, CancellationToken ct = default)
    {
        var n = scores.Length;
        var states = scores[0].Length;
        var back = new int[n][];
        var prev = (double[])scores[0].Clone();
        var cur = new double[states];
        for (var f = 1; f < n; f++)
        {
            if ((f & 1023) == 0) ct.ThrowIfCancellationRequested();
            var bestPrev = 0;
            for (var s = 1; s < states; s++) if (prev[s] > prev[bestPrev]) bestPrev = s;
            var row = new int[states];
            for (var s = 0; s < states; s++)
            {
                var stay = prev[s];
                var move = prev[bestPrev] - penalty;
                if (stay >= move) { cur[s] = stay + scores[f][s]; row[s] = s; }
                else { cur[s] = move + scores[f][s]; row[s] = bestPrev; }
            }
            back[f] = row;
            (prev, cur) = (cur, prev);
        }
        var path = new int[n];
        var last = 0;
        for (var s = 1; s < states; s++) if (prev[s] > prev[last]) last = s;
        path[n - 1] = last;
        for (var f = n - 1; f > 0; f--) path[f - 1] = back[f][path[f]];
        return path;
    }

    /// <summary>
    /// Krumhansl–Kessler correlation of the song's summed chroma (unit frames, silence skipped)
    /// with the 24 key profiles; then, between the two best keys, the one whose tonic chord the
    /// path holds longer gets a bonus (C major and A minor share their notes; the chord the song
    /// rests on tells them apart).
    /// </summary>
    internal static MusicalKey? EstimateKey(Chromagram chroma, bool[] silent, IReadOnlyList<Chord> vocab, int[] path)
    {
        var sum = new double[12];
        var any = false;
        for (var f = 0; f < chroma.Count; f++)
        {
            if (silent[f]) continue;
            var t = chroma.Treble[f];
            var norm = Math.Sqrt(t.Sum(v => v * v));
            if (norm <= 0) continue;
            for (var p = 0; p < 12; p++) sum[p] += t[p] / norm;
            any = true;
        }
        if (!any) return null;

        var held = new Dictionary<Chord, int>();
        var chordFrames = 0;
        foreach (var s in path)
        {
            var c = vocab[s].Triad;
            if (c.IsNone) continue;
            held[c] = held.GetValueOrDefault(c) + 1;
            chordFrames++;
        }

        var scored = new List<(MusicalKey Key, double Score)>();
        for (var tonic = 0; tonic < 12; tonic++)
            foreach (var minor in new[] { false, true })
            {
                var profile = minor ? MinorProfile : MajorProfile;
                var rotated = new double[12];
                for (var p = 0; p < 12; p++) rotated[(p + tonic) % 12] = profile[p];
                var key = new MusicalKey(tonic, minor);
                var tonicShare = chordFrames == 0 ? 0 : held.GetValueOrDefault(key.TonicChord) / (double)chordFrames;
                scored.Add((key, Correlation(sum, rotated) + 0.5 * tonicShare));
            }
        return scored.MaxBy(s => s.Score).Key;
    }

    private static double Correlation(double[] a, double[] b)
    {
        var ma = a.Average();
        var mb = b.Average();
        double num = 0, da = 0, db = 0;
        for (var i = 0; i < a.Length; i++)
        {
            num += (a[i] - ma) * (b[i] - mb);
            da += (a[i] - ma) * (a[i] - ma);
            db += (b[i] - mb) * (b[i] - mb);
        }
        return da <= 0 || db <= 0 ? 0 : num / Math.Sqrt(da * db);
    }

    /// <summary>The key's diatonic triads (major: I ii iii IV V vi; minor: i III iv v V VI VII).</summary>
    internal static HashSet<Chord> Diatonic(MusicalKey key)
    {
        var t = key.Tonic;
        Chord C(int offset, ChordQuality q) => new((t + offset) % 12, q);
        return key.Minor
            ? new HashSet<Chord> { C(0, ChordQuality.Minor), C(3, ChordQuality.Major), C(5, ChordQuality.Minor), C(7, ChordQuality.Minor), C(7, ChordQuality.Major), C(8, ChordQuality.Major), C(10, ChordQuality.Major) }
            : new HashSet<Chord> { C(0, ChordQuality.Major), C(2, ChordQuality.Minor), C(4, ChordQuality.Minor), C(5, ChordQuality.Major), C(7, ChordQuality.Major), C(9, ChordQuality.Minor) };
    }

    private static List<ChordSegment> Segments(Chromagram chroma, IReadOnlyList<Chord> vocab, int[] path, double[][] scores)
    {
        var segments = new List<ChordSegment>();
        var n = path.Length;
        var start = 0;
        for (var f = 1; f <= n; f++)
        {
            if (f < n && path[f] == path[start]) continue;
            var s = path[start];
            double margin = 0;
            for (var g = start; g < f; g++)
            {
                var row = scores[g];
                var second = double.NegativeInfinity;
                for (var o = 0; o < row.Length; o++)
                    if (o != s && row[o] > second) second = row[o];
                margin += Math.Clamp((row[s] - second) / 0.25, 0, 1);
            }
            // Boundaries halfway between the centres of the last frame of one chord and the first of the next.
            var from = start == 0 ? 0 : (chroma.FrameTime(start - 1) + chroma.FrameTime(start)) / 2;
            var to = f == n ? chroma.Duration : (chroma.FrameTime(f - 1) + chroma.FrameTime(f)) / 2;
            if (to > from) segments.Add(new ChordSegment(Math.Round(from, 3), Math.Round(to, 3), vocab[s], Math.Round(margin / (f - start), 3)));
            start = f;
        }
        return segments;
    }
}
