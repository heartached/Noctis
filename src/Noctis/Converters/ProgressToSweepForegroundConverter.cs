using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Noctis.Converters;

/// <summary>
/// Builds the karaoke sweep as a text <b>Foreground</b> brush from
/// <c>[0]=Progress</c>, <c>[1]=the lyrics foreground brush</c>, and optionally
/// <c>[2]=layout width</c> + <c>[3]=font size</c>: a CONSTANT-width feathered band
/// slides left-to-right with progress; its stops clamp to the word box and carry the
/// alpha the band would have at the clamped position, so the visible edges move at
/// constant velocity and the band straddles token boundaries (progress runs a
/// little past [0..1] on neighbouring words — see KaraokeSweep.BandProgress).
///
/// The previous collapsing feather (width = min(F, raw, 1-raw)) kept the stop
/// midpoint linear but parked the solid edge at the start of every token and rushed
/// it at 2× near the end — on multi-second held words that read as the sweep
/// stalling at every letter boundary (worst on CJK, one cell per character).
///
/// Painted as Foreground rather than OpacityMask on purpose: PushOpacityMask makes the
/// compositor render through an intermediate layer, and the sibling held-note glow
/// (an Effect visual) got composited inside that layer — multiplied by the sweep
/// gradient and hard-clipped at the overlay's bounds, which drew a cut-off box around
/// emphasis words. A gradient foreground paints the same wipe with no layer at all.
/// </summary>
public class ProgressToSweepForegroundConverter : IMultiValueConverter
{
    /// <summary>Half-width of the band as a fraction of the token — fallback when
    /// the view doesn't supply layout width + font size.</summary>
    public const double Feather = 0.06;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = values.Count > 0
            ? values[0] switch
            {
                double d => d,
                float f => f,
                _ => 0.0,
            }
            : 0.0;
        var fg = values.Count > 1 ? values[1] as IBrush : null;
        var color = (fg as ISolidColorBrush)?.Color ?? Colors.White;

        // AMLL's feather is a fixed ~0.5em, not a fraction of the word: with the
        // width-relative fallback a narrow token (a single CJK character cell) gets
        // a near hard edge. When the view passes its layout width and font size,
        // use a 0.25em half-band instead, bounded so wide melisma words keep a
        // visible soft edge and narrow cells stay inside the sentinel contract.
        var feather = Feather;
        if (values.Count > 3
            && values[2] is double width && width > 0
            && values[3] is double fontSize && fontSize > 0)
        {
            feather = Math.Clamp(0.25 * fontSize / width, 0.04, 0.45);
        }

        // Band fully before / fully past the token (covers the inert sentinels).
        if (raw <= -feather) return Brushes.Transparent;
        if (raw >= 1 + feather) return fg ?? Brushes.White;

        // The true band edges may hang past the word box; clamp the stops and give
        // the clamped endpoint the alpha the ramp has at that position — a stop
        // pinned at the boundary keeps *brightening* instead of parking the edge.
        var loT = raw - feather;
        var hiT = raw + feather;
        var lo = Math.Max(loT, 0.0);
        var hi = Math.Min(hiT, 1.0);
        double AlphaAt(double x) => Math.Clamp((hiT - x) / (hiT - loT), 0.0, 1.0);

        // NOTE: this allocates a brush per evaluation, and the lyrics timer rewrites each
        // active word's Progress continuously — so this is a real per-frame allocation
        // during karaoke playback. It is NOT safe to cache a brush on the converter: the
        // converter is declared with x:Key in LyricsView/LyricsPanelView, i.e. a single
        // shared instance across every word cell, so a cached brush would be handed to
        // all of them and every word would render the last-written word's offsets.
        // Fixing it properly means moving the gradient onto the word cell itself (bind
        // GradientStop.Offset directly) rather than producing the brush here.
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(WithAlpha(color, AlphaAt(lo)), lo),
                // Same RGB throughout so the feather fades without darkening.
                new GradientStop(WithAlpha(color, AlphaAt(hi)), hi),
            },
        };
    }

    private static Color WithAlpha(Color color, double factor) =>
        Color.FromArgb((byte)Math.Round(color.A * factor), color.R, color.G, color.B);
}
