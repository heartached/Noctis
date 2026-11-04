// Port of CubicSplineInterpolator.kt from spicylyrics/mobile (AGPL-3.0)
// Original cubic-spline package is MIT licensed.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Noctis.Controls.SpicyLyrics;

public readonly record struct SpicyLyricsAnimationPoint(double Time, double Value);

/// <summary>
/// Natural cubic spline interpolation over (time, value) control points.
/// Faithful port of CubicSplineInterpolator.kt.
/// </summary>
public sealed class SpicyLyricsSpline
{
    private readonly double[] _xs;
    private readonly double[] _ys;
    private readonly double[] _ks;

    public SpicyLyricsSpline(IEnumerable<SpicyLyricsAnimationPoint> points)
    {
        var sorted = points.OrderBy(p => p.Time).ToArray();
        if (sorted.Length < 2)
            throw new ArgumentException("Need at least 2 points for interpolation", nameof(points));

        _xs = new double[sorted.Length];
        _ys = new double[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            _xs[i] = sorted[i].Time;
            _ys[i] = sorted[i].Value;
        }

        _ks = NaturalSplineCoefficients(_xs, _ys);
    }

    public static SpicyLyricsSpline FromPoints(params (double Time, double Value)[] points)
        => new(points.Select(p => new SpicyLyricsAnimationPoint(p.Time, p.Value)));

    /// <summary>
    /// Evaluates the spline at a given time t, clamped to the [first, last] time range.
    /// </summary>
    public double At(double t)
    {
        var clamped = Math.Clamp(t, _xs[0], _xs[^1]);

        var i = 1;
        while (i < _xs.Length - 1 && _xs[i] < clamped)
            i++;
        var segIdx = i - 1;

        var dx = _xs[segIdx + 1] - _xs[segIdx];
        if (Math.Abs(dx) < 1e-9)
            return _ys[segIdx];

        var localT = (clamped - _xs[segIdx]) / dx;
        var a = _ks[segIdx] * dx - (_ys[segIdx + 1] - _ys[segIdx]);
        var b = -_ks[segIdx + 1] * dx + (_ys[segIdx + 1] - _ys[segIdx]);

        return (1.0 - localT) * _ys[segIdx] + localT * _ys[segIdx + 1] +
               localT * (1.0 - localT) * (a * (1.0 - localT) + b * localT);
    }

    private static double[] NaturalSplineCoefficients(double[] xs, double[] ys)
    {
        var n = xs.Length;
        var ks = new double[n];

        if (n == 2)
        {
            var slope = (ys[1] - ys[0]) / (xs[1] - xs[0]);
            ks[0] = slope;
            ks[1] = slope;
            return ks;
        }

        var a = new double[n];
        var b = new double[n];
        var c = new double[n];
        var d = new double[n];

        a[0] = 0;
        b[0] = 2.0 / (xs[1] - xs[0]);
        c[0] = 1.0 / (xs[1] - xs[0]);
        d[0] = 3.0 * (ys[1] - ys[0]) / ((xs[1] - xs[0]) * (xs[1] - xs[0]));

        for (var i = 1; i < n - 1; i++)
        {
            var dx0 = xs[i] - xs[i - 1];
            var dx1 = xs[i + 1] - xs[i];
            a[i] = 1.0 / dx0;
            b[i] = 2.0 * (1.0 / dx0 + 1.0 / dx1);
            c[i] = 1.0 / dx1;
            d[i] = 3.0 * ((ys[i] - ys[i - 1]) / (dx0 * dx0) + (ys[i + 1] - ys[i]) / (dx1 * dx1));
        }

        var last = n - 1;
        var dxLast = xs[last] - xs[last - 1];
        a[last] = 1.0 / dxLast;
        b[last] = 2.0 / dxLast;
        c[last] = 0;
        d[last] = 3.0 * (ys[last] - ys[last - 1]) / (dxLast * dxLast);

        // Forward sweep
        for (var i = 1; i < n; i++)
        {
            var m = a[i] / b[i - 1];
            b[i] -= m * c[i - 1];
            d[i] -= m * d[i - 1];
        }

        // Back substitution
        ks[last] = d[last] / b[last];
        for (var i = n - 2; i >= 0; i--)
        {
            ks[i] = (d[i] - c[i] * ks[i + 1]) / b[i];
        }

        return ks;
    }
}
