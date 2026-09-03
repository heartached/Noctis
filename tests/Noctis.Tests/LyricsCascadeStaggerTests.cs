using Noctis.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// The scroll cascade lags each line below the active one by 35ms per line, and bounds
/// the lag difference between neighbours so lines can never cross. With the shared
/// line-motion curve the raw stagger on an ordinary two-row line exceeds the old 16px
/// hard clamp at peak speed (~116ms in): the line below rode the active line for a few
/// frames and then snapped back onto its own curve — a hitch at the start of every
/// flow. These pin the limiter: hard only where crossing is possible, smooth elsewhere.
/// </summary>
public class LyricsCascadeStaggerTests
{
    private readonly ITestOutputHelper _output;
    public LyricsCascadeStaggerTests(ITestOutputHelper output) => _output = output;

    private const double Hard = 16;
    private const double DelayMs = 35;
    private const double FrameMs = 1000.0 / 60;

    [Fact]
    public void ClosingDirection_IsHardBounded_SoLinesNeverCross()
    {
        // Lower line displaced toward the one above: only the hard bound will do.
        Assert.Equal(-Hard, LineMotion.CascadeStep(-40, 0, Hard), 6);
        Assert.Equal(-10, LineMotion.CascadeStep(-10, 0, Hard), 6);
        Assert.Equal(100 - Hard, LineMotion.CascadeStep(50, 100, Hard), 6);
    }

    [Fact]
    public void OpeningDirection_IsIdentityUpToTheBound_ThenSaturatesSmoothly()
    {
        Assert.Equal(10, LineMotion.CascadeStep(10, 0, Hard), 6);
        Assert.Equal(Hard, LineMotion.CascadeStep(Hard, 0, Hard), 6);
        // Continuous through the knee.
        Assert.InRange(LineMotion.CascadeStep(Hard + 0.5, 0, Hard), Hard + 0.4, Hard + 0.5);
        // Bounded: never spreads more than twice the hard step.
        Assert.True(LineMotion.CascadeStep(1000, 0, Hard) <= 2 * Hard);
        // Monotone, so the chained walk keeps the top-to-bottom order.
        var prev = 0.0;
        for (var x = 0.0; x < 200; x += 0.5)
        {
            var v = LineMotion.CascadeStep(x, 0, Hard);
            Assert.True(v >= prev, $"not monotone at {x}");
            prev = v;
        }
    }

    /// <summary>Per-frame velocity of the first cascade line over a forward advance of
    /// <paramref name="deltaPx"/>, using the given neighbour limiter.</summary>
    private static (double MaxJerk, double PeakRawStagger) SimulateFirstCascadeLine(
        double deltaPx, Func<double, double, double> limit)
    {
        var total = (double)LineMotion.DurationMs;
        double prevPos = double.NaN, prevVel = double.NaN, maxJerk = 0, peakRaw = 0;
        for (var elapsed = 0.0; elapsed <= total + DelayMs; elapsed += FrameMs)
        {
            var eased = LineMotion.Ease(Math.Min(1, elapsed / total));
            var tLine = Math.Clamp((elapsed - DelayMs) / total, 0, 1);
            var raw = deltaPx * (eased - LineMotion.Ease(tLine));
            peakRaw = Math.Max(peakRaw, raw);
            var lag = limit(raw, 0);          // prevLag = 0: the active line itself has no lag
            var pos = deltaPx * eased - lag;  // where the line is drawn, in scroll space

            if (!double.IsNaN(prevPos))
            {
                var vel = pos - prevPos;
                if (!double.IsNaN(prevVel))
                    maxJerk = Math.Max(maxJerk, Math.Abs(vel - prevVel));
                prevVel = vel;
            }
            prevPos = pos;
        }
        return (maxJerk, peakRaw);
    }

    [Fact]
    public void ThreeRowLineAdvance_HardClampKinked_SoftLimiterDoesNot()
    {
        // 46px font, three rows, 20px margins. (A two-row line, 132px, only grazes the
        // clamp by ~1px — measured, not a visible kink; three rows overshoot it by 9px.)
        const double threeRowLinePx = 190;

        var (hardJerk, peakRaw) = SimulateFirstCascadeLine(threeRowLinePx,
            (raw, prev) => Math.Clamp(raw, prev - Hard, prev + Hard));
        var (softJerk, _) = SimulateFirstCascadeLine(threeRowLinePx,
            (raw, prev) => LineMotion.CascadeStep(raw, prev, Hard));
        // The base glide's own largest per-frame velocity change is the smoothness floor.
        var baseJerk = 0.0;
        double pv = double.NaN, pp = 0;
        for (var e = 0.0; e <= LineMotion.DurationMs; e += FrameMs)
        {
            var p = threeRowLinePx * LineMotion.Ease(e / LineMotion.DurationMs);
            var v = p - pp; pp = p;
            if (!double.IsNaN(pv)) baseJerk = Math.Max(baseJerk, Math.Abs(v - pv));
            pv = v;
        }

        _output.WriteLine($"peak raw stagger {peakRaw:F1}px (clamp {Hard}); jerk: base {baseJerk:F2}, hard {hardJerk:F2}, soft {softJerk:F2} px/frame²");

        Assert.True(peakRaw > Hard, "harness: this advance should drive the stagger past the hard clamp");
        Assert.True(hardJerk > baseJerk * 1.5, "the hard clamp should show as a kink for this test to prove anything");
        Assert.True(softJerk <= baseJerk * 1.15, $"soft limiter still kinks: {softJerk:F2} vs base {baseJerk:F2}");
    }
}
