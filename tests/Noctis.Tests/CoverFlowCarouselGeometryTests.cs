using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Pure geometry + step logic behind the five-card Cover Flow carousel: the slot poses the
/// view interpolates during a skip, and how the view-model decides how far the row moved.
/// </summary>
public class CoverFlowCarouselGeometryTests
{
    [Fact]
    public void CentreSlot_IsTheIdentityPose()
    {
        var p = CoverFlowCarouselGeometry.At(0);
        Assert.Equal(0, p.X);
        Assert.Equal(1, p.Scale);
        Assert.Equal(0, p.AngleY);
        Assert.Equal(1, p.Opacity);
    }

    [Fact]
    public void SideSlots_MirrorAndTiltTowardTheCentre()
    {
        var l1 = CoverFlowCarouselGeometry.At(-1);
        var r1 = CoverFlowCarouselGeometry.At(1);
        var r2 = CoverFlowCarouselGeometry.At(2);

        Assert.Equal(-r1.X, l1.X);
        Assert.Equal(-r1.AngleY, l1.AngleY);
        Assert.Equal(r1.Scale, l1.Scale);
        // Left cards tilt with a POSITIVE AngleY (outer/left edge nearer), right cards negative.
        Assert.True(l1.AngleY > 0);
        Assert.True(r1.AngleY < 0);
        // Every side card identical: same size, tilt and full opacity (the mockup's
        // consistent row — nothing recedes or fades).
        Assert.Equal(CoverFlowCarouselGeometry.SideScale, r1.Scale, 6);
        Assert.Equal(CoverFlowCarouselGeometry.SideAngle, -r1.AngleY, 6);
        Assert.Equal(r1.Scale, r2.Scale, 6);
        Assert.Equal(r1.AngleY, r2.AngleY, 6);
        Assert.Equal(1, r1.Opacity);
        Assert.Equal(1, r2.Opacity);
        Assert.True(r2.X > r1.X);
    }

    [Fact]
    public void ExitSlot_IsTransparent_AndPositionsClamp()
    {
        Assert.Equal(7, CoverFlowCarouselGeometry.SideSlots);
        Assert.Equal(1, CoverFlowCarouselGeometry.At(7).Opacity);
        Assert.Equal(0, CoverFlowCarouselGeometry.At(8).Opacity);
        Assert.Equal(CoverFlowCarouselGeometry.At(8), CoverFlowCarouselGeometry.At(11));
        Assert.Equal(CoverFlowCarouselGeometry.At(-8), CoverFlowCarouselGeometry.At(-12));
    }

    [Fact]
    public void SlideDuration_GrowsWithTheDistanceJumped()
    {
        var one = CoverFlowCarouselGeometry.SlideDurationFor(1);
        Assert.Equal(CoverFlowCarouselGeometry.SlideDuration, one);
        Assert.Equal(one, CoverFlowCarouselGeometry.SlideDurationFor(-1));
        Assert.Equal(one, CoverFlowCarouselGeometry.SlideDurationFor(0));
        Assert.True(CoverFlowCarouselGeometry.SlideDurationFor(4) > one);
        Assert.True(CoverFlowCarouselGeometry.SlideDurationFor(2) > one);
        Assert.Equal(CoverFlowCarouselGeometry.SlideDurationFor(2), CoverFlowCarouselGeometry.SlideDurationFor(-2));
        Assert.True(CoverFlowCarouselGeometry.SlideDurationFor(2) <= TimeSpan.FromMilliseconds(1000), "a two-slot jump still lands well under a second");
    }

    [Fact]
    public void Row_SitsSideBySide_NeverOverlapping()
    {
        // 09-22 mockup: every card beside its neighbour with the same clear gap between
        // projected faces, every side card the same pose, so the steps are all equal.
        Assert.Equal(CoverFlowCarouselGeometry.CardWidth / 2, CoverFlowCarouselGeometry.OuterHalfWidth(0), 6);
        var step = CoverFlowCarouselGeometry.At(2).X - CoverFlowCarouselGeometry.At(1).X;
        for (var s = 1; s <= CoverFlowCarouselGeometry.SideSlots; s++)
        {
            var inner = CoverFlowCarouselGeometry.At(s - 1);
            var outer = CoverFlowCarouselGeometry.At(s);
            var innerEdge = inner.X + CoverFlowCarouselGeometry.OuterHalfWidth(s - 1);
            var outerEdge = outer.X - CoverFlowCarouselGeometry.InnerHalfWidth(s);
            Assert.Equal(CoverFlowCarouselGeometry.Gap, outerEdge - innerEdge, 6);
            // The outer edge is the near one: it projects wider than the inner edge.
            Assert.True(CoverFlowCarouselGeometry.OuterHalfWidth(s) > CoverFlowCarouselGeometry.InnerHalfWidth(s));
            Assert.Equal(CoverFlowCarouselGeometry.SideScale, outer.Scale, 6);
            Assert.Equal(CoverFlowCarouselGeometry.SideAngle, Math.Abs(outer.AngleY), 6);
            Assert.Equal(1, outer.Opacity);
            if (s >= 2) Assert.Equal(step, outer.X - inner.X, 6);
            Assert.True(CoverFlowCarouselGeometry.ZIndexAt(s) < CoverFlowCarouselGeometry.ZIndexAt(s - 1));
        }
    }

    [Fact]
    public void FractionalPositions_InterpolateBetweenSlots()
    {
        var a = CoverFlowCarouselGeometry.At(1);
        var b = CoverFlowCarouselGeometry.At(2);
        var mid = CoverFlowCarouselGeometry.At(1.5);
        Assert.Equal((a.X + b.X) / 2, mid.X, 6);
        Assert.Equal((a.Scale + b.Scale) / 2, mid.Scale, 6);
        Assert.Equal((a.AngleY + b.AngleY) / 2, mid.AngleY, 6);
        // Centre → ±1 interpolates scale and tilt too.
        var half = CoverFlowCarouselGeometry.At(0.5);
        Assert.Equal((1 + a.Scale) / 2, half.Scale, 6);
    }

    [Fact]
    public void ZOrder_FallsWithDistance_EvenMidSlide()
    {
        Assert.True(CoverFlowCarouselGeometry.ZIndexAt(0) > CoverFlowCarouselGeometry.ZIndexAt(1));
        Assert.True(CoverFlowCarouselGeometry.ZIndexAt(-1) > CoverFlowCarouselGeometry.ZIndexAt(2));
        Assert.True(CoverFlowCarouselGeometry.ZIndexAt(0.5) > CoverFlowCarouselGeometry.ZIndexAt(1.5));
    }

    [Fact]
    public void Ease_IsEaseOut()
    {
        Assert.Equal(0, CoverFlowCarouselGeometry.Ease(0));
        Assert.Equal(1, CoverFlowCarouselGeometry.Ease(1));
        Assert.True(CoverFlowCarouselGeometry.Ease(0.5) > 0.5, "ease-out covers most of the distance early");
        Assert.True(CoverFlowCarouselGeometry.Ease(0.25) < CoverFlowCarouselGeometry.Ease(0.5));
    }

    [Fact]
    public void StepBetween_FindsTheNewCentreAmongTheOldNeighbours()
    {
        var c = new Track { Title = "c" };
        var p1 = new Track { Title = "p1" };
        var p2 = new Track { Title = "p2" };
        var n1 = new Track { Title = "n1" };
        var n2 = new Track { Title = "n2" };
        var other = new Track { Title = "x" };

        Assert.Equal(1, CoverFlowViewModel.StepBetween(c, n1, p1, p2, n1, n2));
        Assert.Equal(2, CoverFlowViewModel.StepBetween(c, n2, p1, p2, n1, n2));
        Assert.Equal(-1, CoverFlowViewModel.StepBetween(c, p1, p1, p2, n1, n2));
        Assert.Equal(-2, CoverFlowViewModel.StepBetween(c, p2, p1, p2, n1, n2));
        Assert.Equal(0, CoverFlowViewModel.StepBetween(c, other, p1, p2, n1, n2));
        Assert.Equal(0, CoverFlowViewModel.StepBetween(c, c, p1, p2, n1, n2));
        Assert.Equal(0, CoverFlowViewModel.StepBetween(c, null, p1, p2, n1, n2));
        var p3 = new Track { Title = "p3" }; var n4 = new Track { Title = "n4" };
        Assert.Equal(-3, CoverFlowViewModel.StepBetween(c, p3, p1, p2, n1, n2, p3, null, null, n4));
        Assert.Equal(4, CoverFlowViewModel.StepBetween(c, n4, p1, p2, n1, n2, p3, null, null, n4));
        // The 15-card row: a jump to the ±7 card still slides.
        var p7 = new Track { Title = "p7" }; var n7 = new Track { Title = "n7" };
        Assert.Equal(7, CoverFlowViewModel.StepBetween(c, n7, new Track?[] { p1, null, null, null, null, null, p7 }, new Track?[] { n1, null, null, null, null, null, n7 }));
        Assert.Equal(-7, CoverFlowViewModel.StepBetween(c, p7, new Track?[] { p1, null, null, null, null, null, p7 }, new Track?[] { n1, null, null, null, null, null, n7 }));
    }
}
