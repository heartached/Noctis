namespace Noctis.Mobile.Views;

/// <summary>The Queue page's gesture thresholds and drop maths, kept pure for tests.</summary>
internal static class QueueGesture
{
    /// <summary>Horizontal travel before a press on a row counts as a swipe.</summary>
    internal const double SlopDip = 12;

    /// <summary>A swipe removes past whichever is larger: this, or <see cref="RemoveFraction"/> of the row.</summary>
    internal const double MinRemoveDip = 96;

    internal const double RemoveFraction = 0.35;

    internal static bool ShouldRemove(double dx, double rowWidth) =>
        dx < 0 && -dx >= Math.Max(MinRemoveDip, rowWidth * RemoveFraction);

    /// <summary>Where a row dragged by <paramref name="dy"/> lands: whole rows, rounded, clamped to the list.</summary>
    internal static int TargetIndex(int from, double dy, double rowHeight, int count)
    {
        if (count <= 0 || rowHeight <= 0) return from;
        return Math.Clamp(from + (int)Math.Round(dy / rowHeight), 0, count - 1);
    }

    /// <summary>How near the list's top or bottom edge a dragged row's finger scrolls the list.</summary>
    internal const double AutoScrollEdgeDip = 72;

    /// <summary>The list's speed with the finger on that edge, or past it.</summary>
    internal const double AutoScrollMaxDipPerSecond = 1200;

    /// <summary>
    /// How fast (dip a second, up negative) the list scrolls under a dragged row whose finger is at
    /// <paramref name="fingerY"/> from the list's top: nothing away from the edges, then faster the
    /// nearer the finger is to one (squared: a slow crawl just inside the band, full speed on or
    /// past the edge). The band is at most a third of a short list, so its middle never scrolls.
    /// </summary>
    internal static double AutoScrollVelocity(double fingerY, double viewportHeight)
    {
        if (viewportHeight <= 0) return 0;
        var edge = Math.Min(AutoScrollEdgeDip, viewportHeight / 3);
        double depth;
        if (fingerY < edge) depth = -(edge - fingerY) / edge;
        else if (fingerY > viewportHeight - edge) depth = (fingerY - (viewportHeight - edge)) / edge;
        else return 0;
        depth = Math.Clamp(depth, -1, 1);
        return Math.Sign(depth) * depth * depth * AutoScrollMaxDipPerSecond;
    }

    /// <summary>The list's offset after <paramref name="seconds"/> of auto-scroll: it stops at either end.</summary>
    internal static double AutoScrollOffset(double offset, double velocity, double seconds, double maxOffset) =>
        Math.Clamp(offset + velocity * seconds, 0, Math.Max(0, maxOffset));

    /// <summary>Where a row dragged in the one list (History on) lands: past every other row whose
    /// centre its own centre is below, plus <paramref name="skippedAbove"/> rows scrolled away above
    /// those (not laid out, so not in <paramref name="otherMidpoints"/>).</summary>
    internal static int DropIndex(IEnumerable<double> otherMidpoints, double center, int skippedAbove = 0) =>
        skippedAbove + otherMidpoints.Count(m => m < center);
}
