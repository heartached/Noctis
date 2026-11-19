namespace Noctis.Services;

/// <summary>
/// How far VLC 3's speex resampler (--speex-resampler-quality=10) delays an input, in frames
/// at the engine's rate. VLC puts it in every input's chain, even a 48 kHz file into the
/// 48 kHz engine (log: "using audio resampler module speex_resampler" for each track), so
/// each input comes out half a filter late, and the half still in the filter when the input
/// ends is never drained. At a gapless seam the incoming delay takes the place of the
/// outgoing tail; between two different delays the timeline stepped (harness 11-19, 48 kHz
/// engine: -0.23 ms going from a 44.1 kHz track into a 48/88.2/96 kHz or DSD one, +0.23 ms
/// coming back: 139.3 against 128.0-128.4 frames).
/// </summary>
public static class VlcResamplerDelay
{
    // speexdsp quality_map[10].base_length.
    private const int BaseFilterLength = 256;

    /// <summary>The module name LibVLC logs for its speex resampler.</summary>
    public const string SpeexModule = "speex_resampler";

    private const string UsingResamplerPrefix = "using audio resampler module \"";

    /// <summary>
    /// The resampler a LibVLC log line says it put in an input's chain
    /// (main: <c>using audio resampler module "speex_resampler"</c>), or null for any other line.
    /// </summary>
    public static string? ModuleFromLogLine(string? message)
    {
        if (message == null || !message.StartsWith(UsingResamplerPrefix, StringComparison.Ordinal))
            return null;
        var end = message.IndexOf('"', UsingResamplerPrefix.Length);
        return end > UsingResamplerPrefix.Length ? message[UsingResamplerPrefix.Length..end] : null;
    }

    /// <summary>
    /// Whether <see cref="OutputFrames"/> is the delay LibVLC really adds: only with speex at
    /// quality 10 (<paramref name="speexForced"/>) and speex actually loaded. A system LibVLC
    /// without the speex plugin falls back to another resampler (soxr, samplerate, ugly) with
    /// another delay, and correcting by the speex one would step every mixed-rate seam.
    /// Before any input has logged its resampler (<paramref name="loggedModule"/> null), only
    /// <paramref name="speexKnownBundled"/> (a LibVLC shipped with the plugin) counts as speex.
    /// </summary>
    public static bool CorrectionApplies(bool speexForced, string? loggedModule, bool speexKnownBundled) =>
        speexForced && (loggedModule == null
            ? speexKnownBundled
            : string.Equals(loggedModule, SpeexModule, StringComparison.Ordinal));

    /// <summary>
    /// speexdsp's output latency (speex_resampler_get_output_latency) for a
    /// <paramref name="inputRate"/> to <paramref name="outputRate"/> resampler at quality 10;
    /// 0 when either rate is unknown.
    /// </summary>
    public static int OutputFrames(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) return 0;
        var g = Gcd(inputRate, outputRate);
        long num = inputRate / g, den = outputRate / g;
        long filterLength = BaseFilterLength;
        if (num > den)
        {
            // Down-sampling widens the filter by the ratio, rounded up to a multiple of 8
            // (speexdsp update_filter).
            filterLength = filterLength * num / den;
            filterLength = ((filterLength - 1) & ~7L) + 8;
        }
        return (int)((filterLength / 2 * den + (num >> 1)) / num);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
