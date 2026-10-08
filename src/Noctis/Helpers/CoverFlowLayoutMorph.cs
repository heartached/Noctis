using System;
using Avalonia;

namespace Noctis.Helpers;

/// <summary>
/// One Cover Flow layout layer's look during a layout switch (Carousel / Cascade / Collage /
/// empty state): its opacity plus a uniform scale and a translation, applied as a render
/// transform about the layer's top-left corner (layer-local point p lands on Scale·p + (X, Y)).
/// </summary>
public readonly record struct LayerPose(double Opacity, double Scale, double X, double Y)
{
    public static readonly LayerPose Rest = new(1, 1, 0, 0);

    public bool HasTransform => Scale != 1 || X != 0 || Y != 0;

    public Matrix Matrix => Matrix.CreateScale(Scale, Scale) * Matrix.CreateTranslation(X, Y);

    /// <summary>Where a layer-local rect lands on screen (in the layer's own, untransformed space).</summary>
    public Rect Apply(Rect local) => new(
        local.X * Scale + X, local.Y * Scale + Y, local.Width * Scale, local.Height * Scale);
}

/// <summary>
/// Pure maths for the Cover Flow layout switch (owner 10-08: switching Carousel / Cascade /
/// Collage should glide, not snap). Every layout shows the playing cover, so the switch is a
/// shared-element morph done with whole-layer transforms: the outgoing layer scales and moves
/// so its playing cover lands on the incoming layout's playing cover while it fades, and the
/// incoming layer starts transformed so its playing cover sits exactly on the outgoing one and
/// eases home. Both covers follow the same path, so the cover reads as one card gliding and
/// resizing while everything around it crossfades. A layer without a playing cover (empty
/// state, nothing playing) falls back to a plain fade with a slight grow.
/// </summary>
public static class CoverFlowLayoutMorph
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(420);

    /// <summary>The incoming layer is fully opaque after this share of the duration, so the
    /// layer on top covers the outgoing one before that one fades away (no see-through dip).</summary>
    public const double FadeInShare = 0.55;

    /// <summary>Fallback (no shared cover): the incoming layer grows from this scale.</summary>
    public const double FallbackScale = 0.96;

    // Our own bezier, not Avalonia.SplineEasing (see CubicBezierEase). Movement uses the
    // sidebar's long, soft settle; fades use a quick decelerate in and an accelerate out.
    private static readonly CubicBezierEase MoveEase = new(0.32, 0.72, 0, 1);
    private static readonly CubicBezierEase FadeInEase = new(0.2, 0, 0, 1);
    private static readonly CubicBezierEase FadeOutEase = new(0.4, 0, 1, 1);

    /// <summary>Transform that puts a layer's hero rect <paramref name="from"/> (layer-local,
    /// untransformed) onto <paramref name="to"/> (same space). Uniform scale by width: the
    /// covers are square.</summary>
    public static LayerPose Map(Rect from, Rect to, double opacity)
    {
        var s = to.Width / from.Width;
        return new LayerPose(opacity, s, to.Center.X - s * from.Center.X, to.Center.Y - s * from.Center.Y);
    }

    /// <summary>Scale <paramref name="scale"/> about the centre of a layer of size <paramref name="size"/>.</summary>
    public static LayerPose ScaleAbout(Size size, double scale, double opacity) =>
        new(opacity, scale, (1 - scale) * size.Width / 2, (1 - scale) * size.Height / 2);

    /// <summary>Pose at linear progress <paramref name="t"/> (0..1) between two poses.
    /// Every layer moves on the same curve (so shared covers stay on top of each other);
    /// opacity rises fast when the layer is coming in and falls late when it is going out.</summary>
    public static LayerPose Interpolate(LayerPose from, LayerPose to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        var m = MoveEase.Ease(t);
        var f = to.Opacity >= from.Opacity
            ? FadeInEase.Ease(Math.Min(1, t / FadeInShare))
            : FadeOutEase.Ease(t);
        return new LayerPose(
            Lerp(from.Opacity, to.Opacity, f),
            Lerp(from.Scale, to.Scale, m),
            Lerp(from.X, to.X, m),
            Lerp(from.Y, to.Y, m));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
