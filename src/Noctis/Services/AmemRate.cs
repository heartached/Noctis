namespace Noctis.Services;

/// <summary>
/// The highest sample rate LibVLC 3's audio callbacks (amem) deliver correctly. Its rate wraps
/// at 2^18 = 262144 Hz: asked for 384000 it hands over 121856 Hz worth of frames per block
/// (384000 − 262144), so every block played ~3× fast and left ~71 ms of silence behind it, and
/// 262144 itself is refused as "0 Hz" (Discord, Ardhito 10-08: a 384 kHz device; measured
/// against LibVLC 3.0.23 at 48k/96k/176.4k/192k/256k = exact, 262144+ = broken).
/// </summary>
public static class AmemRate
{
    /// <summary>The ceiling every rate handed to amem is fitted under.</summary>
    public const int Max = 192000;

    /// <summary>
    /// <paramref name="rate"/> halved until it is at most <see cref="Max"/>: 384000 → 192000,
    /// 352800 → 176400, 768000 → 192000. Halving keeps the 44.1k/48k family and an exact 2:1
    /// ratio to the device, which shared mode's converter (or VLC's resampler) then bridges.
    /// </summary>
    public static int Fit(int rate)
    {
        while (rate > Max) rate /= 2;
        return rate;
    }
}
