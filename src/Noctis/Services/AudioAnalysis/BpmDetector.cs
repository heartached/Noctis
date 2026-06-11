using System;

namespace Noctis.Services.AudioAnalysis;

/// <summary>
/// Tempo estimation via a spectral-flux onset envelope and autocorrelation over
/// 40–200 BPM, with octave-error correction toward the 90–150 BPM range.
/// </summary>
public static class BpmDetector
{
    private const int FrameSize = 1024;
    private const int HopSize = 512;

    public static (int Bpm, double Confidence) Detect(float[] mono, int sampleRate)
    {
        if (mono.Length < sampleRate) return (0, 0);

        var envelope = OnsetEnvelope(mono, sampleRate, out double envRate);
        if (envelope.Length < 4) return (0, 0);

        // Energy guard: near-silent input yields no usable peak.
        double mean = 0;
        for (int i = 0; i < envelope.Length; i++) mean += envelope[i];
        mean /= envelope.Length;
        if (mean <= 1e-7) return (0, 0);

        int minLag = (int)Math.Floor(envRate * 60.0 / 200.0); // 200 BPM
        int maxLag = (int)Math.Ceiling(envRate * 60.0 / 40.0); // 40 BPM
        maxLag = Math.Min(maxLag, envelope.Length - 1);
        if (minLag < 1 || maxLag <= minLag) return (0, 0);

        double bestScore = 0, total = 0;
        int bestLag = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = lag; i < envelope.Length; i++) sum += envelope[i] * envelope[i - lag];
            total += sum;
            if (sum > bestScore) { bestScore = sum; bestLag = lag; }
        }
        if (bestLag == 0) return (0, 0);

        // Octave-error correction: fold the raw estimate into a perceptually
        // centred [80, 160) BPM range. A sharp click train autocorrelates as
        // strongly at half-period (double tempo) as at the true period, and the
        // shorter lag wins on raw energy, so the upper bound must fold back down.
        double bpm = 60.0 * envRate / bestLag;
        while (bpm < 80) bpm *= 2;
        while (bpm >= 160) bpm /= 2;

        double confidence = total > 0 ? Math.Clamp(bestScore / (total / (maxLag - minLag + 1)) / 8.0, 0, 1) : 0;
        return ((int)Math.Round(bpm), confidence);
    }

    private static double[] OnsetEnvelope(float[] mono, int sampleRate, out double envRate)
    {
        int frames = Math.Max(0, 1 + (mono.Length - FrameSize) / HopSize);
        envRate = (double)sampleRate / HopSize;
        var env = new double[frames];
        var prevMag = new double[FrameSize / 2];
        var re = new double[FrameSize];
        var im = new double[FrameSize];

        for (int f = 0; f < frames; f++)
        {
            int start = f * HopSize;
            for (int i = 0; i < FrameSize; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1)); // Hann
                re[i] = mono[start + i] * w;
                im[i] = 0;
            }
            Fft.Forward(re, im);
            double flux = 0;
            for (int k = 0; k < FrameSize / 2; k++)
            {
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                double diff = mag - prevMag[k];
                if (diff > 0) flux += diff; // half-wave rectified spectral flux
                prevMag[k] = mag;
            }
            env[f] = flux;
        }

        // Subtract a moving-average baseline to emphasise onsets.
        int win = (int)Math.Max(1, envRate * 0.1);
        var outp = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0; int c = 0;
            for (int j = Math.Max(0, i - win); j <= Math.Min(frames - 1, i + win); j++) { sum += env[j]; c++; }
            double v = env[i] - sum / Math.Max(1, c);
            outp[i] = v > 0 ? v : 0;
        }
        return outp;
    }
}
