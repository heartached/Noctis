namespace Noctis.Services;

/// <summary>
/// Spots LibVLC 3's mpg123 decoder wedged on damaged MP3 data. Its decoder module turns
/// mpg123's resync off (MPG123_NO_RESYNC) and on a decode error only breaks out of the
/// block, without a flush (modules/codec/mpg123.c, VLC 3.0.x), so one run of junk bytes
/// between frames leaves the handle failing every frame fed after it: "mpg123_decode_frame
/// error: A generic mpg123 error." once per frame (~38 a second) and no audio, until a
/// seek flushes it. Discord 10-28 ("Trouble Playing Long MP3 File"): 380+ in 10 s, silent
/// from 31:13 on; the harness (LongMp3PlaybackHarnessTests) reproduces it with 64 KiB of
/// junk between two encodes. A storm is <see cref="Threshold"/> such lines within
/// <see cref="WindowMs"/>; it reports once, then not again for <see cref="CooldownMs"/>.
/// Not thread-safe (the caller locks). Time is passed in so the policy is unit-testable.
/// </summary>
internal sealed class DecoderWedgeDetector
{
    public const int Threshold = 12;
    public const long WindowMs = 2000;
    public const long CooldownMs = 4000;

    private long _windowStartMs = long.MinValue;
    private int _count;
    private long _lastStormMs = long.MinValue;

    /// <summary>Errors counted in the storm that was last reported (for the log line).</summary>
    public int LastStormErrors { get; private set; }

    /// <summary>The VLC line a wedged mpg123 decoder prints for every frame it is fed. Pure.</summary>
    public static bool IsWedgeLine(string? module, string? message)
        => module == "mpg123" && message != null &&
           message.StartsWith("mpg123_decode_frame error", StringComparison.Ordinal);

    /// <summary>Feeds one wedge line seen at <paramref name="nowMs"/>; true when it completes a storm.</summary>
    public bool Accept(long nowMs)
    {
        if (_lastStormMs != long.MinValue && nowMs - _lastStormMs < CooldownMs)
            return false;
        if (_windowStartMs == long.MinValue || nowMs - _windowStartMs > WindowMs)
        {
            _windowStartMs = nowMs;
            _count = 0;
        }
        if (++_count < Threshold)
            return false;
        LastStormErrors = _count;
        _lastStormMs = nowMs;
        _windowStartMs = long.MinValue;
        _count = 0;
        return true;
    }
}
