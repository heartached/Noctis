using System;

namespace Noctis.Helpers;

/// <summary>
/// A damped spring evaluated in closed form at any time, so a value can be read at a frame's
/// timestamp (dropped frames skip ahead, they never slow the motion down) and retargeted
/// mid-flight from where it is AND how fast it is moving: a press released half-way springs
/// back without a kink. A value type with no allocations; <c>zeta</c> 1 is critically damped
/// (an ease-out with no overshoot), below 1 overshoots once and settles.
/// </summary>
internal struct Spring
{
    private double _x0, _v0, _t0, _omega, _zeta;

    /// <summary>Where the spring is heading.</summary>
    public double Target { get; private set; }

    /// <summary>True once the value has been snapped onto <see cref="Target"/>.</summary>
    public bool AtRest { get; private set; }

    public Spring(double value)
    {
        _x0 = Target = value;
        _v0 = _t0 = 0;
        _omega = 1;
        _zeta = 1;
        AtRest = true;
    }

    /// <summary>Jumps to <paramref name="value"/> with no motion.</summary>
    public void Snap(double value)
    {
        _x0 = Target = value;
        _v0 = 0;
        AtRest = true;
    }

    /// <summary>Heads for <paramref name="target"/> from the value and velocity at
    /// <paramref name="now"/> (seconds).</summary>
    public void Retarget(double now, double target, double omega, double zeta)
    {
        var (x, v) = Evaluate(now);
        _x0 = x;
        _v0 = v;
        _t0 = now;
        _omega = omega;
        _zeta = Math.Clamp(zeta, 0.05, 1);
        Target = target;
        AtRest = false;
    }

    /// <summary>Restarts from <paramref name="from"/> at rest (velocity 0) towards
    /// <paramref name="target"/>.</summary>
    public void Launch(double now, double from, double target, double omega, double zeta)
    {
        _x0 = from;
        _v0 = 0;
        _t0 = now;
        _omega = omega;
        _zeta = Math.Clamp(zeta, 0.05, 1);
        Target = target;
        AtRest = false;
    }

    /// <summary>The value and velocity at <paramref name="now"/>.</summary>
    public readonly (double Value, double Velocity) Evaluate(double now)
    {
        if (AtRest) return (Target, 0);
        var t = Math.Max(0, now - _t0);
        var y0 = _x0 - Target;
        var w = _omega;
        if (_zeta >= 1)
        {
            var e = Math.Exp(-w * t);
            var c = _v0 + w * y0;
            return (Target + (y0 + c * t) * e, (_v0 - w * c * t) * e);
        }
        var a = _zeta * w;
        var wd = w * Math.Sqrt(1 - _zeta * _zeta);
        var decay = Math.Exp(-a * t);
        var cos = Math.Cos(wd * t);
        var sin = Math.Sin(wd * t);
        var b = (_v0 + a * y0) / wd;
        var y = decay * (y0 * cos + b * sin);
        var v = decay * (_v0 * cos - (a * _v0 + w * w * y0) / wd * sin);
        return (Target + y, v);
    }

    /// <summary>
    /// The value at <paramref name="now"/>; once within <paramref name="epsilon"/> of the target
    /// and nearly still, the spring snaps onto the target exactly and reports <see cref="AtRest"/>,
    /// so a run always ends on the precise rest value (no drift after many interruptions).
    /// </summary>
    public double Step(double now, double epsilon = 0.0008)
    {
        if (AtRest) return Target;
        var (x, v) = Evaluate(now);
        if (Math.Abs(x - Target) < epsilon && Math.Abs(v) < epsilon * 20)
        {
            Snap(Target);
            return Target;
        }
        return x;
    }
}
