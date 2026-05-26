using System;

namespace Noctis.Helpers;

/// <summary>
/// Damped-sine spring easing. Produces a small overshoot then settles — closer to
/// AMLL / Apple Music motion than a plain cubic ease. Defaults are tuned for
/// active-line scale/lift and per-word lift; lower <see cref="Damping"/> = bouncier,
/// higher <see cref="Frequency"/> = faster wobble.
/// </summary>
public sealed class SpringEase : Avalonia.Animation.Easings.Easing
{
    /// <summary>Higher = settles faster, less bounce. Range ~3..10.</summary>
    public double Damping { get; set; } = 5.5;

    /// <summary>Higher = more oscillation cycles. Range ~4..12.</summary>
    public double Frequency { get; set; } = 7.5;

    public override double Ease(double progress)
    {
        if (progress <= 0.0) return 0.0;
        if (progress >= 1.0) return 1.0;

        // 1 - e^(-d*t) * cos(f*t) — classic damped-sine overshoot.
        return 1.0 - Math.Exp(-Damping * progress) * Math.Cos(Frequency * progress);
    }

    /// <summary>Shared instance for plain-function use (e.g. manual scroll tween).</summary>
    public static double Apply(double t, double damping = 5.5, double frequency = 7.5)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        return 1.0 - Math.Exp(-damping * t) * Math.Cos(frequency * t);
    }
}
