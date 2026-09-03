using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The shared flowing-artwork math: rotated copies always cover the viewport, the
/// beat pulse only ever adds a small breathe, and the on-screen smoothing has a
/// fast attack and a slow release so beats read as hits, not flicker.
/// </summary>
public class FlowingArtworkMotionTests
{
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1920, 1080)]
    [InlineData(340, 900)]   // lyrics panel
    [InlineData(420, 200)]   // mini lyrics column
    public void Layers_CoverTheViewportAtEveryAngle(double w, double h)
    {
        // A copy rotated about the centre needs at least diagonal/short-side to hide
        // the viewport corners; the drift adds a little more on top.
        var minimum = FlowingArtworkMotion.CoverScale(w, h);
        for (var t = 0.0; t < 600; t += 7.3)
        {
            var frame = FlowingArtworkMotion.Evaluate(t, w, h, 0);
            Assert.True(frame.Layer1.Scale >= minimum, $"layer1 {frame.Layer1.Scale} < {minimum} at t={t}");
            Assert.True(frame.Layer2.Scale >= minimum, $"layer2 {frame.Layer2.Scale} < {minimum} at t={t}");
            // Drift stays a small fraction of the viewport.
            Assert.InRange(Math.Abs(frame.Layer1.X), 0, w * 0.07);
            Assert.InRange(Math.Abs(frame.Layer2.Y), 0, h * 0.07);
            Assert.InRange(frame.Layer1.Opacity, 0.3, 0.7);
            Assert.InRange(frame.Layer2.Opacity, 0.2, 0.5);
        }
    }

    [Fact]
    public void Pulse_ScalesBackdropAndGlowLinearly()
    {
        var rest = FlowingArtworkMotion.Evaluate(1, 800, 600, 0);
        var hit = FlowingArtworkMotion.Evaluate(1, 800, 600, 1);
        var half = FlowingArtworkMotion.Evaluate(1, 800, 600, 0.5);
        Assert.Equal(1.0, rest.BackdropScale, 9);
        Assert.Equal(0.0, rest.GlowOpacity, 9);
        Assert.Equal(1.0 + FlowingArtworkMotion.PulseScale, hit.BackdropScale, 9);
        Assert.Equal(FlowingArtworkMotion.PulseGlow, hit.GlowOpacity, 9);
        Assert.Equal(1.0 + FlowingArtworkMotion.PulseScale / 2, half.BackdropScale, 9);
        // The drift is independent of the pulse: same layer poses at the same t.
        Assert.Equal(rest.Layer1, hit.Layer1);
    }

    [Fact]
    public void Pulse_IsClampedAndNaNSafe()
    {
        Assert.Equal(1.0 + FlowingArtworkMotion.PulseScale, FlowingArtworkMotion.Evaluate(0, 10, 10, 5).BackdropScale, 9);
        Assert.Equal(1.0, FlowingArtworkMotion.Evaluate(0, 10, 10, -3).BackdropScale, 9);
        Assert.Equal(1.0, FlowingArtworkMotion.Evaluate(0, 10, 10, double.NaN).BackdropScale, 9);
        Assert.Equal(1.0, FlowingArtworkMotion.CoverScale(0, 10));
        Assert.Equal(1.0, FlowingArtworkMotion.CoverScale(double.NaN, 10));
    }

    [Fact]
    public void Smooth_AttacksFastAndReleasesSlow()
    {
        // One 16 ms frame toward a hit covers most of the distance…
        var up = FlowingArtworkMotion.Smooth(0, 1, 16);
        Assert.InRange(up, 0.4, 0.6);
        // …while the same frame back toward rest moves only a little.
        var down = FlowingArtworkMotion.Smooth(1, 0, 16);
        Assert.InRange(down, 0.85, 0.95);
        // Degenerate dt leaves the value alone.
        Assert.Equal(0.3, FlowingArtworkMotion.Smooth(0.3, 1, 0));
        Assert.Equal(0.3, FlowingArtworkMotion.Smooth(0.3, 1, double.NaN));
    }

    [Fact]
    public void Motion_NeverLoopsVisiblyWithinTenMinutes()
    {
        // The drift frequencies share no common period: no two frames a whole number
        // of seconds apart within 10 minutes repeat the first pose.
        var first = FlowingArtworkMotion.Evaluate(0, 1000, 700, 0).Layer1;
        for (var t = 5.0; t < 600; t += 1.0)
        {
            var pose = FlowingArtworkMotion.Evaluate(t, 1000, 700, 0).Layer1;
            var same = Math.Abs(pose.X - first.X) < 0.5 && Math.Abs(pose.Y - first.Y) < 0.5
                && Math.Abs((pose.AngleDeg % 360) - (first.AngleDeg % 360)) < 0.5;
            Assert.False(same, $"pose repeated at t={t}");
        }
    }
}
