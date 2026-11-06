namespace Noctis.Models;

/// <summary>
/// GitHub #185: the persisted values of <see cref="AppSettings.AnimatedArtworkQuality"/>, the
/// same labels the Edit Info chooser's quality pills carry ("1080p", "2160p").
/// </summary>
public static class AnimatedArtworkQualities
{
    public const string P1080 = "1080p";
    public const string P2160 = "2160p";
    public const string Default = P1080;

    public static IReadOnlyList<string> All { get; } = new[] { P1080, P2160 };

    /// <summary>A known value, else the default (a hand-edited or missing setting).</summary>
    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), P2160, StringComparison.OrdinalIgnoreCase) ? P2160 : P1080;

    /// <summary>True when the setting asks for the 2160p loop.</summary>
    public static bool PrefersMax(string? value) => Normalize(value) == P2160;
}
