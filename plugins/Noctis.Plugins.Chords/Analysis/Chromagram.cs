namespace Noctis.Plugins.Chords.Analysis;

/// <summary>
/// Pitch-class profiles over time (a chromagram), from mono PCM.
///
/// Each frame (4096 samples, Hann window, hop 1024: 93 ms at 11025 Hz) is FFT'd; spectral peaks
/// between 50 Hz and 2.5 kHz are located to a fraction of a bin (parabolic interpolation on the
/// log magnitude), and each peak adds its magnitude to the pitch classes it could belong to
/// (itself and, with falling weight, the fundamentals it could be the 2nd–4th harmonic of: the
/// harmonic pitch-class profile, HPCP). A peak's weight falls off with its distance from the
/// semitone centre (cos², zero half a semitone away). The semitone grid is first shifted to the
/// recording's own tuning, estimated from where all peaks sit relative to A = 440 Hz.
/// Two profiles per frame: the whole range (treble) and below ~260 Hz (bass).
/// </summary>
public sealed class Chromagram
{
    public const int DefaultRate = 11025;
    public const int WindowSize = 4096;
    public const int Hop = 1024;
    private const double MinFreq = 50;
    private const double MaxFreq = 2500;
    private const double BassMaxMidi = 60; // below middle C
    private const double PeakFloorDb = -50;

    private Chromagram(int sampleRate, double[][] treble, double[][] bass, double[] energy, double tuningCents, double duration)
    {
        SampleRate = sampleRate;
        Treble = treble;
        Bass = bass;
        Energy = energy;
        TuningCents = tuningCents;
        Duration = duration;
    }

    public int SampleRate { get; }
    public int Count => Treble.Length;
    /// <summary>Per frame: 12 values (C..B), not normalized.</summary>
    public double[][] Treble { get; }
    /// <summary>Per frame: 12 values, from the bass range only.</summary>
    public double[][] Bass { get; }
    /// <summary>Per frame: RMS of the windowed samples.</summary>
    public double[] Energy { get; }
    public double TuningCents { get; }
    public double Duration { get; }

    public double HopSeconds => Hop / (double)SampleRate;

    /// <summary>Centre time of frame <paramref name="i"/>, seconds.</summary>
    public double FrameTime(int i) => (i * (double)Hop + WindowSize / 2.0) / SampleRate;

    /// <param name="harmonics">How many harmonics a peak is credited back to (1 = plain chroma).</param>
    public static Chromagram Compute(ReadOnlySpan<float> samples, int sampleRate, int harmonics = 4,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var duration = samples.Length / (double)sampleRate;
        var frames = samples.Length < WindowSize ? (samples.Length > 0 ? 1 : 0) : 1 + (samples.Length - WindowSize) / Hop;
        var fft = new Fft(WindowSize);
        var window = new double[WindowSize];
        for (var i = 0; i < WindowSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1));
        var re = new double[WindowSize];
        var im = new double[WindowSize];
        var mag = new double[WindowSize / 2 + 1];
        var lowBin = Math.Max(2, (int)Math.Floor(MinFreq * WindowSize / sampleRate));
        var highBin = Math.Min(WindowSize / 2 - 2, (int)Math.Ceiling(MaxFreq * WindowSize / sampleRate));

        // Pass 1: peaks of every frame (frequency, magnitude), frame energy, and the tuning.
        var peaks = new List<(float Freq, float Mag)>[frames];
        var energy = new double[frames];
        double tuneX = 0, tuneY = 0;
        for (var f = 0; f < frames; f++)
        {
            if ((f & 63) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(0.8 * f / Math.Max(1, frames));
            }
            var start = f * Hop;
            double sumSq = 0;
            for (var i = 0; i < WindowSize; i++)
            {
                var x = start + i < samples.Length ? samples[start + i] : 0f;
                var w = x * window[i];
                re[i] = w;
                im[i] = 0;
                sumSq += w * w;
            }
            energy[f] = Math.Sqrt(sumSq / WindowSize);
            var list = new List<(float, float)>();
            peaks[f] = list;
            if (energy[f] < 1e-6) continue;
            fft.Transform(re, im);
            double max = 0;
            for (var k = lowBin - 1; k <= highBin + 1; k++)
            {
                mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                if (k >= lowBin && k <= highBin && mag[k] > max) max = mag[k];
            }
            if (max <= 0) continue;
            var floor = max * Math.Pow(10, PeakFloorDb / 20);
            for (var k = lowBin; k <= highBin; k++)
            {
                var m = mag[k];
                if (m < floor || m <= mag[k - 1] || m < mag[k + 1]) continue;
                // Parabolic interpolation on the log magnitude: the true peak between bins.
                var a = Math.Log(mag[k - 1] + 1e-12);
                var b = Math.Log(m + 1e-12);
                var c = Math.Log(mag[k + 1] + 1e-12);
                var denom = a - 2 * b + c;
                var p = denom < 0 ? 0.5 * (a - c) / denom : 0;
                if (p is < -0.5 or > 0.5) p = 0;
                var freq = (k + p) * sampleRate / WindowSize;
                var peakMag = Math.Exp(b - 0.25 * (a - c) * p);
                list.Add(((float)freq, (float)peakMag));
                var semis = 12 * Math.Log2(freq / 440.0);
                var dev = semis - Math.Round(semis);
                tuneX += peakMag * Math.Cos(2 * Math.PI * dev);
                tuneY += peakMag * Math.Sin(2 * Math.PI * dev);
            }
        }
        var tuning = tuneX == 0 && tuneY == 0 ? 0 : Math.Atan2(tuneY, tuneX) / (2 * Math.PI); // semitones, -0.5..0.5

        // Pass 2: pitch-class profiles on the recording's own semitone grid.
        var treble = new double[frames][];
        var bass = new double[frames][];
        harmonics = Math.Clamp(harmonics, 1, 8);
        for (var f = 0; f < frames; f++)
        {
            if ((f & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(0.8 + 0.15 * f / Math.Max(1, frames));
            }
            var t = new double[12];
            var bs = new double[12];
            foreach (var (freq, m) in peaks[f])
            {
                for (var h = 1; h <= harmonics; h++)
                {
                    var f0 = freq / (double)h;
                    if (f0 < MinFreq) break;
                    var midi = 69 + 12 * Math.Log2(f0 / 440.0) - tuning;
                    var nearest = Math.Round(midi);
                    var d = midi - nearest;
                    var w = Math.Cos(Math.PI * d);
                    w *= w;
                    var weight = m * w * Math.Pow(0.6, h - 1);
                    var pc = (((int)nearest % 12) + 12) % 12;
                    t[pc] += weight;
                    if (h == 1 && midi < BassMaxMidi) bs[pc] += weight;
                }
            }
            treble[f] = t;
            bass[f] = bs;
        }
        progress?.Report(0.95);
        return new Chromagram(sampleRate, treble, bass, energy, tuning * 100, duration);
    }
}
