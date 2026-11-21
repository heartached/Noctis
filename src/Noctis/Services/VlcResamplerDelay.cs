namespace Noctis.Services;

/// <summary>
/// How far VLC 3's speex resampler (--speex-resampler-quality=10) delays an input, in frames
/// at its output rate. VLC puts the resampler in every input's chain, even at equal rates, so
/// each input comes out half a filter late, and the half still in the filter when the input
/// ends is never drained (harness 11-19: the last 2.7-2.9 ms of every track at a gapless
/// seam). The splice engine's players now run VLC's pass-through resampler and convert
/// themselves (GaplessRateStage); this delay is still compensated for the inputs VLC keeps
/// converting, the ones above what its audio callbacks take (AmemRate).
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
