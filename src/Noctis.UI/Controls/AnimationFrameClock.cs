namespace Noctis.Controls;

/// <summary>
/// The timestamp the hand-driven animation loops (<see cref="FoldingListBoxItem"/>, the
/// liquid drag-to-reorder) read from <c>TopLevel.RequestAnimationFrame</c>. In the app that is
/// the render clock. Test seam: <see cref="Override"/> replaces it, so a headless test moves
/// those animations by exact steps instead of by however much wall-clock time passed between
/// its frames: the sidebar folder fold and reorder tests failed now and then in a big batch,
/// when a busy machine let a 280 ms fold finish inside "six frames". Production leaves it null.
/// </summary>
internal static class AnimationFrameClock
{
    /// <summary>Test seam: the frame time to use instead of the render clock's.</summary>
    internal static Func<TimeSpan>? Override { get; set; }

    /// <summary>The frame time an animation loop should use for a frame the render clock
    /// stamped <paramref name="frameTime"/>.</summary>
    internal static TimeSpan Now(TimeSpan frameTime) => Override?.Invoke() ?? frameTime;
}
