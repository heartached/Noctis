using Noctis.Plugins.Chords.Analysis;

namespace Noctis.Tests;

/// <summary>
/// Synthesized chord audio with a known chord at every instant, for measuring the Chords
/// plugin's accuracy. Each chord is a bass root (octave 2) plus its notes in a close voicing
/// around middle C, every note a stack of harmonics (amplitude 1/h^tilt), so the spectrum carries
/// the same misleading overtones a real instrument does (a C's 3rd harmonic is a G, its 5th an E).
/// </summary>
internal static class ChordSynth
{
    public enum Voice
    {
        /// <summary>Held notes (organ/pad): six harmonics, 1/h.</summary>
        Pad,
        /// <summary>Re-struck on every beat (0.5 s), exponential decay: piano-ish; eight harmonics, 1/h^1.3.</summary>
        Piano,
    }

    public sealed record Options
    {
        public int SampleRate { get; init; } = ChordAnalyzer.SampleRate;
        public Voice Voice { get; init; } = Voice.Pad;
        /// <summary>Whole recording detuned by this many cents from A = 440 Hz.</summary>
        public double TuningCents { get; init; }
        /// <summary>White noise at this signal-to-noise ratio (dB); null = none.</summary>
        public double? NoiseSnrDb { get; init; }
        /// <summary>Kick on every beat and hi-hat between (0.5 s beats).</summary>
        public bool Drums { get; init; }
        /// <summary>A sung-like line on top (vibrato, octave 5, a new note every 0.25 s) drawn from
        /// this major scale's notes (tonic pitch class), so most of its notes are not in the chord.</summary>
        public int? MelodyScale { get; init; }
        /// <summary>The bass plays the chord's third or fifth on every other chord (inversions).</summary>
        public bool Inversions { get; init; }
        public int Seed { get; init; } = 7;
    }

    /// <summary>"C G Am F" (seconds each) repeated <paramref name="times"/>.</summary>
    public static List<(Chord Chord, double Seconds)> Progression(string chords, double seconds, int times = 1)
    {
        var list = new List<(Chord, double)>();
        for (var r = 0; r < times; r++)
            foreach (var name in chords.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Chord.TryParse(name, out var c)) throw new ArgumentException("Unknown chord " + name);
                list.Add((c, seconds));
            }
        return list;
    }

    public static float[] Render(IReadOnlyList<(Chord Chord, double Seconds)> progression, Options? o = null)
    {
        o ??= new Options();
        var rate = o.SampleRate;
        var total = progression.Sum(p => (int)Math.Round(p.Seconds * rate));
        var buffer = new double[total];
        var tune = Math.Pow(2, o.TuningCents / 1200.0);
        var harmonics = o.Voice == Voice.Piano ? 8 : 6;
        var tilt = o.Voice == Voice.Piano ? 1.3 : 1.0;
        var random = new Random(o.Seed);

        var at = 0;
        var index = 0;
        foreach (var (chord, seconds) in progression)
        {
            var length = (int)Math.Round(seconds * rate);
            if (!chord.IsNone)
            {
                var bassNote = o.Inversions && index % 2 == 1 ? chord.Notes[1 + index / 2 % 2] : chord.Root;
                var notes = new List<(double Midi, double Amp)> { (36 + bassNote, 0.30) };
                foreach (var pc in chord.Notes) notes.Add((pc >= 7 ? 48 + pc : 60 + pc, 0.20)); // close voicing G3..F#4
                foreach (var (midi, amp) in notes)
                {
                    var f0 = 440 * Math.Pow(2, (midi - 69) / 12.0) * tune;
                    var phase0 = random.NextDouble() * 2 * Math.PI;
                    for (var h = 1; h <= harmonics; h++)
                    {
                        var f = f0 * h;
                        if (f > rate / 2.0 - 100) break;
                        var a = amp / Math.Pow(h, tilt);
                        var w = 2 * Math.PI * f / rate;
                        for (var i = 0; i < length; i++)
                        {
                            var env = Envelope(o.Voice, i / (double)rate, h);
                            buffer[at + i] += a * env * Math.Sin(w * (at + i) + phase0 * h);
                        }
                    }
                }
                // 5 ms fades at the edges so a change does not click across the whole spectrum.
                var fade = (int)(0.005 * rate);
                for (var i = 0; i < Math.Min(fade, length); i++)
                {
                    buffer[at + i] *= i / (double)fade;
                    buffer[at + length - 1 - i] *= i / (double)fade;
                }
            }
            at += length;
            index++;
        }

        if (o.MelodyScale is { } tonic)
        {
            int[] steps = { 0, 2, 4, 5, 7, 9, 11 };
            var noteLength = (int)(0.25 * rate);
            double phase = 0;
            for (var start = 0; start < total; start += noteLength)
            {
                if (TruthAt(progression, start / (double)rate).IsNone) continue;
                var midi = 72 + (tonic + steps[random.Next(steps.Length)]) % 12;
                var f0 = 440 * Math.Pow(2, (midi - 69) / 12.0) * tune;
                for (var i = 0; i < noteLength && start + i < total; i++)
                {
                    var t = (start + i) / (double)rate;
                    var f = f0 * (1 + 0.006 * Math.Sin(2 * Math.PI * 5.5 * t)); // vibrato
                    phase += 2 * Math.PI * f / rate;
                    var env = Math.Min(1, i / (0.02 * rate)) * Math.Min(1, (noteLength - i) / (0.02 * rate));
                    buffer[start + i] += 0.25 * env * (Math.Sin(phase) + 0.4 * Math.Sin(2 * phase) + 0.2 * Math.Sin(3 * phase));
                }
            }
        }

        if (o.Drums)
        {
            var beat = (int)(0.5 * rate);
            for (var b = 0; b * beat < total; b++)
            {
                var start = b * beat;
                for (var i = 0; i < (int)(0.25 * rate) && start + i < total; i++)
                {
                    var t = i / (double)rate;
                    // Kick: a falling 120→50 Hz thump.
                    var f = 50 + 70 * Math.Exp(-t * 30);
                    buffer[start + i] += 0.6 * Math.Exp(-t * 18) * Math.Sin(2 * Math.PI * f * t);
                }
                var hat = start + beat / 2;
                for (var i = 0; i < (int)(0.08 * rate) && hat + i < total; i++)
                    buffer[hat + i] += 0.25 * Math.Exp(-i / (double)rate * 60) * (random.NextDouble() * 2 - 1);
            }
        }

        if (o.NoiseSnrDb is { } snr)
        {
            var rms = Math.Sqrt(buffer.Sum(v => v * v) / Math.Max(1, total));
            var noise = rms / Math.Pow(10, snr / 20) * Math.Sqrt(3); // uniform noise of that RMS
            for (var i = 0; i < total; i++) buffer[i] += noise * (random.NextDouble() * 2 - 1);
        }

        var peak = buffer.Select(Math.Abs).DefaultIfEmpty(0).Max();
        var scale = peak > 0 ? 0.9 / peak : 1;
        var result = new float[total];
        for (var i = 0; i < total; i++) result[i] = (float)(buffer[i] * scale);
        return result;
    }

    private static double Envelope(Voice voice, double t, int harmonic)
    {
        if (voice == Voice.Pad) return 1;
        var sinceStrike = t % 0.5;
        var attack = Math.Min(1, sinceStrike / 0.004);
        return attack * Math.Exp(-sinceStrike * (2.0 + 0.6 * harmonic)); // upper harmonics die faster
    }

    /// <summary>The true chord at <paramref name="seconds"/>.</summary>
    public static Chord TruthAt(IReadOnlyList<(Chord Chord, double Seconds)> progression, double seconds)
    {
        double at = 0;
        foreach (var (chord, length) in progression)
        {
            if (seconds < at + length) return chord;
            at += length;
        }
        return Chord.NoChord;
    }

    /// <summary>
    /// Frame accuracy: the share of 10 ms instants where the estimate names the true chord (root and
    /// kind; with <paramref name="triadsOnly"/> a seventh counts as its triad on both sides).
    /// </summary>
    public static double Accuracy(ChordAnalysis analysis, IReadOnlyList<(Chord Chord, double Seconds)> progression, bool triadsOnly)
    {
        var total = progression.Sum(p => p.Seconds);
        int right = 0, all = 0;
        for (var t = 0.005; t < total; t += 0.01)
        {
            var truth = TruthAt(progression, t);
            var guess = analysis.ChordAt(t);
            if (triadsOnly)
            {
                truth = truth.Triad;
                guess = guess.Triad;
            }
            all++;
            if (truth == guess) right++;
        }
        return all == 0 ? 0 : right / (double)all;
    }
}
