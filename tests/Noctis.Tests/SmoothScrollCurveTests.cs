using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Wheel smooth-scrolling felt choppy while the wheel kept turning, on every section and at
/// every wheel speed.
///
/// The integrator moved the position a fixed fraction of the distance left each frame, so speed
/// was proportional to distance and a notch — which adds a whole Step to that distance at once —
/// was a step change in velocity. The result was a sawtooth at wheel rate: at a 100ms cadence
/// the instantaneous speed swung 1253..3436 px/s around a 2200 px/s mean (~99% ripple), and at
/// slower cadences it decelerated almost to a standstill before each lurch (327% at 300ms).
///
/// These tests pin the properties that fixed it. The old integrator
/// (<c>current += (target - current) * (1 - exp(-dt / (settleMs / 4600)))</c>) fails
/// <see cref="VelocityRippleStaysLowWhileWheelKeepsTurning"/> at every cadence below.
/// </summary>
public class SmoothScrollCurveTests
{
    // The values the app ships (the ScrollViewer style in Assets/Styles.axaml).
    private const double Step = 220.0;
    private const double SettleMs = 380.0;

    /// <summary>
    /// Runs the integrator for <paramref name="seconds"/> at a fixed frame time, adding a notch
    /// every <paramref name="notchIntervalMs"/>, and returns the per-frame speed in px/s.
    /// </summary>
    private static List<double> SpeedsPerFrame(
        double notchIntervalMs, double fps, double seconds, int? notchesStopAfterFrame = null)
    {
        var dt = 1.0 / fps;
        var framesPerNotch = Math.Max(1, (int)Math.Round(notchIntervalMs / 1000.0 / dt));
        var frames = (int)(seconds / dt);

        double position = 0, velocity = 0, target = 0;
        var speeds = new List<double>(frames);

        for (var i = 0; i < frames; i++)
        {
            if (i % framesPerNotch == 0 && (notchesStopAfterFrame is null || i < notchesStopAfterFrame))
                target += Step;

            var previous = position;
            position = SmoothScrollBehavior.Advance(position, target, dt, SettleMs, ref velocity);
            speeds.Add((position - previous) / dt);
        }

        return speeds;
    }

    /// <summary>
    /// The integrator this replaced, kept so the bounds below are demonstrably tighter than the
    /// choppy behavior rather than just asserted to be. 4.6 time constants ≈ 99%, which is how
    /// SettleMs read there.
    /// </summary>
    private static double LegacyAdvance(double current, double target, double dt, double settleMs)
        => current + (target - current) * (1 - Math.Exp(-dt / (settleMs / 4600.0)));

    private static double SteadyStateRipple(IReadOnlyList<double> speeds)
    {
        // Second half only: the first notch starts from rest, so early frames are the intended
        // ramp-in rather than steady-state ripple.
        var steady = speeds.Skip(speeds.Count / 2).Select(Math.Abs).ToList();
        return (steady.Max() - steady.Min()) / steady.Average();
    }

    [Theory]
    // cadence, bound. Each bound sits between what the spring produces and what the old
    // distance-proportional chase produced — see RippleBoundsRejectTheChoppyIntegrator.
    [InlineData(60, 0.30)]
    [InlineData(80, 0.35)]
    [InlineData(100, 0.45)]
    [InlineData(125, 0.70)]
    [InlineData(150, 0.85)]
    public void VelocityRippleStaysLowWhileWheelKeepsTurning(double notchIntervalMs, double bound)
    {
        var ripple = SteadyStateRipple(SpeedsPerFrame(notchIntervalMs, fps: 60, seconds: 3));

        Assert.True(
            ripple < bound,
            $"speed ripple at a {notchIntervalMs}ms wheel cadence is {ripple:P0}, over the {bound:P0} bound");
    }

    [Theory]
    [InlineData(60, 0.30)]
    [InlineData(80, 0.35)]
    [InlineData(100, 0.45)]
    [InlineData(125, 0.70)]
    [InlineData(150, 0.85)]
    public void RippleBoundsRejectTheChoppyIntegrator(double notchIntervalMs, double bound)
    {
        var dt = 1.0 / 60.0;
        var framesPerNotch = Math.Max(1, (int)Math.Round(notchIntervalMs / 1000.0 / dt));
        var frames = (int)(3.0 / dt);

        double position = 0, target = 0;
        var speeds = new List<double>(frames);

        for (var i = 0; i < frames; i++)
        {
            if (i % framesPerNotch == 0)
                target += Step;

            var previous = position;
            position = LegacyAdvance(position, target, dt, SettleMs);
            speeds.Add((position - previous) / dt);
        }

        var ripple = SteadyStateRipple(speeds);

        Assert.True(
            ripple > bound,
            $"the {bound:P0} bound for a {notchIntervalMs}ms cadence does not actually exclude the " +
            $"old integrator (it ripples {ripple:P0}), so it is not a regression guard");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(144)]
    public void SameWallClockCurveAtAnyFrameRate(double fps)
    {
        var dt = 1.0 / fps;
        double position = 0, velocity = 0;

        // One notch from rest, integrated for 500ms.
        for (var elapsed = 0.0; elapsed < 0.5 - 1e-9; elapsed += dt)
            position = SmoothScrollBehavior.Advance(position, Step, dt, SettleMs, ref velocity);

        // Solved rather than Euler-stepped, so the frame rate must not change where it lands.
        Assert.Equal(219.66, position, precision: 2);
    }

    [Fact]
    public void CriticalDampingDoesNotOvershootWhenTheWheelStops()
    {
        // Notches for the first 30 frames, then the wheel stops and the built-up momentum has to
        // bleed off without sailing past the target.
        var dt = 1.0 / 60.0;
        double position = 0, velocity = 0, target = 0, peak = 0;

        for (var i = 0; i < 180; i++)
        {
            if (i % 6 == 0 && i < 30)
                target += Step;
            position = SmoothScrollBehavior.Advance(position, target, dt, SettleMs, ref velocity);
            peak = Math.Max(peak, position);
        }

        Assert.True(peak - target < 0.5, $"overshot the target by {peak - target:F2}px");
    }

    [Fact]
    public void SettleMsStillMeansHowLongANotchTakesToLand()
    {
        // The knob's documented meaning has to survive the integrator swap, or the value tuned in
        // Styles.axaml silently changes feel.
        var dt = 1.0 / 60.0;
        double position = 0, velocity = 0;
        var landedMs = -1.0;

        for (var i = 0; i < 300; i++)
        {
            position = SmoothScrollBehavior.Advance(position, Step, dt, SettleMs, ref velocity);
            if (Math.Abs(Step - position) < 0.5 && Math.Abs(velocity) < 20)
            {
                landedMs = (i + 1) * dt * 1000;
                break;
            }
        }

        Assert.InRange(landedMs, SettleMs * 0.8, SettleMs * 1.4);
    }
}
