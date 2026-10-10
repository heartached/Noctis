using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #88 band auto-scroll: dragging past the list's edge scrolls it. The step is a
/// rate (px/s per px of edge depth) scaled by real frame time, so it moves the same distance
/// per second on the compositor frame clock at 60 or 250 Hz.
/// </summary>
public class QueueBandAutoScrollTests
{
    private const double Edge = 24;
    private const double Height = 400;

    [Theory]
    [InlineData(100)]  // middle
    [InlineData(24)]   // exactly on the top edge line
    [InlineData(376)]  // exactly on the bottom edge line
    public void InsideTheEdgeZones_DoesNotScroll(double y)
    {
        Assert.Equal(0, QueueBandAutoScroll.Step(y, Height, Edge, 1 / 60.0));
    }

    [Fact]
    public void AboveTheTopEdge_ScrollsUp_DeeperIsFaster()
    {
        var shallow = QueueBandAutoScroll.Step(14, Height, Edge, 1 / 60.0);   // 10 px deep
        var deep = QueueBandAutoScroll.Step(-6, Height, Edge, 1 / 60.0);      // 30 px deep
        Assert.True(shallow < 0);
        Assert.True(deep < shallow);
    }

    [Fact]
    public void BelowTheBottomEdge_ScrollsDown()
    {
        Assert.True(QueueBandAutoScroll.Step(Height + 10, Height, Edge, 1 / 60.0) > 0);
    }

    [Fact]
    public void Depth_IsClampedAt60Px()
    {
        var at60 = QueueBandAutoScroll.Step(Height - Edge + 60, Height, Edge, 1 / 60.0);
        var at200 = QueueBandAutoScroll.Step(Height - Edge + 200, Height, Edge, 1 / 60.0);
        Assert.Equal(at60, at200, 6);
    }

    [Fact]
    public void Step_ScalesLinearlyWithFrameTime_AndMatchesTheOld16msTick()
    {
        // The old DispatcherTimer tick moved depth * 0.5 px per nominal 16 ms.
        var per16ms = QueueBandAutoScroll.Step(Height - Edge + 20, Height, Edge, 0.016);
        Assert.Equal(20 * 0.5, per16ms, 2);
        var per4ms = QueueBandAutoScroll.Step(Height - Edge + 20, Height, Edge, 0.004);
        Assert.Equal(per16ms / 4, per4ms, 6);
        Assert.Equal(0, QueueBandAutoScroll.Step(Height - Edge + 20, Height, Edge, 0));
    }
}
