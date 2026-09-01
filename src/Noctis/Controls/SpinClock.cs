using System;

namespace Noctis.Controls;

/// <summary>
/// Angular-velocity model for the spinning disc/reels in <see cref="MediaArtwork"/>.
///
/// A turntable, not a keyframe animation: velocity eases toward the target speed when
/// <see cref="IsRunning"/> flips on, coasts down when it flips off, and the angle simply
/// stops where the coast ends. A style animation would restart from 0° on every
/// pause/resume, which reads as the disc jumping.
/// </summary>
public sealed class SpinClock
{
    /// <summary>One turn every five seconds — slow enough to read the art, fast enough to
    /// notice it moving.</summary>
    public const double DefaultDegreesPerSecond = 72;

    /// <summary>Below this speed a coasting disc is declared stopped, so the frame loop can end.</summary>
    private const double StopThresholdDegreesPerSecond = 0.5;

    public double TargetDegreesPerSecond { get; set; } = DefaultDegreesPerSecond;

    /// <summary>Time constant of the spin-up ease (seconds).</summary>
    public double SpinUpSeconds { get; set; } = 0.9;

    /// <summary>Time constant of the coast-down ease (seconds) — longer than spin-up, the
    /// way a platter keeps turning after the motor cuts.</summary>
    public double SpinDownSeconds { get; set; } = 1.6;

    public bool IsRunning { get; set; }

    /// <summary>Degrees turned since the clock was created, unwrapped. Anything geared
    /// off the disc (cassette reels at 1.8×) must derive from THIS, not <see cref="Angle"/>:
    /// a ratio applied to the wrapped angle jumps every time the disc passes 360°.</summary>
    public double TotalDegrees { get; private set; }

    /// <summary>Current rotation in degrees, always within [0, 360).</summary>
    public double Angle => Wrap(TotalDegrees);

    public static double Wrap(double degrees)
    {
        var angle = degrees % 360;
        return angle < 0 ? angle + 360 : angle;
    }

    /// <summary>Current angular velocity in degrees per second.</summary>
    public double Velocity { get; private set; }

    /// <summary>True once the disc is neither driven nor coasting — nothing left to draw.</summary>
    public bool IsSettled => !IsRunning && Velocity == 0;

    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0) return;

        var target = IsRunning ? TargetDegreesPerSecond : 0;
        var tau = IsRunning ? SpinUpSeconds : SpinDownSeconds;
        var blend = 1 - Math.Exp(-deltaSeconds / tau);
        Velocity += (target - Velocity) * blend;

        if (!IsRunning && Math.Abs(Velocity) < StopThresholdDegreesPerSecond)
            Velocity = 0;

        TotalDegrees += Velocity * deltaSeconds;
    }
}
