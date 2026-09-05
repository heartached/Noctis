using System;

namespace Noctis.Models;

/// <summary>How the live audio visualizer draws the spectrum on the lyrics page.</summary>
public enum VisualizerStyle
{
    /// <summary>Frequency bars rising from the bottom edge.</summary>
    Bars,
    /// <summary>Bars mirrored about the vertical centre — a symmetric waveform look.</summary>
    Mirror,
    /// <summary>A smooth filled curve along the bottom edge.</summary>
    Wave,
}

public static class VisualizerStyles
{
    public const string DefaultSetting = nameof(VisualizerStyle.Bars);

    /// <summary>Parses a stored style name; unknown/blank falls back to <see cref="VisualizerStyle.Bars"/>.
    /// Names only — a stray numeral must not be read as an enum ordinal.</summary>
    public static VisualizerStyle Parse(string? setting)
    {
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return VisualizerStyle.Bars;
        return Enum.TryParse<VisualizerStyle>(name, ignoreCase: true, out var style) && Enum.IsDefined(style)
            ? style
            : VisualizerStyle.Bars;
    }
}
