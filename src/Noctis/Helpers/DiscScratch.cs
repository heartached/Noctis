using System;
using Avalonia;

namespace Noctis.Helpers;

/// <summary>
/// What the spinning now-playing disc talks to while it is scratched (Settings → Player →
/// Disc Scratching). <see cref="ViewModels.PlayerViewModel"/> implements it.
/// </summary>
public interface IDiscScratchTarget
{
    /// <summary>The hand goes on the disc. False when there is nothing to scratch (no track,
    /// unknown length); the disc then does nothing.</summary>
    bool BeginScratch();

    /// <summary>The disc has been turned <paramref name="degrees"/> since the grab (clockwise =
    /// forward). Returns the degrees actually applied: less when the track's start or end
    /// stopped the turn, so the disc stops there too.</summary>
    double ScratchTo(double degrees);

    /// <summary>The hand comes off: one seek to where the disc was left, then playback carries on
    /// (or stays paused).</summary>
    void EndScratch();
}

/// <summary>
/// The arithmetic of disc scratching: pointer angle around the disc's centre, the turn between
/// two pointer positions, and how far a turn moves the track.
/// </summary>
public static class DiscScratch
{
    /// <summary>One turn of a 33⅓ rpm record: 60 / 33⅓ = 1.8 seconds of music per revolution.</summary>
    public const double SecondsPerRevolution = 1.8;

    /// <summary>Pointer travel (DIP, either axis) before a press becomes a scratch. Below it the
    /// press stays a plain click. Same as the mini player's window-drag threshold.</summary>
    public const double DragThreshold = 4;

    /// <summary>Inner part of the disc (fraction of its radius) where the pointer angle is too
    /// unstable to read; moves in there are ignored until the pointer leaves it.</summary>
    public const double DeadZoneFraction = 0.08;

    /// <summary>A scratch never lands on the very end of the track: the release would resume
    /// into an immediate track end and skip to the next song.</summary>
    public static readonly TimeSpan EndGuard = TimeSpan.FromMilliseconds(250);

    /// <summary>Angle of <paramref name="point"/> around <paramref name="centre"/> in degrees.
    /// Screen Y points down, so the angle grows clockwise, the same sense as RotateTransform.</summary>
    public static double AngleOf(Point point, Point centre) =>
        Math.Atan2(point.Y - centre.Y, point.X - centre.X) * 180.0 / Math.PI;

    /// <summary>The shortest turn from <paramref name="fromDegrees"/> to <paramref name="toDegrees"/>,
    /// in (-180, 180]. Positive = clockwise.</summary>
    public static double TurnBetween(double fromDegrees, double toDegrees)
    {
        var d = (toDegrees - fromDegrees) % 360.0;
        if (d > 180.0) d -= 360.0;
        else if (d <= -180.0) d += 360.0;
        return d;
    }

    /// <summary>Track time covered by a turn of <paramref name="degrees"/>.</summary>
    public static TimeSpan OffsetFor(double degrees) =>
        TimeSpan.FromSeconds(degrees / 360.0 * SecondsPerRevolution);

    /// <summary>The turn that covers <paramref name="offset"/> of track time.</summary>
    public static double DegreesFor(TimeSpan offset) =>
        offset.TotalSeconds / SecondsPerRevolution * 360.0;

    /// <summary><paramref name="position"/> held inside the track: 0 … duration − <see cref="EndGuard"/>.</summary>
    public static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        var max = duration - EndGuard;
        if (max < TimeSpan.Zero) max = TimeSpan.Zero;
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return position > max ? max : position;
    }

    /// <summary>Where a turn of <paramref name="degrees"/> from <paramref name="origin"/> lands, clamped to the track.</summary>
    public static TimeSpan TargetFor(TimeSpan origin, double degrees, TimeSpan duration) =>
        Clamp(origin + OffsetFor(degrees), duration);

    /// <summary>True once the pointer has left the press point by <see cref="DragThreshold"/> on either axis.</summary>
    public static bool PastThreshold(Point press, Point now) =>
        Math.Abs(now.X - press.X) >= DragThreshold || Math.Abs(now.Y - press.Y) >= DragThreshold;
}
