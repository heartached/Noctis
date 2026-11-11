using System.Text.Json.Serialization;

namespace Noctis.Models;

/// <summary>
/// A user-saved equalizer preset (GitHub #95): the parametric curve plus the pre-amp,
/// recalled exactly as saved. Persisted in settings.json.
/// </summary>
public class UserEqPreset : IJsonExtensible
{
    /// <summary>Members a newer Noctis saved that this build does not know, written back
    /// unchanged so an older build sharing the data folder does not erase them (GitHub #230).</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

    /// <summary>Display name. Never collides with a built-in preset name, "Custom" or "None".</summary>
    public string Name { get; set; } = "";

    /// <summary>The parametric bands at save time.</summary>
    public List<ParametricEqBand> Bands { get; set; } = new();

    /// <summary>EQ pre-amp in dB relative to native, as in <see cref="AppSettings.EqPreampDb"/>.</summary>
    public double PreampDb { get; set; }
}
