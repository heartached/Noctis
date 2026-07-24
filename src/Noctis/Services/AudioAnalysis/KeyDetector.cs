using System;

namespace Noctis.Services.AudioAnalysis;

/// <summary>
/// Estimates the musical key by accumulating a 12-bin chroma vector and
/// correlating it against rotated Krumhansl–Schmuckler major/minor profiles.
/// Returns a standard key string such as "C major" / "A minor".
/// </summary>
public static class KeyDetector
{
    private const int FrameSize = 4096;
    private const int HopSize = 2048;

    private static readonly string[] PitchNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly double[] MajorProfile =
        { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] MinorProfile =
        { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    public static (string Key, double Confidence) Detect(float[] mono, int sampleRate)
        => Detect(mono, sampleRate, mono.Length);

    /// <summary>Variant for reusable buffers: only the first <paramref name="count"/> samples are valid.</summary>
    public static (string Key, double Confidence) Detect(float[] mono, int sampleRate, int count)
    {
        if (count < FrameSize) return ("", 0);

        var chroma = new double[12];
        var re = new double[FrameSize];
        var im = new double[FrameSize];
        int frames = 1 + (count - FrameSize) / HopSize;

        for (int f = 0; f < frames; f++)
        {
            int start = f * HopSize;
            for (int i = 0; i < FrameSize; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));
                re[i] = mono[start + i] * w;
                im[i] = 0;
            }
            Fft.Forward(re, im);
            for (int k = 1; k < FrameSize / 2; k++)
            {
                double freq = (double)k * sampleRate / FrameSize;
                if (freq < 55 || freq > 5000) continue; // A1..~D8
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                int pitch = (int)Math.Round(12 * Math.Log2(freq / 440.0)) + 69; // MIDI
                chroma[((pitch % 12) + 12) % 12] += mag;
            }
        }

        double sum = 0;
        for (int i = 0; i < 12; i++) sum += chroma[i];
        // `sum <= 1e-9` is false for NaN, so a NaN chroma slipped past this guard —
        // ffmpeg passes NaN straight through for a corrupt pcm_f32le WAV.
        if (!double.IsFinite(sum) || sum <= 1e-9) return ("", 0);
        for (int i = 0; i < 12; i++) chroma[i] /= sum;

        // bestKey starts at -1, not 0. Correlate returns NaN for a NaN chroma and exactly
        // 0 for a zero-variance one (white noise / a flat chroma); NaN fails every `>`
        // comparison below, so bestKey was never assigned and the method happily returned
        // PitchNames[0] — "C major" — as a confident-looking result for undetectable
        // input, which then got written to the library and into the user's file tags.
        double bestScore = double.NegativeInfinity, secondBest = double.NegativeInfinity;
        int bestKey = -1; bool bestMinor = false;
        for (int rot = 0; rot < 12; rot++)
        {
            double maj = Correlate(chroma, MajorProfile, rot);
            double min = Correlate(chroma, MinorProfile, rot);
            if (maj > bestScore) { secondBest = bestScore; bestScore = maj; bestKey = rot; bestMinor = false; }
            else if (maj > secondBest) secondBest = maj;
            if (min > bestScore) { secondBest = bestScore; bestScore = min; bestKey = rot; bestMinor = true; }
            else if (min > secondBest) secondBest = min;
        }

        if (bestKey < 0 || !double.IsFinite(bestScore)) return ("", 0);

        double confidence = bestScore > 0 ? Math.Clamp((bestScore - secondBest) / bestScore, 0, 1) : 0;
        if (!double.IsFinite(confidence)) confidence = 0;
        return ($"{PitchNames[bestKey]} {(bestMinor ? "minor" : "major")}", confidence);
    }

    private static double Correlate(double[] chroma, double[] profile, int rotation)
    {
        // Pearson correlation between chroma and the profile rotated to start at `rotation`.
        double mc = 0, mp = 0;
        for (int i = 0; i < 12; i++) { mc += chroma[i]; mp += profile[i]; }
        mc /= 12; mp /= 12;
        double num = 0, dc = 0, dp = 0;
        for (int i = 0; i < 12; i++)
        {
            double c = chroma[i] - mc;
            double p = profile[(i - rotation + 12) % 12] - mp;
            num += c * p; dc += c * c; dp += p * p;
        }
        return (dc <= 0 || dp <= 0) ? 0 : num / Math.Sqrt(dc * dp);
    }
}
