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

    /// <summary>
    /// Preamp (dB) that makes VLC's equalizer unity gain. VLC's filter scales
    /// its input by EQZ_IN_FACTOR = 0.25 (−12 dB) and relies on the preamp to
    /// make up the loss (modules/audio_filter/equalizer.c) — its own presets
    /// bake ~+12 dB into their table for the same reason. 20·log10(4) cancels
    /// the factor exactly; without it, engaging any custom band drops the whole
    /// signal ~12 dB below the bypassed/native level.
    /// </summary>
    public const float VlcEqUnityPreampDb = 12.0412f;

    /// <summary>
    /// User-facing EQ pre-amp bounds, in dB relative to native level. The point
    /// of the control is negative headroom (boosted bands clip in the chain
    /// BEFORE the post-mix volume slider can help), so the boost side is kept
    /// small. VLC's preamp ceiling is +20 dB; unity sits at ~+12, leaving ~8 dB
    /// of real headroom above it — +6 stays inside that.
    /// </summary>
    public const double EqPreampMinDb = -12.0;
    public const double EqPreampMaxDb = 6.0;

    /// <summary>
    /// Folds the user pre-amp (dB relative to native) into a resolved VLC
    /// preamp. Flat's preamp is zeroed so an untouched flat curve registers as
    /// flat and takes the no-filter bypass; a non-zero user pre-amp must ride
    /// the EQ filter instead, so unity make-up is restored before offsetting.
    /// </summary>
    public static float ApplyUserPreamp(float vlcPreampDb, double userPreampDb)
    {
        var user = Math.Clamp(userPreampDb, EqPreampMinDb, EqPreampMaxDb);
        if (Math.Abs(user) < 1e-9) return vlcPreampDb;
        if (vlcPreampDb == 0f) vlcPreampDb = VlcEqUnityPreampDb;
        return (float)Math.Clamp(vlcPreampDb + user, -20.0, 20.0);
    }

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
    /// Convert a 10-band graphic curve into parametric bands (one band per
    /// graphic frequency at the default Q) whose COMPOSITE response reproduces
    /// the curve at the graphic frequencies. Used to migrate pre-parametric
    /// settings and to seed the band editor from a VLC preset.
    ///
    /// The band gains are solved, not copied: adjacent Q=1.41 peaking filters
    /// overlap (60–600 Hz sit ~1 octave apart, 12–16 kHz ~0.2), and
    /// <see cref="MapToGraphicBands"/> SUMS their responses — copying the
    /// graphic dB values straight into the bands overshot the curve by up to
    /// ~14 dB on bass-heavy presets, so the first slider tweak after picking a
    /// preset blew the level up ("custom EQ goes to 300% volume").
    /// </summary>
    public static List<ParametricEqBand> FromGraphicBands(float[]? graphicGains)
    {
        var n = GraphicBandFrequencies.Length;
        var target = new double[n];
        for (var i = 0; i < n; i++)
        {
            var gain = graphicGains is { Length: 10 } ? graphicGains[i] : 0f;
            target[i] = Math.Clamp(gain, MinGainDb, MaxGainDb);
        }

        // Damped fixed-point iteration: nudge each band by the curve error at
        // its own frequency. Converges below 0.05 dB within ~20 rounds for
        // every VLC preset; a flat curve exits on the first pass.
        var gains = (double[])target.Clone();
        for (var iter = 0; iter < 40; iter++)
        {
            var maxErr = 0.0;
            var errors = new double[n];
            for (var i = 0; i < n; i++)
            {
                var composite = 0.0;
                for (var j = 0; j < n; j++)
                    composite += PeakingResponseDb(GraphicBandFrequencies[j], gains[j], DefaultQ, GraphicBandFrequencies[i]);
                errors[i] = target[i] - composite;
                maxErr = Math.Max(maxErr, Math.Abs(errors[i]));
            }
            if (maxErr < 0.05) break;
            for (var i = 0; i < n; i++)
                gains[i] = Math.Clamp(gains[i] + 0.6 * errors[i], MinGainDb, MaxGainDb);
        }

        var bands = new List<ParametricEqBand>(n);
        for (var i = 0; i < n; i++)
        {
            bands.Add(new ParametricEqBand
            {
                FrequencyHz = GraphicBandFrequencies[i],
                GainDb = gains[i],
                Q = DefaultQ,
            });
        }
        return bands;
    }
}
