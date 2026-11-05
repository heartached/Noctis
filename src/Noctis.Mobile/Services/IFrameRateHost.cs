namespace Noctis.Mobile.Services;

/// <summary>
/// Settings › Appearance › Smooth Motion. On, the app asks the screen for its fastest refresh
/// rate (90 or 120 Hz on phones that have one); off, for 60 Hz. Adaptive-refresh phones run an
/// app that asks for nothing at whatever rate their heuristics guess, often 60 Hz.
/// AndroidFrameRate asks the platform; tests use a fake.
/// </summary>
public interface IFrameRateHost
{
    void Request(bool smoothMotion);
}

/// <summary>One of the screen's modes (Android's Display.Mode): its id, size in pixels and rate.</summary>
public readonly record struct DisplayModeInfo(int Id, int Width, int Height, float RefreshRate);

/// <summary>
/// Which display mode and surface rate to ask for, from the modes the screen offers. Kept off
/// Android so the choice is tested: only modes at the current resolution are candidates (a
/// mode at another size would rescale the whole screen), the fastest of them for Smooth
/// Motion and the one nearest 60 Hz for Standard.
/// </summary>
public static class FrameRatePolicy
{
    public const float StandardRate = 60f;

    /// <summary>The mode to ask for, or null when no mode has the current size (ask for none).</summary>
    public static DisplayModeInfo? PickMode(IReadOnlyList<DisplayModeInfo> modes, DisplayModeInfo current, bool smoothMotion)
    {
        DisplayModeInfo? best = null;
        foreach (var mode in modes)
        {
            if (mode.Width != current.Width || mode.Height != current.Height) continue;
            if (best is not { } b) { best = mode; continue; }
            if (smoothMotion ? mode.RefreshRate > b.RefreshRate : IsNearerStandard(mode.RefreshRate, b.RefreshRate))
                best = mode;
        }
        return best;
    }

    /// <summary>The rate voted on the app's drawing surface: the chosen mode's for Smooth Motion
    /// (0, no vote, when there is none), always 60 for Standard.</summary>
    public static float SurfaceRate(DisplayModeInfo? mode, bool smoothMotion) =>
        smoothMotion ? mode?.RefreshRate ?? 0f : StandardRate;

    // Nearer 60 wins; at the same distance the slower (59.94 over 60.06, 48 over 72).
    private static bool IsNearerStandard(float candidate, float best)
    {
        var a = Math.Abs(candidate - StandardRate);
        var b = Math.Abs(best - StandardRate);
        return a < b - 0.01f || (Math.Abs(a - b) <= 0.01f && candidate < best);
    }
}
