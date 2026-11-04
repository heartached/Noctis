// Port of SpringSimulation.kt from spicylyrics/mobile (AGPL-3.0),
// which was ported from Fractality's spr.lua (MIT).

using System;

namespace Noctis.Controls.SpicyLyrics;

/// <summary>
/// A damped spring, matching SpringSimulation.kt.
/// Frequency is stored in Hz and converted to rad/s (2 * PI * frequency) inside Step.
/// Delta time is in seconds.
/// </summary>
public sealed class SpicyLyricsSpring
{
    private const double Epsilon = 1e-5;
    private const double TwoPi = Math.PI * 2.0;

    private double _damping;
    private double _frequency;
    private double _goal;
    private double _position;
    private double _velocity;

    public SpicyLyricsSpring(
        double initialPosition,
        double frequency,
        double damping)
    {
        _position = initialPosition;
        _goal = initialPosition;
        _velocity = 0;
        _frequency = frequency;
        _damping = damping;
    }

    public double Position => _position;
    public double Velocity => _velocity;
    public double Goal => _goal;
    public double Frequency => _frequency;
    public double Damping => _damping;

    public void SetGoal(double target, bool replacePosition = false)
    {
        _goal = target;
        if (replacePosition)
        {
            _position = target;
            _velocity = 0;
        }
    }

    /// <summary>Snaps the spring's position (velocity zeroed) without changing the goal.</summary>
    public void ResetTo(double currentValue)
    {
        _position = currentValue;
        _velocity = 0;
    }

    public double Step(double dt)
    {
        if (dt <= 0 || !double.IsFinite(dt))
            return _position;

        var d = _damping;
        var f = _frequency * TwoPi;
        var g = _goal;
        var p = _position;
        var v = _velocity;

        if (Math.Abs(d - 1.0) < 1e-9) // critically damped
        {
            var q = Math.Exp(-f * dt);
            var w = dt * q;

            var c0 = q + w * f;
            var c2 = q - w * f;
            var c3 = w * f * f;

            var o = p - g;
            p = o * c0 + v * w + g;
            v = v * c2 - o * c3;
        }
        else if (d < 1.0) // underdamped
        {
            var q = Math.Exp(-d * f * dt);
            var c = Math.Sqrt(1.0 - d * d);

            var i = Math.Cos(dt * f * c);
            var j = Math.Sin(dt * f * c);

            double z;
            if (c > Epsilon)
            {
                z = j / c;
            }
            else
            {
                var a = dt * f;
                z = a + ((a * a * c * c * c * c / 20.0 - c * c) * a * a * a / 6.0);
            }

            double y;
            if (f * c > Epsilon)
            {
                y = j / (f * c);
            }
            else
            {
                var b = f * c;
                y = dt + ((dt * dt * b * b * b * b / 20.0 - b * b) * dt * dt * dt / 6.0);
            }

            var o = p - g;
            p = (o * (i + z * d) + v * y) * q + g;
            v = (v * (i - z * d) - o * (z * f)) * q;
        }
        else // overdamped
        {
            var c = Math.Sqrt(d * d - 1.0);
            var r1 = -f * (d + c);
            var r2 = -f * (d - c);

            var ec1 = Math.Exp(r1 * dt);
            var ec2 = Math.Exp(r2 * dt);

            var o = p - g;
            var co2 = (v - o * r1) / (2.0 * f * c);
            var co1 = ec1 * (o - co2);

            p = co1 + co2 * ec2 + g;
            v = co1 * r1 + co2 * ec2 * r2;
        }

        _position = p;
        _velocity = v;
        return p;
    }

    public void SetFrequency(double frequency) => _frequency = frequency;
    public void SetDamping(double damping) => _damping = damping;

    public bool CanSleep()
    {
        const double sleepOffsetSqLimit = 1.0 / (3840.0 * 3840.0);
        const double sleepVelocitySqLimit = 1e-4;
        if (_velocity * _velocity > sleepVelocitySqLimit)
            return false;

        var offset = _position - _goal;
        return offset * offset <= sleepOffsetSqLimit;
    }
}
