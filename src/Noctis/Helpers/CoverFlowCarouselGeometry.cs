using System;

namespace Noctis.Helpers;

/// <summary>One card's place in the Cover Flow carousel at a (fractional) position.</summary>
/// <param name="X">Horizontal offset from the centre slot, logical px (negative = left).</param>
/// <param name="Scale">Uniform scale (1 at the centre).</param>
/// <param name="AngleY">3D tilt about the vertical axis, degrees. Left cards are POSITIVE
/// so their left edge (the OUTER one) is the near edge; mirrored on the right — the row
/// curves round the viewer like the inside of a drum.</param>
/// <param name="Opacity">Card opacity: the entry/exit slot past the last real one is fully
/// transparent so a card sliding in from off-row fades in rather than popping.</param>
public readonly record struct CarouselPose(double X, double Scale, double AngleY, double Opacity);

/// <summary>
/// Pure slot geometry for the Cover Flow carousel (centre + <see cref="SideSlots"/> each
/// side), so the view can interpolate a card smoothly between slots during a skip and the
/// tests can pin the numbers without a visual tree. Positions are signed: 0 = centre,
/// negative = history, positive = up next; |position| is clamped to <see cref="ExitSlot"/>.
///
/// The look (09-22, the reference mockup): FIFTEEN cards SIDE BY SIDE on one midline, never
/// in front of or behind each other. Each side card turns its outer edge toward the viewer
/// (concave row). Every side card is the SAME size, tilt and brightness — the row stays
/// consistent out to the page edges instead of receding and fading. X is not tuned by
/// hand: each card's projected face starts <see cref="Gap"/> px after its neighbour's.
/// </summary>
public static class CoverFlowCarouselGeometry
{
    // ── Tuning ────────────────────────────────────────────────────────────────

    /// <summary>Slots on each side of the centre that hold a real card.</summary>
    public const int SideSlots = 7;

    /// <summary>One slot past the last visible one: where a card starts (fading in) when it
    /// enters the row and ends (faded out) when it leaves.</summary>
    public const int ExitSlot = SideSlots + 1;

    /// <summary>Perspective depth for the 3D tilt (Rotate3DTransform.Depth). Lower = stronger
    /// foreshortening: at 1400 a 30° tilt of a 320px card read as flat.</summary>
    public const double Depth = 610;

    /// <summary>Unscaled card width (300px artwork + 10px glass each side): the size the
    /// tilt is projected at, before the slot scale.</summary>
    public const double CardWidth = 320;

    /// <summary>Length of the slide for a one-slot skip.</summary>
    public static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(360);

    /// <summary>Added per extra slot of travel, so a jump to a far card (the row moves
    /// ~1000px) reads as a slide instead of a blink: 1 → 360ms, 4 → 630ms, 8 → 990ms.</summary>
    public static readonly TimeSpan SlidePerExtraSlot = TimeSpan.FromMilliseconds(90);

    /// <summary>Slide length for a skip of <paramref name="step"/> slots (sign ignored).</summary>
    public static TimeSpan SlideDurationFor(int step) =>
        SlideDuration + SlidePerExtraSlot * Math.Max(0, Math.Abs(step) - 1);

    /// <summary>Clear space between two neighbouring cards' projected faces, px: nearly
    /// touching, as in the mockup, but never overlapping.</summary>
    public const double Gap = 4;

    /// <summary>Tilt of EVERY side card, degrees — one angle, so the row reads as one
    /// consistent wall (the mockup), not a tunnel receding into the distance.</summary>
    public const double SideAngle = 30;

    /// <summary>Scale of EVERY side card: with the tilt its near (outer) edge stands ~87% of
    /// the centre card's height, as in the mockup. No shrink outward.</summary>
    public const double SideScale = 0.76;

    // ── Derived keyframes by |position| 0..ExitSlot ───────────────────────────

    private static readonly double[] Xs = Build(XAt);
    private static readonly double[] Scales = Build(ScaleAt);
    private static readonly double[] Angles = Build(AngleAt);
    private static readonly double[] Opacities = Build(OpacityAt);

    private static double[] Build(Func<int, double> f)
    {
        var k = new double[ExitSlot + 1];
        for (var i = 0; i <= ExitSlot; i++) k[i] = f(i);
        return k;
    }

    /// <summary>X(k): each card's inner (far) projected edge sits <see cref="Gap"/> px
    /// past its inner neighbour's outer (near) projected edge, so no two cards ever overlap.</summary>
    public static double XAt(int slot)
    {
        double x = 0;
        for (var i = 1; i <= slot; i++)
            x += OuterHalfWidth(i - 1) + Gap + InnerHalfWidth(i);
        return x;
    }

    /// <summary>Projected distance from a card's centre to its OUTER edge (the near one),
    /// after tilt, perspective and scale.</summary>
    public static double OuterHalfWidth(int slot) => ProjectedHalf(slot, near: true);

    /// <summary>Projected distance from a card's centre to its INNER edge (the far one).</summary>
    public static double InnerHalfWidth(int slot) => ProjectedHalf(slot, near: false);

    private static double ProjectedHalf(int slot, bool near)
    {
        var a = AngleAt(slot) * Math.PI / 180;
        var h = CardWidth / 2;
        var dx = h * Math.Cos(a);
        var dz = h * Math.Sin(a);
        return ScaleAt(slot) * dx * Depth / (near ? Depth - dz : Depth + dz);
    }

    /// <summary>Scale(k): 1 at the centre, <see cref="SideScale"/> for every side card.</summary>
    public static double ScaleAt(int slot) => slot == 0 ? 1 : SideScale;

    /// <summary>Angle(k): 0 at the centre, <see cref="SideAngle"/> for every side card.</summary>
    public static double AngleAt(int slot) => slot == 0 ? 0 : SideAngle;

    /// <summary>Opacity(k): every real card fully opaque; only the exit slot is transparent
    /// (a card entering or leaving the row fades rather than pops).</summary>
    public static double OpacityAt(int slot) => slot >= ExitSlot ? 0 : 1;

    /// <summary>Pose for a signed, possibly fractional, slot position.</summary>
    public static CarouselPose At(double position)
    {
        var sign = position < 0 ? -1 : 1;
        var d = Math.Min(Math.Abs(position), ExitSlot);
        var i = (int)Math.Floor(d);
        if (i >= ExitSlot) i = ExitSlot - 1;
        var t = d - i;

        double Lerp(double[] k) => k[i] + (k[i + 1] - k[i]) * t;

        return new CarouselPose(
            X: sign * Lerp(Xs),
            Scale: Lerp(Scales),
            AngleY: -sign * Lerp(Angles),
            Opacity: Lerp(Opacities));
    }

    /// <summary>Draw order: the centre on top, falling off with distance. Cards no longer
    /// overlap at rest; this only matters mid-slide.</summary>
    public static int ZIndexAt(double position) => 100 - (int)Math.Round(Math.Abs(position) * 10);

    /// <summary>Ease-out cubic for the slide.</summary>
    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var u = 1 - t;
        return 1 - u * u * u;
    }
}
