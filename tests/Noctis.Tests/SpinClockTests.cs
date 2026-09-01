using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The disc/reel rotation behind the CD, vinyl and cassette artwork styles is a
/// turntable, not a style animation: it spins up when playback starts, coasts to a
/// stop on pause and HOLDS its angle there, so pausing never snaps the disc back to 0°.
/// </summary>
public class SpinClockTests
{
    private static SpinClock Run(SpinClock clock, double seconds, double step = 1.0 / 60)
    {
        for (double t = 0; t < seconds; t += step) clock.Advance(step);
        return clock;
    }

    [Fact]
    public void FreshClock_IsSettledAtZero()
    {
        var clock = new SpinClock();

        Assert.True(clock.IsSettled);
        Assert.Equal(0, clock.Angle);
        Assert.Equal(0, clock.Velocity);
    }

    [Fact]
    public void TotalDegrees_KeepsCountingWhileAngleWraps()
    {
        var clock = Run(new SpinClock { IsRunning = true }, 12);

        Assert.True(clock.TotalDegrees > 360, "a 12 s run must exceed one turn");
        Assert.InRange(clock.Angle, 0, 360);
        Assert.Equal(clock.TotalDegrees % 360, clock.Angle, 9);
    }

    [Fact]
    public void Running_IsNeverSettled_AndAdvancesTheAngle()
    {
        var clock = new SpinClock { IsRunning = true };
        Assert.False(clock.IsSettled);

        double previous = 0;
        for (int i = 0; i < 30; i++)
        {
            clock.Advance(1.0 / 60);
            Assert.True(clock.Angle > previous, $"angle did not advance on frame {i}");
            previous = clock.Angle;
        }
    }

    [Fact]
    public void Running_ApproachesTheTargetSpeed()
    {
        var clock = Run(new SpinClock { IsRunning = true, TargetDegreesPerSecond = 90 }, 10);

        Assert.InRange(clock.Velocity, 89, 90);
    }

    [Fact]
    public void Stopping_CoastsBeforeItSettles()
    {
        var clock = Run(new SpinClock { IsRunning = true }, 5);
        clock.IsRunning = false;

        var angleAtPause = clock.Angle;
        clock.Advance(1.0 / 60);

        Assert.False(clock.IsSettled);
        Assert.True(clock.Velocity > 0, "the disc should still be coasting one frame after pause");
        Assert.NotEqual(angleAtPause, clock.Angle);
    }

    [Fact]
    public void Stopped_SettlesAndHoldsItsAngle()
    {
        var clock = Run(new SpinClock { IsRunning = true }, 5);
        clock.IsRunning = false;
        Run(clock, 20);

        Assert.True(clock.IsSettled);
        var resting = clock.Angle;
        Assert.NotEqual(0, resting);

        Run(clock, 2);
        Assert.Equal(resting, clock.Angle);
    }

    [Fact]
    public void Angle_StaysWithinOneTurn()
    {
        var clock = Run(new SpinClock { IsRunning = true, TargetDegreesPerSecond = 720 }, 30);

        Assert.InRange(clock.Angle, 0, 360);
    }

    [Fact]
    public void Advance_IgnoresNonPositiveTime()
    {
        var clock = Run(new SpinClock { IsRunning = true }, 1);
        var angle = clock.Angle;

        clock.Advance(0);
        clock.Advance(-1);

        Assert.Equal(angle, clock.Angle);
    }
}
