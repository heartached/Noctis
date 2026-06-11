using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Math for the parametric equalizer.
///
/// LibVLC only exposes a fixed 10-band graphic equalizer, so parametric bands
/// (RBJ peaking filters with frequency / gain / Q) are applied by sampling
/// their composite magnitude response at the 10 graphic band frequencies and
/// driving the native equalizer with the result. This keeps the entire
/// playback chain (crossfade, standby player, output module) untouched.
/// </summary>
public static class ParametricEqMath
{
    public const int MinBands = 5;
    public const int MaxBands = 10;
    public const double MinFrequencyHz = 20.0;
    public const double MaxFrequencyHz = 20000.0;
    public const double MinGainDb = -12.0;
    public const double MaxGainDb = 12.0;
    public const double MinQ = 0.1;
    public const double MaxQ = 10.0;
    public const double DefaultQ = 1.41;

    // Reference rate for evaluating the response curve. The shape is nearly
    // rate-independent for audio bands; 48k matches the common output rate.
    private const double SampleRate = 48000.0;

    /// <summary>Center frequencies (Hz) of LibVLC's 10 graphic EQ bands.</summary>
    public static readonly double[] GraphicBandFrequencies =
        { 60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000 };

    /// <summary>
    /// Magnitude response in dB of a single RBJ peaking filter at <paramref name="atHz"/>.
    /// </summary>
    public static double PeakingResponseDb(double centerHz, double gainDb, double q, double atHz)
    {
        gainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        if (Math.Abs(gainDb) < 1e-9) return 0.0;
        centerHz = Math.Clamp(centerHz, MinFrequencyHz, MaxFrequencyHz);
        q = Math.Clamp(q, MinQ, MaxQ);

        // RBJ Audio EQ Cookbook peaking-EQ coefficients.
        var a = Math.Pow(10.0, gainDb / 40.0);
        var w0 = 2.0 * Math.PI * centerHz / SampleRate;
        var alpha = Math.Sin(w0) / (2.0 * q);
        var b0 = 1 + alpha * a;
        var b1 = -2 * Math.Cos(w0);
        var b2 = 1 - alpha * a;
        var a0 = 1 + alpha / a;
        var a1 = b1;
        var a2 = 1 - alpha / a;

        var w = 2.0 * Math.PI * Math.Clamp(atHz, 1.0, SampleRate / 2.0 - 1.0) / SampleRate;
        var cosW = Math.Cos(w);
        var cos2W = Math.Cos(2 * w);
        var sinW = Math.Sin(w);
        var sin2W = Math.Sin(2 * w);
        var numRe = b0 + b1 * cosW + b2 * cos2W;
        var numIm = b1 * sinW + b2 * sin2W;
        var denRe = a0 + a1 * cosW + a2 * cos2W;
        var denIm = a1 * sinW + a2 * sin2W;
        var mag2 = (numRe * numRe + numIm * numIm) / (denRe * denRe + denIm * denIm);
        return 10.0 * Math.Log10(mag2);
    }

    /// <summary>Composite response in dB of all bands at <paramref name="atHz"/> (filters cascade, so dB adds).</summary>
    public static double CompositeResponseDb(IEnumerable<ParametricEqBand> bands, double atHz)
        => bands.Sum(b => PeakingResponseDb(b.FrequencyHz, b.GainDb, b.Q, atHz));

    /// <summary>
    /// Sample the composite parametric response at the 10 graphic band
    /// frequencies, producing the amp values to hand to LibVLC's equalizer.
    /// </summary>
    public static float[] MapToGraphicBands(IEnumerable<ParametricEqBand> bands)
    {
        var snapshot = bands as IReadOnlyCollection<ParametricEqBand> ?? bands.ToList();
        var result = new float[GraphicBandFrequencies.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (float)Math.Clamp(
                CompositeResponseDb(snapshot, GraphicBandFrequencies[i]),
                MinGainDb, MaxGainDb);
        }
        return result;
    }

    /// <summary>
    /// Convert legacy 10-band graphic gains into parametric bands (one band per
    /// graphic frequency at the default Q). Used to migrate pre-parametric settings.
    /// </summary>
    public static List<ParametricEqBand> FromGraphicBands(float[]? graphicGains)
    {
        var bands = new List<ParametricEqBand>(GraphicBandFrequencies.Length);
        for (var i = 0; i < GraphicBandFrequencies.Length; i++)
        {
            var gain = graphicGains is { Length: 10 } ? graphicGains[i] : 0f;
            bands.Add(new ParametricEqBand
            {
                FrequencyHz = GraphicBandFrequencies[i],
                GainDb = Math.Clamp(gain, MinGainDb, MaxGainDb),
                Q = DefaultQ,
            });
        }
        return bands;
    }
}
