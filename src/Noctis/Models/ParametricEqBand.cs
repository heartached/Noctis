namespace Noctis.Models;

/// <summary>
/// One band of the parametric equalizer: a peaking filter described by its
/// center frequency, gain, and Q (bandwidth). Persisted in settings.json.
/// </summary>
public class ParametricEqBand
{
    /// <summary>Center frequency in Hz (20–20000).</summary>
    public double FrequencyHz { get; set; } = 1000;

    /// <summary>Peak gain in dB (-12 to +12).</summary>
    public double GainDb { get; set; }

    /// <summary>Filter Q / sharpness (0.1–10). Higher = narrower band.</summary>
    public double Q { get; set; } = 1.41;
}
