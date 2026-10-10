namespace Noctis.Helpers;

/// <summary>
/// GitHub #88: while a rubber-band drag in the queue panel sits past the list's top or bottom
/// edge zone, the list scrolls toward the pointer, faster the deeper it is. Expressed as a
/// rate so the frame-clock loop in MainWindow moves the same distance per second whatever
/// the refresh rate; the old DispatcherTimer version moved depth × 0.5 px per nominal 16 ms
/// tick, which this reproduces at dt = 0.016.
/// </summary>
public static class QueueBandAutoScroll
{
    /// <summary>Pixels per second per pixel of edge depth (0.5 px per 16 ms).</summary>
    public const double PixelsPerSecondPerDepthPx = 0.5 / 0.016;

    /// <summary>Edge depth beyond which the speed stops growing.</summary>
    public const double MaxDepthPx = 60;

    /// <summary>
    /// Signed scroll delta for this frame: negative above the top edge zone, positive below
    /// the bottom one, 0 inside the list.
    /// </summary>
    public static double Step(double pointerY, double listHeight, double edge, double dtSeconds)
    {
        var depth = pointerY < edge ? pointerY - edge
            : pointerY > listHeight - edge ? pointerY - (listHeight - edge)
            : 0;
        if (depth == 0 || dtSeconds <= 0) return 0;
        return Math.Clamp(depth, -MaxDepthPx, MaxDepthPx) * PixelsPerSecondPerDepthPx * dtSeconds;
    }
}
