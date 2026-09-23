using System;
using Avalonia;

namespace Noctis.Helpers;

/// <summary>
/// Where the mini player's top-left goes when its size jumps (a style switch, the Lyrics
/// toggle) — GitHub #75. Resizing keeps the top-left fixed, so a card parked near the
/// right or bottom edge of the screen grew straight off it. The window now stays anchored
/// to the screen edge it is nearest to (judged by its centre) and is then clamped inside
/// the work area. All rects are in screen pixels; sizes arrive in DIPs with the screen's
/// scaling, like Window.Width/Height and Position.
/// </summary>
public static class MiniPlayerPlacement
{
    /// <summary>Top-left for the window at <paramref name="current"/> once it is
    /// <paramref name="targetWidth"/> × <paramref name="targetHeight"/> DIPs: right half of
    /// the work area keeps the right edge, bottom half keeps the bottom edge, then the
    /// result is clamped into <paramref name="workArea"/>.</summary>
    public static PixelPoint Anchored(PixelRect current, double targetWidth, double targetHeight,
        double scaling, PixelRect workArea)
    {
        var size = ToPixels(targetWidth, targetHeight, scaling);
        var keepRight = current.X + current.Width / 2.0 > workArea.X + workArea.Width / 2.0;
        var keepBottom = current.Y + current.Height / 2.0 > workArea.Y + workArea.Height / 2.0;
        var anchored = new PixelPoint(
            keepRight ? current.Right - size.Width : current.X,
            keepBottom ? current.Bottom - size.Height : current.Y);
        return Clamp(anchored, size, workArea);
    }

    /// <summary>Moves <paramref name="position"/> the least distance that puts a window of
    /// <paramref name="size"/> inside <paramref name="workArea"/>. A window larger than the
    /// area on an axis is pinned to the area's top/left edge on that axis.</summary>
    public static PixelPoint Clamp(PixelPoint position, PixelSize size, PixelRect workArea)
    {
        var x = size.Width >= workArea.Width
            ? workArea.X
            : Math.Clamp(position.X, workArea.X, workArea.Right - size.Width);
        var y = size.Height >= workArea.Height
            ? workArea.Y
            : Math.Clamp(position.Y, workArea.Y, workArea.Bottom - size.Height);
        return new PixelPoint(x, y);
    }

    /// <summary>A DIP size on a screen with <paramref name="scaling"/>, in whole pixels.</summary>
    public static PixelSize ToPixels(double width, double height, double scaling)
    {
        var s = scaling > 0 && double.IsFinite(scaling) ? scaling : 1;
        return new PixelSize(
            (int)Math.Round(Math.Max(0, width) * s),
            (int)Math.Round(Math.Max(0, height) * s));
    }
}
