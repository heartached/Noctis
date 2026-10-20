namespace Noctis.Mobile.Services;

/// <summary>
/// How big a cover the playback session (notification, lock screen, Bluetooth) is given.
/// Media3 copies MediaMetadata's artwork bytes on every player update, so a full-size scan
/// (a 4000 px PNG is 16.5 MB) in each queued item ran the app out of memory when songs
/// were passed over quickly. A cached cover over <see cref="MaxBytes"/> is decoded at
/// <see cref="SampleSize"/> and re-encoded; smaller ones go as they are.
/// </summary>
public static class SessionArtwork
{
    /// <summary>Covers up to this size are handed over unchanged.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>The longest edge of a shrunk cover, in pixels: more than a lock screen shows.</summary>
    public const int MaxEdge = 1024;

    public static bool NeedsShrinking(long length) => length > MaxBytes;

    /// <summary>The power-of-two decode step (BitmapFactory's inSampleSize) that brings the
    /// longest edge to <see cref="MaxEdge"/> or below; 1 when the size is unknown.</summary>
    public static int SampleSize(int width, int height)
    {
        var edge = Math.Max(width, height);
        var sample = 1;
        while (edge / sample > MaxEdge) sample *= 2;
        return sample;
    }
}
