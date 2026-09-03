using System;
using Avalonia;
using Avalonia.Media;

namespace Noctis.Helpers;

/// <summary>
/// Gradient stops for the mini player's frosted band (the blurred artwork copy and
/// the contrast scrim under the controls), anchored to the CONTROLS in pixels
/// instead of to a fixed fraction of the card.
///
/// The band used to be 0.40 → 0.54 → 0.64 of the host height, measured at the
/// canonical 340×520 card where the controls start at ~0.66. On a taller card the
/// controls keep their pixel height, so 0.40 of the height climbed well above them
/// and frosted the middle of the cover (the heart of the "Neverita" sleeve was a
/// smudge). This keeps the fade the same pixel length and lets the full frost land
/// just above wherever the controls actually begin — identical to the old look at
/// 340×520, lower on every taller card.
/// </summary>
public static class MiniFrostBand
{
    /// <summary>The gap between the top of the controls and full frost, px (from the 340×520 measurement).</summary>
    public const double FullFrostLeadPx = 10;
    /// <summary>Length of the fade from clear to full frost, px (0.24 × 520).</summary>
    public const double FadeSpanPx = 125;
    /// <summary>Where the mid stop sits above full frost, px (0.10 × 520).</summary>
    public const double MidLeadPx = 52;

    public readonly record struct Stops(double Start, double Mid, double Full);

    /// <summary>
    /// Offsets (0..1, top → bottom) for a host <paramref name="hostHeight"/> px tall
    /// whose bottom-anchored controls block is <paramref name="controlsHeight"/> px
    /// (including its bottom margin).
    /// </summary>
    public static Stops Compute(double hostHeight, double controlsHeight)
    {
        if (hostHeight <= 0 || double.IsNaN(hostHeight) || double.IsNaN(controlsHeight))
            return new Stops(0.40, 0.54, 0.64);
        controlsHeight = Math.Max(0, controlsHeight);
        var full = Math.Clamp(1.0 - (controlsHeight + FullFrostLeadPx) / hostHeight, 0.0, 1.0);
        var start = Math.Clamp(full - FadeSpanPx / hostHeight, 0.0, full);
        var mid = Math.Clamp(full - MidLeadPx / hostHeight, start, full);
        return new Stops(start, mid, full);
    }

    /// <summary>Fresh reveal mask for the blurred copy (alpha ramp only).</summary>
    public static LinearGradientBrush CreateMask() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.40),
            new GradientStop(Color.FromArgb(0x8C, 0, 0, 0), 0.54),
            new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.64),
        },
    };

    /// <summary>Fresh contrast scrim (same measured alphas as the shared resource).</summary>
    public static LinearGradientBrush CreateScrim() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.40),
            new GradientStop(Color.FromArgb(0x7A, 0, 0, 0), 0.64),
            new GradientStop(Color.FromArgb(0xA0, 0, 0, 0), 1.0),
        },
    };

    /// <summary>Moves a mask/scrim pair's stops in place (mutate-in-place, no rebinding).</summary>
    public static void Apply(LinearGradientBrush mask, LinearGradientBrush scrim, Stops stops)
    {
        if (mask.GradientStops.Count >= 3)
        {
            mask.GradientStops[0].Offset = stops.Start;
            mask.GradientStops[1].Offset = stops.Mid;
            mask.GradientStops[2].Offset = stops.Full;
        }
        if (scrim.GradientStops.Count >= 3)
        {
            scrim.GradientStops[0].Offset = stops.Start;
            scrim.GradientStops[1].Offset = stops.Full;
            // The bottom stop stays at 1.
        }
    }
}
