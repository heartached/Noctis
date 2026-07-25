namespace Noctis.Helpers;

/// <summary>
/// Scroll-offset math shared by the lyrics page and the lyrics panel: where the
/// scroll viewer must sit so the active line lands on its anchor line.
/// </summary>
public static class LyricsScrollAnchor
{
    /// <summary>Fraction of the viewport height the active line is anchored at.</summary>
    public const double AnchorRatio = 0.22;

    /// <summary>
    /// Offset that puts the line at <paramref name="childTop"/> on the anchor line,
    /// clamped to what the viewer can actually scroll to.
    /// </summary>
    /// <remarks>
    /// The upper clamp matters at the end of a song. Once the tail of the lyrics fits on
    /// screen the anchor target runs past the end of the content, and while
    /// <c>ScrollViewer.Offset</c> coerces the write back into range, the callers animate
    /// towards the raw target: the per-line cascade stagger is driven by the animation's
    /// delta, so every remaining line change slid the visible lines down and let them
    /// settle again with no scrolling to justify it. On the page the unclamped value also
    /// desynced the "did the user scroll?" bookkeeping, which reads a coerced offset as a
    /// manual scroll and pauses auto-follow.
    /// </remarks>
    /// <param name="childTop">Top of the active line in content coordinates.</param>
    /// <param name="childHeight">Height of the active line.</param>
    /// <param name="viewportHeight">Visible height of the scroll viewer.</param>
    /// <param name="extentHeight">Total content height of the scroll viewer.</param>
    public static double ComputeAnchorOffset(
        double childTop, double childHeight, double viewportHeight, double extentHeight)
    {
        var offset = childTop - (viewportHeight * AnchorRatio) + (childHeight / 2.0);

        // Only clamp once the viewport has been measured. During the first layout pass both
        // sizes read 0, and clamping against that would pin every target to 0 and strand
        // the initial jump-to-line at the top of the list.
        if (viewportHeight > 0)
            offset = Math.Min(offset, extentHeight - viewportHeight);

        return Math.Max(0, offset);
    }
}
