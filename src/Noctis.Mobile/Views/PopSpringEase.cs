using Noctis.Helpers;

namespace Noctis.Mobile.Views;

/// <summary>
/// The pop-ups' open curve: a damped spring's step response (<see cref="LineSpring"/>) squeezed
/// into the transition's run, so a card shoots most of the way out of what was tapped at once,
/// runs a hair past its size and settles back (Apple's context menus). The spring is tuned in
/// the run's own time: by the end its swing is under <see cref="Tolerance"/>, so the last frame's
/// snap to exactly 1 cannot be seen. With <see cref="Zeta"/> 0.75 it overshoots 2.8 % of the
/// distance (0.85 → 1 peaks at a scale of 1.004).
/// </summary>
public sealed class PopSpringEase : Avalonia.Animation.Easings.Easing
{
    /// <summary>Damping ratio: under 1 swings past the end, 1 would not.</summary>
    public const double Zeta = 0.75;
    /// <summary>What is left of the swing when the run ends.</summary>
    public const double Tolerance = 0.002;

    private static readonly LineSpring Spring = new(Zeta, NaturalFrequency());

    // The frequency (per run) whose envelope has decayed to Tolerance at the end of the run.
    private static double NaturalFrequency()
    {
        var amplitude = 1 + Zeta / Math.Sqrt(1 - Zeta * Zeta);
        return Math.Log(amplitude / Tolerance) / Zeta;
    }

    public override double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        return Spring.Step(progress);
    }
}
