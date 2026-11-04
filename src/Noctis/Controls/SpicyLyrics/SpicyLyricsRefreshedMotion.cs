// Port of RefreshedMotion.kt from spicylyrics/mobile (AGPL-3.0)

using System;

namespace Noctis.Controls.SpicyLyrics;

/// <summary>
/// Cubic Bézier curve evaluator matching Compose's CubicBezierEasing.
/// </summary>
public sealed class CubicBezier
{
    private readonly double _x1, _y1, _x2, _y2;

    public CubicBezier(double x1, double y1, double x2, double y2)
    {
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    public double Transform(double fraction)
    {
        if (fraction <= 0) return 0;
        if (fraction >= 1) return 1;

        // Newton-Raphson to solve for t given x = fraction
        var t = fraction;
        for (var i = 0; i < 8; i++)
        {
            var x = SampleX(t) - fraction;
            if (Math.Abs(x) < 1e-6)
                break;
            var dx = SampleDerivativeX(t);
            if (Math.Abs(dx) < 1e-6)
                break;
            t -= x / dx;
        }

        t = Math.Clamp(t, 0.0, 1.0);
        return SampleY(t);
    }

    private double SampleX(double t)
    {
        var oneMinusT = 1.0 - t;
        return 3.0 * oneMinusT * oneMinusT * t * _x1 +
               3.0 * oneMinusT * t * t * _x2 +
               t * t * t;
    }

    private double SampleY(double t)
    {
        var oneMinusT = 1.0 - t;
        return 3.0 * oneMinusT * oneMinusT * t * _y1 +
               3.0 * oneMinusT * t * t * _y2 +
               t * t * t;
    }

    private double SampleDerivativeX(double t)
    {
        var oneMinusT = 1.0 - t;
        return 3.0 * (1.0 - 3.0 * t + 3.0 * t * t) * 0.0 + // (0 is omitted)
               3.0 * (1.0 - 4.0 * t + 3.0 * t * t) * _x1 +
               3.0 * (2.0 * t - 3.0 * t * t) * _x2 +
               3.0 * t * t;
    }
}

/// <summary>
/// Faithful port of RefreshedMotion.kt.
/// Times are in milliseconds, offsets in em units of the lyric font size.
/// </summary>
public static class SpicyLyricsRefreshedMotion
{
    public const double Alpha = 1.0;
    public const double GapEm = 0.62;
    public const double EdgeEm = 0.55;
    public const double DefaultGapEm = 0.19;

    private const double MinMs = 1000.0;
    private const double Stagger = 1.0 / 2.5;
    private const double Scale = 0.1;
    private const double Spread = 0.03;
    private const double Lift = 0.025;
    private const double MaxAmount = 1.2;
    private const double Radius = 0.3;
    private const double MaxRadius = 0.3;
    private const double MaxBlur = 0.8;

    private const double Float = 0.05;
    private const double FloatLeadMs = 400.0;
    private const double FloatStretch = 1.4;
    private const double DimAlpha = 0.4;

    private static readonly CubicBezier EmphasisIn = new(0.2, 0.4, 0.58, 1.0);
    private static readonly CubicBezier EmphasisOut = new(0.3, 0.0, 0.58, 1.0);

    public static double Clamp(double x) => Math.Clamp(x, 0.0, 1.0);
    public static double Smoothstep(double x) => Clamp(x) is var c ? c * c * (3.0 - 2.0 * c) : 0.0;
    public static double Ease(double x) => 0.5 - 0.5 * Math.Cos(Math.PI * Clamp(x));

    public readonly record struct Strength(double Amount, double Blur, double Duration);

    public static Strength WaveStrength(double durationMs, bool lastWord)
    {
        static double Grow(double x) => x > 1.0 ? Math.Sqrt(x) : x * x * x;

        var ms = Math.Max(MinMs, durationMs);
        var ratio = Math.Clamp(ms / 2000.0, 0.5, 2.5);
        var growAmount = ratio >= 1.0 ? Math.Sqrt(ratio) : (0.5 + 0.5 * ratio);
        var growBlur = ratio >= 1.0 ? Math.Sqrt(ratio) : (0.4 + 0.6 * ratio);
        var amount = growAmount * 0.8;
        var blur = growBlur * 0.75;

        if (lastWord)
        {
            amount *= 1.4;
            blur *= 1.3;
            ms *= 1.2;
        }

        return new Strength(
            Math.Min(MaxAmount, amount),
            Math.Min(MaxBlur, blur),
            ms);
    }

    /// <summary>
    /// 0 to 1 and back over [x] from 0 to 1: up over the first half, down over the second.
    /// </summary>
    public static double Emphasis(double x)
    {
        if (x <= 0 || x >= 1) return 0.0;
        return x < 0.5
            ? EmphasisIn.Transform(x / 0.5)
            : 1.0 - EmphasisOut.Transform((x - 0.5) / 0.5);
    }

    public readonly record struct LetterPose(
        double OffsetXEm,
        double OffsetYEm,
        double Scale,
        double Glow,
        double RadiusEm);

    public static LetterPose EvaluateLetter(
        double t,
        double wordStartMs,
        int index,
        int count,
        Strength strength,
        bool background,
        double lit)
    {
        var du = strength.Duration;
        var at = t - wordStartMs - du * Stagger / Math.Max(1, count) * index;
        var swell = Emphasis(at / du);
        var floatAt = (at + FloatLeadMs) / (du * FloatStretch);
        var floatVal = floatAt > 0 && floatAt < 1
            ? Math.Sin(floatAt * Math.PI) * Float * (background ? 2.0 : 1.0)
            : 0.0;

        var x = -swell * Spread * strength.Amount * (Math.Max(1, count) / 2.0 - index);
        var y = -swell * Lift * strength.Amount - floatVal;
        var glow = swell * strength.Blur * (DimAlpha + (1.0 - DimAlpha) * lit);

        return new LetterPose(
            OffsetXEm: x,
            OffsetYEm: y,
            Scale: 1.0 + swell * Scale * strength.Amount,
            Glow: glow,
            RadiusEm: Math.Min(MaxRadius, strength.Blur * Radius));
    }

    /// <summary>
    /// A word floats up from the moment it starts, over its own length but never quicker than
    /// MinMs (1000ms), easing out; 0 to 1.
    /// </summary>
    public static double WordFloat(double t, double startMs, double endMs)
    {
        var x = Clamp((t - startMs) / Math.Max(MinMs, endMs - startMs));
        return 1.0 - (1.0 - x) * (1.0 - x);
    }

    public static double WordOffset(double floatProgress)
        => 0.01 - 0.043 * floatProgress;

    /// <summary>
    /// Evaluates where the continuous fill edge is for every word of a line, in px along the line laid end-to-end.
    /// </summary>
    public static double[] SweepEdges(
        double[] x,
        double[] w,
        double[] starts,
        double[] ends,
        double fade,
        double t)
    {
        var n = x.Length;
        var edges = new double[n];
        for (var i = 0; i < n; i++) edges[i] = double.NegativeInfinity;

        var first = -1;
        var last = -1;
        for (var i = 0; i < n; i++)
        {
            if (w[i] > 0)
            {
                if (first < 0) first = i;
                last = i;
            }
        }

        if (first < 0) return edges;

        var half = fade / 2.0;
        var reached = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            if (w[i] <= 0) continue;
            var from = i == first ? x[i] - half : x[i];
            var to = x[i] + w[i] + (i == last ? half : 0.0);
            var at = t >= ends[i]
                ? to
                : t > starts[i]
                    ? from + (to - from) * (t - starts[i]) / (ends[i] - starts[i])
                    : double.NegativeInfinity;

            edges[i] = at;
            if (at > reached) reached = at;
        }

        var before = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            if (w[i] <= 0) continue;
            var at = edges[i];
            if (t >= ends[i]) edges[i] = reached;
            else if (t <= starts[i]) edges[i] = before;
            if (at > before) before = at;
        }

        return edges;
    }

    /// <summary>
    /// Gradient sweep position (-50 fully dim, 100 fully lit) for a box at [x], [width] wide, for an [edge].
    /// </summary>
    public static double SweepPosition(double edge, double x, double width, double fade)
    {
        if (double.IsNegativeInfinity(edge)) return -50.0;
        var rest = (width + fade / 2.0 - (edge - x)) / (width + fade);
        return Math.Clamp(100.0 - 150.0 * rest, -50.0, 100.0);
    }

    /// <summary>
    /// Dot interlude animation: each dot fills smoothly; staggered entrance fades in from zero.
    /// </summary>
    public static double DotAlpha(double elapsedMs, double durationMs, int index)
    {
        var span = Math.Max(90.0, (durationMs - 550.0)) / 3.0;
        var fill = Ease((elapsedMs - index * span) / span);
        var enter = Smoothstep((elapsedMs - index * 120.0) / 650.0);
        return (0.22 + (0.92 - 0.22) * fill) * enter;
    }

    /// <summary>
    /// Dot breathing scale over long interludes.
    /// </summary>
    public static double DotBreath(double elapsedMs, double durationMs)
    {
        var room = durationMs - 500.0;
        if (room < 2600.0) return 1.0;
        var cycles = Math.Max(1.0, Math.Floor(room / 4200.0 + 0.5));
        var phase = Math.Clamp(elapsedMs / (room / cycles), 0.0, cycles);
        return 1.0 + 0.16 * (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * phase));
    }
}
