using System;

namespace Noctis.Helpers;

/// <summary>
/// Pure maths for the album carousel (GitHub #149: browse the library's albums, or one
/// artist's, "like an iPod classic"). Unlike the now-playing Cover Flow row
/// (<see cref="CoverFlowCarouselGeometry"/>: cards side by side, never overlapping) this is the
/// classic stack: one album flat in the middle, the others turned steeply to face it and
/// packed edge-behind-edge, so a long library reads as two receding walls of covers.
///
/// Everything scales with the card's own layout width <c>w</c>, so the look holds at any
/// window size and for every case style (a jewel case or a record peeking out of its sleeve
/// is wider than a bare cover). Positions are signed slot offsets from the centre: 0 = the
/// centred album, -1 = the one to its left, +2.5 = halfway between the 2nd and 3rd on the right.
/// Reuses <see cref="CarouselPose"/> so the view applies it exactly like the Cover Flow row.
/// </summary>
public static class AlbumCarouselGeometry
{
    /// <summary>Tilt of every side card, degrees (classic Cover Flow is steep).</summary>
    public const double SideAngle = 62;

    /// <summary>Scale of every side card: set back a little behind the centred one.</summary>
    public const double SideScale = 0.86;

    /// <summary>Perspective depth as a multiple of the card width (Rotate3DTransform.Depth).</summary>
    public const double DepthFactor = 2.4;

    /// <summary>Clear space between the centred card and the first side card, × card width.</summary>
    public const double CenterGapFactor = 0.06;

    /// <summary>Distance between two stacked side cards, × card width: the strip of each
    /// side cover that shows past its inner neighbour.</summary>
    public const double SpacingFactor = 0.2;

    /// <summary>Most side cards built on each side, whatever the width (virtualization cap).</summary>
    public const int MaxSideCards = 16;

    /// <summary>Follow speed of the glide towards the target, per second: 14 covers ~90% of
    /// a step in 165 ms and settles in ~330 ms — quick, but it still reads as motion, and a
    /// fast wheel spin just keeps moving the target so the row coasts through the albums.</summary>
    public const double Stiffness = 14;

    /// <summary>A release keeps going for this long at the drag's speed before it snaps.</summary>
    public const double FlickSeconds = 0.18;

    public static double Depth(double w) => DepthFactor * w;

    public static double Spacing(double w) => SpacingFactor * w;

    /// <summary>Projected half-width of a side card on its near (outer) or far (inner) side.</summary>
    public static double SideHalfWidth(double w, bool near)
    {
        var a = SideAngle * Math.PI / 180;
        var h = w / 2;
        var dx = h * Math.Cos(a);
        var dz = h * Math.Sin(a);
        var d = Depth(w);
        return SideScale * dx * d / (near ? d - dz : d + dz);
    }

    /// <summary>X of the first side card's centre: the centred card's half, the gap, then the
    /// side card's far (inner) half.</summary>
    public static double FirstSideX(double w) => w / 2 + CenterGapFactor * w + SideHalfWidth(w, near: false);

    /// <summary>Side cards worth building on each side for a viewport <paramref name="viewportWidth"/>
    /// wide: every card whose inner edge still lands inside it, plus one more that fades in.</summary>
    public static int VisibleSides(double viewportWidth, double w)
    {
        if (w <= 0 || viewportWidth <= 0) return 1;
        var reach = viewportWidth / 2 + SideHalfWidth(w, near: false) - FirstSideX(w);
        var k = reach <= 0 ? 1 : (int)Math.Floor(reach / Spacing(w)) + 1;
        return Math.Clamp(k + 1, 1, MaxSideCards);
    }

    /// <summary>Pose of a card <paramref name="offset"/> slots from the centre (fractional
    /// during a glide). <paramref name="sides"/> = how many side cards are fully shown; the
    /// next slot out fades so cards enter and leave the row without popping.</summary>
    public static CarouselPose At(double offset, double w, int sides)
    {
        var sign = offset < 0 ? -1 : 1;
        var d = Math.Abs(offset);
        double x, scale, angle;
        if (d <= 1)
        {
            x = FirstSideX(w) * d;
            scale = 1 + (SideScale - 1) * d;
            angle = SideAngle * d;
        }
        else
        {
            x = FirstSideX(w) + (d - 1) * Spacing(w);
            scale = SideScale;
            angle = SideAngle;
        }
        var opacity = d <= sides ? 1 : Math.Clamp(sides + 1 - d, 0, 1);
        // Left cards turn POSITIVE (outer, left edge nearer), mirrored on the right — the
        // same convention as the Cover Flow row, so both walls face the centred album.
        return new CarouselPose(sign * x, scale, -sign * angle, opacity);
    }

    /// <summary>Draw order: the centred card on top, then outwards.</summary>
    public static int ZIndexAt(double offset) => 1000 - (int)Math.Round(Math.Abs(offset) * 10);

    /// <summary>One frame of the glide: exponential approach of <paramref name="position"/>
    /// to <paramref name="target"/> over <paramref name="dt"/> seconds; lands exactly once close.</summary>
    public static double Approach(double position, double target, double dt)
    {
        var next = target + (position - target) * Math.Exp(-Stiffness * Math.Max(0, dt));
        return Math.Abs(next - target) < 0.002 ? target : next;
    }

    /// <summary>Album to settle on after a drag ends at <paramref name="position"/> moving at
    /// <paramref name="velocity"/> slots/second, clamped to the list.</summary>
    public static int SnapTarget(double position, double velocity, int count)
    {
        if (count <= 0) return 0;
        var landed = (int)Math.Round(position + velocity * FlickSeconds, MidpointRounding.AwayFromZero);
        return Math.Clamp(landed, 0, count - 1);
    }

    /// <summary>Pixels of drag that move the row by one album: one side strip, so the cover
    /// under the pointer roughly follows it.</summary>
    public static double DragPixelsPerAlbum(double w) => Math.Max(24, Spacing(w) * 1.6);
}
