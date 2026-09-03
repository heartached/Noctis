using System;

namespace Noctis.Helpers;

/// <summary>
/// The one motion curve every lyric line change uses — the scroll glide that brings
/// the new line to the anchor AND the line's own scale transition (XAML
/// <see cref="LineMotionEase"/>) share it, so size and position arrive together
/// instead of the line popping to full size before the list has started to move.
///
/// Spring step response, damping ratio <see cref="Zeta"/> = 0.8, settled to 1% at
/// p = 1 and normalized to end exactly at 1. Opens at zero velocity but accelerates at
/// once (27% travelled at 15% of the duration; smootherstep managed 3%), and arrives
/// CRISPLY: a critically damped spring was tried first and its exponential tail left
/// ~3.5px of sub-threshold creep after the eye had read the line as stopped (the
/// "stops, then drifts again" report). At ζ = 0.8 both the creep after the perceived
/// stop and the overshoot are 0.7px on a 131px line change — below a pixel either
/// way (ζ = 0.85: 1.5px creep; ζ = 0.75: 2.5px overshoot). Pinned by LyricsMotionTests.
/// </summary>
public static class LineMotion
{
    /// <summary>Length of a line-change motion. The XAML line transitions carry the
    /// same value ("0:0:0.65") — pinned by LyricsMotionTests.</summary>
    public const int DurationMs = 650;

    /// <summary>Damping ratio. 1 = critically damped (creeps), lower = bouncier;
    /// 0.8 is where creep-after-stop and overshoot are both under a pixel.</summary>
    public const double Zeta = 0.8;

    // Natural frequency such that the envelope e^(−ζωₙp) / √(1−ζ²) reaches 1% at p = 1.
    private static readonly double Wn = -Math.Log(0.01 * Math.Sqrt(1 - Zeta * Zeta)) / Zeta;
    private static readonly double Wd = Wn * Math.Sqrt(1 - Zeta * Zeta);
    private static readonly double Norm = Raw(1);

    private static double Raw(double p)
    {
        var env = Math.Exp(-Zeta * Wn * p);
        return 1 - env * (Math.Cos(Wd * p) + Zeta / Math.Sqrt(1 - Zeta * Zeta) * Math.Sin(Wd * p));
    }

    /// <summary>Normalized progress → eased fraction; 0 at 0, exactly 1 at 1, clamped outside.</summary>
    public static double Ease(double p)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;
        return Raw(p) / Norm;
    }

    /// <summary>Time at which the motion has covered half its travel — the moment the
    /// eye reads the line as "arrived". The line-only lyric lead is derived from it.</summary>
    public static readonly double HalfTravelMs = SolveHalfTravel();

    /// <summary>
    /// Limits one cascade line's lag relative to the line above it. Lines below the
    /// active one lag the base glide by a per-line delay; the raw lag difference
    /// between neighbours peaks at ~13% of the travel (this curve's steepest slope ×
    /// the 35ms delay), which on a three-row line exceeds the old 16px hard clamp by
    /// 9px. A hard clamp is a velocity kink — the line rides the one above for a few
    /// frames, then snaps back to its own curve — right at peak speed.
    ///
    /// A <em>negative</em> step means the lower line is displaced toward the one above
    /// (the cascade is closing: a backward glide). That is the only direction lines can
    /// cross, so it keeps the hard bound. A positive step means the lines are spreading
    /// (forward advance): identity up to <paramref name="hardStepPx"/>, then a smooth
    /// saturation that never exceeds twice it — no kink, no fly-apart on multi-line skips.
    /// </summary>
    public static double CascadeStep(double rawLag, double prevLag, double hardStepPx)
    {
        var step = rawLag - prevLag;
        if (step <= hardStepPx)
            return prevLag + Math.Max(step, -hardStepPx);
        var excess = step - hardStepPx;
        return prevLag + hardStepPx + hardStepPx * Math.Tanh(excess / hardStepPx);
    }

    private static double SolveHalfTravel()
    {
        double lo = 0, hi = 1;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (Ease(mid) < 0.5) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2 * DurationMs;
    }
}

/// <summary>XAML-usable easing over <see cref="LineMotion.Ease"/>.</summary>
public sealed class LineMotionEase : Avalonia.Animation.Easings.Easing
{
    public override double Ease(double progress) => LineMotion.Ease(progress);
}
