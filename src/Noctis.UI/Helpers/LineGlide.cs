using System;

namespace Noctis.Helpers;

/// <summary>
/// A damped spring (ζ &lt; 1) in real time: closed-form position and velocity for any start
/// position, start velocity and target, so a glide can start from wherever — and however
/// fast — the list already is.
/// </summary>
public readonly record struct LineSpring(double Zeta, double NaturalFrequency)
{
    /// <summary>Position and velocity (units/s) <paramref name="tSec"/> after leaving
    /// <paramref name="x0"/> at velocity <paramref name="v0"/> toward <paramref name="target"/>.</summary>
    public (double X, double V) At(double x0, double v0, double target, double tSec)
    {
        if (tSec <= 0) return (x0, v0);
        var w = NaturalFrequency;
        var z = Math.Min(Zeta, 0.9999);
        var wd = w * Math.Sqrt(1 - z * z);
        var a = x0 - target;
        var b = (v0 + z * w * a) / wd;
        var env = Math.Exp(-z * w * tSec);
        var c = Math.Cos(wd * tSec);
        var s = Math.Sin(wd * tSec);
        var x = target + env * (a * c + b * s);
        var v = env * (v0 * c - (z * w * v0 + w * w * a) / wd * s);
        return (x, v);
    }

    /// <summary>Seconds until the motion's envelope is within <paramref name="tolerance"/>
    /// of the target (a conservative bound: the real residual is never larger).</summary>
    public double SettleSeconds(double x0, double v0, double target, double tolerance)
    {
        var w = NaturalFrequency;
        var z = Math.Min(Zeta, 0.9999);
        var wd = w * Math.Sqrt(1 - z * z);
        var a = x0 - target;
        var amp = Math.Abs(a) + Math.Abs((v0 + z * w * a) / wd);
        if (amp <= tolerance) return 0;
        return Math.Log(amp / tolerance) / (z * w);
    }

    /// <summary>Normalized step response (0 → 1 from rest) at <paramref name="tSec"/>.</summary>
    public double Step(double tSec) => At(0, 0, 1, tSec).X;

    /// <summary>Milliseconds until the step response first reaches <paramref name="fraction"/>.</summary>
    public double TimeToReachMs(double fraction)
    {
        for (var ms = 0; ms <= 5000; ms++)
            if (Step(ms / 1000.0) >= fraction) return ms;
        return double.NaN;
    }
}

/// <summary>
/// One scroll glide of the lyric list, with the cascade: the scroll offset and every line's
/// own displayed scroll position follow <see cref="LineSpring"/>s toward one target; lines
/// below the active one set off <c>delay</c> later each (Apple: 50 ms per line).
///
/// A glide that starts while another is in flight (fast rap: lines 0.6-1.3 s apart, inside
/// the old 1 s + cascade) takes over each line's displayed position AND velocity: a line
/// still moving keeps its old course until its own stagger elapses, then turns toward the
/// new target. The old glide restarted every line from rest and reset the cascade
/// translates to zero — a stall plus a jump of up to two clamp steps at every fast line.
///
/// Positions are in scroll-offset units: a line's render translate is
/// <see cref="Offset"/> − <see cref="LineScroll"/> (positive while it lags behind).
/// Allocates once per glide (per-line arrays), nothing per frame.
/// </summary>
public sealed class LineGlide
{
    /// <summary>Residual (px) under which a glide counts as landed and snaps.</summary>
    public const double SettleTolerancePx = 0.3;

    private readonly double _t0;
    private readonly double _target;
    private readonly LineSpring _spring;
    private readonly double _baseX0, _baseV0;
    // Per line: segment A (old course, from _t0) until _switch, then segment B toward _target.
    private readonly double[] _ax0, _av0, _aTarget, _switch, _bx0, _bv0;

    /// <summary>Time (ms, caller's clock) the glide started.</summary>
    public double StartMs => _t0;

    /// <summary>Scroll offset the glide lands on.</summary>
    public double Target => _target;

    /// <summary>The spring this glide runs on.</summary>
    public LineSpring Spring => _spring;

    /// <summary>Caller's-clock time (ms) by which every line is within
    /// <see cref="SettleTolerancePx"/> of the target.</summary>
    public double EndMs { get; private set; }

    /// <summary>Lines tracked (indices 0..LineCount-1 of the lines panel).</summary>
    public int LineCount => _ax0.Length;

    private LineGlide(double t0, double target, LineSpring spring, double baseX0, double baseV0, int lines)
    {
        _t0 = t0;
        _target = target;
        _spring = spring;
        _baseX0 = baseX0;
        _baseV0 = baseV0;
        _ax0 = new double[lines];
        _av0 = new double[lines];
        _aTarget = new double[lines];
        _switch = new double[lines];
        _bx0 = new double[lines];
        _bv0 = new double[lines];
        EndMs = t0 + spring.SettleSeconds(baseX0, baseV0, target, SettleTolerancePx) * 1000;
    }

    /// <summary>
    /// Starts a glide from scroll offset <paramref name="from"/> to <paramref name="to"/> at
    /// <paramref name="nowMs"/>. <paramref name="previous"/> (the glide being superseded, or
    /// null) hands over the offset's and each line's velocity; <paramref name="displayedLag"/>
    /// gives each line's current render translate (0 when it has none), so the new glide
    /// starts from exactly what is on screen. Lines after <paramref name="activeIndex"/>
    /// start <paramref name="delayPerLineMs"/> later each, the delay capped at
    /// <paramref name="maxDelayLines"/> lines; pass 0 for no cascade.
    /// </summary>
    public static LineGlide Begin(LineGlide? previous, double nowMs, double from, double to,
        LineSpring spring, int lineCount, int activeIndex, double delayPerLineMs, int maxDelayLines,
        Func<int, double> displayedLag)
    {
        var carry = previous != null && nowMs < previous.EndMs;
        var baseV = carry ? previous!.OffsetVelocity(nowMs) : 0;
        var g = new LineGlide(nowMs, to, spring, from, baseV, Math.Max(0, lineCount));
        var end = g.EndMs;
        for (var i = 0; i < g.LineCount; i++)
        {
            var lag = displayedLag(i);
            var x = from - lag;
            double v, aTarget;
            if (carry && i < previous!.LineCount)
            {
                v = previous.LineVelocity(i, nowMs);
                aTarget = previous._target;
            }
            else if (carry)
            {
                v = baseV;
                aTarget = previous!._target;
            }
            else
            {
                // At rest: holds where it is until its stagger elapses.
                v = 0;
                aTarget = x;
            }

            var delay = i > activeIndex && delayPerLineMs > 0
                ? Math.Min(i - activeIndex, maxDelayLines) * delayPerLineMs
                : 0;
            g._ax0[i] = x;
            g._av0[i] = v;
            g._aTarget[i] = aTarget;
            g._switch[i] = nowMs + delay;
            var (bx, bv) = spring.At(x, v, aTarget, delay / 1000.0);
            g._bx0[i] = bx;
            g._bv0[i] = bv;
            var lineEnd = nowMs + delay + spring.SettleSeconds(bx, bv, to, SettleTolerancePx) * 1000;
            if (lineEnd > end) end = lineEnd;
        }
        g.EndMs = end;
        return g;
    }

    /// <summary>True once every line has landed (the caller snaps and clears translates).</summary>
    public bool IsDone(double nowMs) => nowMs >= EndMs;

    /// <summary>The scroll offset at <paramref name="nowMs"/>.</summary>
    public double Offset(double nowMs) =>
        nowMs >= EndMs ? _target : _spring.At(_baseX0, _baseV0, _target, (nowMs - _t0) / 1000.0).X;

    /// <summary>The scroll offset's velocity (px/s) at <paramref name="nowMs"/>.</summary>
    public double OffsetVelocity(double nowMs) =>
        nowMs >= EndMs ? 0 : _spring.At(_baseX0, _baseV0, _target, (nowMs - _t0) / 1000.0).V;

    /// <summary>The scroll position line <paramref name="i"/> is displayed at.</summary>
    public double LineScroll(int i, double nowMs) => LineState(i, nowMs).X;

    /// <summary>Line <paramref name="i"/>'s render translate (raw, before the caller's
    /// neighbour clamp): <see cref="Offset"/> − <see cref="LineScroll"/>.</summary>
    public double LineLag(int i, double nowMs) =>
        i < 0 || i >= LineCount || nowMs >= EndMs ? 0 : Offset(nowMs) - LineScroll(i, nowMs);

    private double LineVelocity(int i, double nowMs) => LineState(i, nowMs).V;

    private (double X, double V) LineState(int i, double nowMs)
    {
        if (i < 0 || i >= LineCount) return (Offset(nowMs), OffsetVelocity(nowMs));
        if (nowMs >= EndMs) return (_target, 0);
        return nowMs < _switch[i]
            ? _spring.At(_ax0[i], _av0[i], _aTarget[i], (nowMs - _t0) / 1000.0)
            : _spring.At(_bx0[i], _bv0[i], _target, (nowMs - _switch[i]) / 1000.0);
    }
}
