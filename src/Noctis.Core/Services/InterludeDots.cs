namespace Noctis.Services;

/// <summary>
/// The "..." dots of an intro or instrumental interlude fill one after another with the
/// gap's progress, so the wait reads as a countdown: Joy Tunes (Billie Jean capture) lights
/// its three dots in turn across the gap. Each dot rises from <see cref="RestLevel"/> to
/// full over its third of the gap on a smoothstep. Pure, unit-tested.
/// </summary>
public static class InterludeDots
{
    /// <summary>Dots in the row.</summary>
    public const int Count = 3;

    /// <summary>Opacity of a dot that has not started filling.</summary>
    public const double RestLevel = 0.35;

    /// <summary>
    /// Opacity of dot <paramref name="index"/> at gap <paramref name="progress"/> (0..1).
    /// NaN progress (the dots are not the active line) draws every dot at full opacity:
    /// the whole row is dimmed by its line opacity instead, like any inactive line.
    /// </summary>
    public static double DotLevel(double progress, int index)
    {
        if (double.IsNaN(progress)) return 1.0;
        var x = Math.Clamp(progress * Count - index, 0, 1);
        var eased = x * x * (3 - 2 * x);
        return RestLevel + (1 - RestLevel) * eased;
    }
}
