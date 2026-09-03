using System;

namespace Noctis.Helpers;

/// <summary>One drifting copy of the blurred artwork: translate, rotate, scale, opacity.</summary>
public readonly record struct FlowLayerPose(double X, double Y, double AngleDeg, double Scale, double Opacity);

/// <summary>Everything the flowing-artwork background draws for one frame.</summary>
public readonly record struct FlowFrame(FlowLayerPose Layer1, FlowLayerPose Layer2, double BackdropScale, double GlowOpacity);

/// <summary>
/// Pure math for the Apple-Music-style flowing artwork background, shared by the
/// lyrics page, the lyrics panel and the mini player so the three surfaces move
/// identically. Two extra copies of the pre-blurred cover slowly rotate and drift
/// over the static one (so the background keeps the artwork's own colours — no
/// palette blobs), and the whole backdrop breathes on the beat pulse.
/// </summary>
public static class FlowingArtworkMotion
{
    /// <summary>Backdrop scale added per unit of beat pulse.</summary>
    public const double PulseScale = 0.035;
    /// <summary>White glow opacity added per unit of beat pulse.</summary>
    public const double PulseGlow = 0.08;

    // Rotation rates (deg/s): a full turn takes minutes, opposite directions so
    // the two copies never line up for long.
    private const double Layer1DegPerSec = 2.4;
    private const double Layer2DegPerSec = -1.7;

    // Smoothing time constants for the on-screen pulse: snappy attack so the beat
    // reads as a hit, slower release so it settles instead of flickering.
    public const double AttackMs = 28;
    public const double ReleaseMs = 150;

    /// <summary>
    /// The scale a centre-rotated UniformToFill copy needs so no corner of the
    /// viewport ever shows through at any angle: viewport diagonal over its short side.
    /// </summary>
    public static double CoverScale(double w, double h)
    {
        if (w <= 0 || h <= 0 || double.IsNaN(w) || double.IsNaN(h)) return 1;
        return Math.Sqrt(w * w + h * h) / Math.Min(w, h);
    }

    /// <summary>Poses for time <paramref name="t"/> (seconds) in a w×h viewport at beat <paramref name="pulse"/> (0..1).</summary>
    public static FlowFrame Evaluate(double t, double w, double h, double pulse)
    {
        pulse = double.IsNaN(pulse) ? 0 : Math.Clamp(pulse, 0, 1);
        var cover = CoverScale(w, h);

        // Drift amplitudes are fractions of the viewport so the motion scales with
        // the surface; the extra scale headroom covers the drift so edges never show.
        const double drift1X = 0.06, drift1Y = 0.05, drift2X = 0.05, drift2Y = 0.06;
        var scale1 = cover * (1.0 + drift1X + drift1Y) * (1.02 + 0.03 * Math.Sin(t * 0.090 + 1.0));
        var scale2 = cover * (1.0 + drift2X + drift2Y) * (1.05 + 0.03 * Math.Sin(t * 0.070 + 2.5));

        var layer1 = new FlowLayerPose(
            Math.Sin(t * 0.110) * w * drift1X,
            Math.Cos(t * 0.083) * h * drift1Y,
            t * Layer1DegPerSec,
            scale1,
            0.50 + 0.12 * Math.Sin(t * 0.131));
        var layer2 = new FlowLayerPose(
            Math.Sin(t * 0.071 + 2.1) * w * drift2X,
            Math.Cos(t * 0.127 + 0.7) * h * drift2Y,
            180 + t * Layer2DegPerSec,
            scale2,
            0.36 + 0.10 * Math.Sin(t * 0.101 + 2.6));

        return new FlowFrame(layer1, layer2, 1.0 + PulseScale * pulse, PulseGlow * pulse);
    }

    /// <summary>Asymmetric follower: fast toward a higher target, slow toward a lower one.</summary>
    public static double Smooth(double current, double target, double dtMs)
    {
        if (dtMs <= 0 || double.IsNaN(dtMs)) return current;
        var tau = target > current ? AttackMs : ReleaseMs;
        var k = 1.0 - Math.Exp(-dtMs / tau);
        return current + (target - current) * k;
    }
}
