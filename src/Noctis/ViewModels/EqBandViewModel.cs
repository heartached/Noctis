using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// One editable parametric EQ band (frequency / gain / Q) in Settings.
/// Edits are clamped to the ParametricEqMath ranges and bubbled to the
/// SettingsViewModel via the change callback.
/// </summary>
public partial class EqBandViewModel : ObservableObject
{
    private readonly Action _onEdited;

    public EqBandViewModel(double frequencyHz, double gainDb, double q, Action onEdited)
    {
        _onEdited = onEdited;
        _frequencyHz = Math.Clamp(frequencyHz, ParametricEqMath.MinFrequencyHz, ParametricEqMath.MaxFrequencyHz);
        _gainDb = Math.Clamp(gainDb, ParametricEqMath.MinGainDb, ParametricEqMath.MaxGainDb);
        _q = Math.Clamp(q, ParametricEqMath.MinQ, ParametricEqMath.MaxQ);
    }

    [ObservableProperty] private double _frequencyHz;
    [ObservableProperty] private double _gainDb;
    [ObservableProperty] private double _q;

    /// <summary>Compact frequency label, e.g. "60 Hz" / "1.0 kHz".</summary>
    public string FrequencyLabel => FrequencyHz >= 1000
        ? $"{FrequencyHz / 1000.0:0.#} kHz"
        : $"{FrequencyHz:0} Hz";

    public string GainLabel => $"{GainDb:+0.0;-0.0;0.0} dB";

    partial void OnFrequencyHzChanged(double value)
    {
        var clamped = Math.Clamp(value, ParametricEqMath.MinFrequencyHz, ParametricEqMath.MaxFrequencyHz);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            FrequencyHz = clamped;
            return;
        }
        OnPropertyChanged(nameof(FrequencyLabel));
        _onEdited();
    }

    partial void OnGainDbChanged(double value)
    {
        var clamped = Math.Clamp(value, ParametricEqMath.MinGainDb, ParametricEqMath.MaxGainDb);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            GainDb = clamped;
            return;
        }
        OnPropertyChanged(nameof(GainLabel));
        _onEdited();
    }

    partial void OnQChanged(double value)
    {
        var clamped = Math.Clamp(value, ParametricEqMath.MinQ, ParametricEqMath.MaxQ);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            Q = clamped;
            return;
        }
        _onEdited();
    }
}
