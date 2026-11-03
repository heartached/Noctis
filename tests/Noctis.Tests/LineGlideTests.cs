using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyric list's glide (LineGlide): closed-form springs for the offset and every line,
/// a stagger below the active line, and — the fast-rap fix — a glide that interrupts another
/// takes over each line's position and velocity instead of restarting from rest and snapping
/// the cascade translates to zero.
/// </summary>
public class LineGlideTests
{
    private static readonly LineSpring Base = LineMotion.BaseSpring;

    [Fact]
    public void Spring_FromRest_StartsInPlace_AndLandsOnTarget()
    {
        var (x0, v0) = Base.At(10, 0, 110, 0);
        Assert.Equal(10, x0, 9);
        Assert.Equal(0, v0, 9);
        var (x1, v1) = Base.At(10, 0, 110, 2.0);
        Assert.Equal(110, x1, 1);
        Assert.Equal(0, v1, 1);
    }

    [Fact]
    public void Spring_Velocity_IsTheDerivativeOfPosition()
    {
        const double dt = 1e-5;
        foreach (var t in new[] { 0.01, 0.1, 0.25, 0.5 })
        {
            var (xa, _) = Base.At(0, 300, 100, t - dt);
            var (xb, _) = Base.At(0, 300, 100, t + dt);
            var (_, v) = Base.At(0, 300, 100, t);
            Assert.Equal((xb - xa) / (2 * dt), v, 3);
        }
    }

    [Fact]
    public void SettleSeconds_BoundsTheResidual()
    {
        var settle = Base.SettleSeconds(0, 500, 131, 0.3);
        for (var t = settle; t < settle + 1; t += 0.01)
            Assert.True(Math.Abs(Base.At(0, 500, 131, t).X - 131) <= 0.3 + 1e-9);
    }

    private static LineGlide Glide(LineGlide? previous, double now, double from, double to,
        int lines = 6, int active = 1, Func<int, double>? lag = null) =>
        LineGlide.Begin(previous, now, from, to, Base, lines, active, LineMotion.CascadeDelayPerLineMs, 8,
            lag ?? (_ => 0));

    [Fact]
    public void Cascade_LinesBelowTheActiveOne_HoldUntilTheirStagger_ThenCatchUp()
    {
        var g = Glide(null, 1000, 0, 131);
        // Line 3 is two below the active line: 100 ms of stagger, still at rest at 90 ms.
        Assert.Equal(0, g.LineScroll(3, 1090), 6);
        Assert.True(g.LineScroll(3, 1200) > 0);
        Assert.True(g.LineLag(3, 1150) > 0, "a lower line lags behind the offset while the list moves");
        // The active line and those above ride with the offset.
        Assert.Equal(0, g.LineLag(1, 1150), 9);
        Assert.Equal(0, g.LineLag(0, 1150), 9);
        Assert.True(g.IsDone(g.EndMs));
        Assert.Equal(131, g.Offset(g.EndMs), 9);
        Assert.Equal(0, g.LineLag(5, g.EndMs), 9);
    }

    /// <summary>
    /// Rap: lines 0.6 s apart. The old glide (1 s + cascade) was always interrupted; it
    /// restarted from zero velocity (a stall at every line) and dropped the cascade lags to 0
    /// (a jump). Now every line's displayed position and velocity carry straight over.
    /// </summary>
    [Fact]
    public void Interrupting_AGlide_KeepsEveryLinesPositionAndVelocity()
    {
        var first = Glide(null, 0, 0, 120);
        const double now = 180;   // mid-glide: fast, with cascade lags open
        double DisplayedLag(int i) => first.LineLag(i, now);
        var second = Glide(first, now, first.Offset(now), 240, active: 2, lag: DisplayedLag);

        const double dt = 0.5;
        Assert.Equal(first.Offset(now), second.Offset(now), 6);
        var vFirst = (first.Offset(now) - first.Offset(now - dt)) / dt;
        var vSecond = (second.Offset(now + dt) - second.Offset(now)) / dt;
        Assert.True(vFirst > 0.2, $"the list was moving ({vFirst:0.000} px/ms)");
        Assert.InRange(vSecond, vFirst * 0.9, vFirst * 1.1);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(first.LineScroll(i, now), second.LineScroll(i, now), 6);
            var a = (first.LineScroll(i, now) - first.LineScroll(i, now - dt)) / dt;
            var b = (second.LineScroll(i, now + dt) - second.LineScroll(i, now)) / dt;
            Assert.True(Math.Abs(a - b) < 0.02 + Math.Abs(a) * 0.1, $"line {i}: {a:0.000} → {b:0.000} px/ms");
        }
    }

    [Fact]
    public void AFastRapRun_NeverStallsBetweenLines()
    {
        // Six changes 250 ms apart (a rapid burst: each one lands mid-glide), 60 px each:
        // sample the offset every frame.
        LineGlide? g = null;
        var offset = 0.0;
        var speeds = new List<double>();
        double last = 0;
        for (var line = 0; line < 6; line++)
        {
            var now = line * 250.0;
            g = Glide(g, now, g?.Offset(now) ?? offset, (line + 1) * 60.0,
                lag: i => 0);
            for (var t = now; t < now + 250; t += 16.67)
            {
                var y = g.Offset(t);
                if (t > 0) speeds.Add(y - last);
                last = y;
            }
        }
        // Between the first and the last change the list never comes to a stop and restarts:
        // no frame in the run moves less than a tenth of the run's mean step.
        var mean = speeds.Average();
        var inner = speeds.Skip(3).Take(speeds.Count - 6).ToList();
        Assert.True(inner.Min() > mean * 0.1, $"stalled: min step {inner.Min():0.00} px, mean {mean:0.00} px");
    }

    [Fact]
    public void ACompletedGlide_HandsOverNothing()
    {
        var first = Glide(null, 0, 0, 100);
        var second = Glide(first, first.EndMs + 10, 100, 200);
        // From rest: the first frame barely moves.
        Assert.True(second.Offset(first.EndMs + 11) - 100 < 0.05);
    }
}
