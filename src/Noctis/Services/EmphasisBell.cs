namespace Noctis.Services;

/// <summary>
/// Pure math for the held-note (emphasis) glow envelope, following AMLL's
/// initEmphasizeAnimation: a bell that rises over the first half of the word and
/// releases over the second half, whose peak intensity grows with how long the
/// note is actually held — a 1s hold barely glows, a 4s note blooms.
/// Deterministic and Avalonia-free so it can be unit-tested.
/// </summary>
public static class EmphasisBell
{
    /// <summary>
    /// Bell envelope over word progress x ∈ [0..1]: 0 at both ends, 1 at the middle.
    /// Outside [0..1] (pre-roll, overshoot, inert sentinels) it is 0. AMLL's
    /// empEasing: rise via cubic-bezier(0.2,0.4,0.58,1.0) over the first half,
    /// release via 1 − cubic-bezier(0.3,0.0,0.58,1.0) over the second half.
    /// </summary>
    public static double Envelope(double x)
    {
        if (x <= 0 || x >= 1) return 0;
        return x < 0.5
            ? Bezier(0.2, 0.4, 0.58, 1.0, x / 0.5)
            : 1.0 - Bezier(0.3, 0.0, 0.58, 1.0, (x - 0.5) / 0.5);
    }

    /// <summary>
    /// Peak glow opacity for a hold of <paramref name="durationMs"/> (AMLL "blur"
    /// strength: f(du/3000)·0.5 capped at 0.8, with f(x)=x³ below 1 and √x above).
    /// </summary>
    public static double Strength(double durationMs)
    {
        if (durationMs <= 0) return 0;
        var x = durationMs / 3000.0;
        var f = x > 1 ? Math.Sqrt(x) : x * x * x;
        return Math.Min(0.8, f * 0.5);
    }

    /// <summary>Glow opacity at <paramref name="progress"/> for a hold of <paramref name="durationMs"/>.</summary>
    public static double Evaluate(double progress, double durationMs) =>
        Envelope(progress) * Strength(durationMs);

    /// <summary>CSS cubic-bezier easing: y at the curve parameter where x(t) = u.</summary>
    private static double Bezier(double x1, double y1, double x2, double y2, double u)
    {
        if (u <= 0) return 0;
        if (u >= 1) return 1;

        // Newton–Raphson on x(t), with a bisection fallback (x(t) is monotone for
        // valid easing control points).
        var t = u;
        for (var i = 0; i < 8; i++)
        {
            var xe = CubicAt(t, x1, x2) - u;
            if (Math.Abs(xe) < 1e-7) break;
            var dx = CubicDerivativeAt(t, x1, x2);
            if (Math.Abs(dx) < 1e-6) break;
            t -= xe / dx;
        }
        if (t < 0 || t > 1 || Math.Abs(CubicAt(t, x1, x2) - u) > 1e-4)
        {
            double lo = 0, hi = 1;
            for (var i = 0; i < 40; i++)
            {
                t = (lo + hi) / 2;
                if (CubicAt(t, x1, x2) < u) lo = t;
                else hi = t;
            }
        }
        return CubicAt(t, y1, y2);
    }

    private static double CubicAt(double t, double a1, double a2) =>
        ((1 - 3 * a2 + 3 * a1) * t * t * t) + ((3 * a2 - 6 * a1) * t * t) + (3 * a1 * t);

    private static double CubicDerivativeAt(double t, double a1, double a2) =>
        (3 * (1 - 3 * a2 + 3 * a1) * t * t) + (2 * (3 * a2 - 6 * a1) * t) + (3 * a1);
}
