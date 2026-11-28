using Avalonia.Controls;
using Avalonia.Layout;

namespace Noctis.Controls;

/// <summary>
/// Where the cells of one row of a karaoke line go: the one rule the live word layer
/// (<see cref="LyricWrapPanel"/>) and its resting copy (<see cref="KaraokeRestingWords"/>) both
/// place their rows by, so a word never moves when its line turns live or rests (#269).
/// A row is aligned by its glyphs, not its cells: a cell ends in blank (its word's trailing
/// space, the line-final pad), which left a right-aligned row short of the edge and a centred
/// one off centre. Right to left, the slots are mirrored (the words themselves never are).
/// </summary>
internal static class LyricRowPlacement
{
    /// <summary>
    /// Fills <paramref name="xs"/> with each cell's left edge, rounded to the pixel grid when
    /// <paramref name="dpi"/> is above 0. <paramref name="widths"/> are the cells in reading order;
    /// the glyph offsets are where the first and last cells' glyphs start and end inside them.
    /// </summary>
    public static void Place(ReadOnlySpan<double> widths, double firstGlyphLeft, double firstGlyphRight,
        double lastGlyphLeft, double lastGlyphRight, double available, WrapPanelItemsAlignment alignment,
        bool rightToLeft, double dpi, Span<double> xs)
    {
        var n = widths.Length;
        if (n == 0) return;
        double lastStart = 0;   // the last cell's start from the row's start, in reading order
        for (var i = 0; i < n - 1; i++) lastStart += widths[i];
        var first = widths[0];
        var last = widths[n - 1];

        // u: the row's start in reading order (before mirroring). Left to right the visible glyphs
        // run from u + firstGlyphLeft to u + lastStart + lastGlyphRight; right to left, mirrored,
        // from available - u - lastStart - last + lastGlyphLeft to available - u - first + firstGlyphRight.
        double u;
        if (!rightToLeft)
        {
            u = alignment switch
            {
                WrapPanelItemsAlignment.End => available - lastStart - lastGlyphRight,
                WrapPanelItemsAlignment.Center => available / 2 - (firstGlyphLeft + lastStart + lastGlyphRight) / 2,
                _ => -firstGlyphLeft,
            };
        }
        else
        {
            u = alignment switch
            {
                WrapPanelItemsAlignment.End => available - lastStart - last + lastGlyphLeft,
                WrapPanelItemsAlignment.Center => available / 2 - (lastStart + last - lastGlyphLeft + first - firstGlyphRight) / 2,
                _ => firstGlyphRight - first,
            };
        }

        var at = u;
        for (var i = 0; i < n; i++)
        {
            var x = rightToLeft ? available - at - widths[i] : at;
            xs[i] = dpi > 0 ? LayoutHelper.RoundLayoutValue(x, dpi) : x;
            at += widths[i];
        }
    }
}
