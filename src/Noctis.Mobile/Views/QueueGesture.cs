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
}
