namespace Noctis.Services.Waveform;

/// <summary>
/// A track's waveform reduced to a fixed number of time buckets (GitHub #93). Each bucket
/// holds the bucket's peak (max |sample|) and RMS level, both normalized to the track's
/// loudest bucket and quantized to a byte: 0 = silence, 255 = the loudest bucket of that
/// measure. Immutable — one instance is shared by every seek bar showing the track.
/// </summary>
public sealed class WaveformData
{
    private readonly byte[] _peaks;
    private readonly byte[] _rms;

    public WaveformData(byte[] peaks, byte[] rms)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        ArgumentNullException.ThrowIfNull(rms);
        if (peaks.Length == 0 || peaks.Length != rms.Length)
            throw new ArgumentException("Peak and RMS arrays must be non-empty and the same length.");
        _peaks = peaks;
        _rms = rms;
    }

    public int BucketCount => _peaks.Length;

    public ReadOnlySpan<byte> Peaks => _peaks;

    public ReadOnlySpan<byte> Rms => _rms;

    /// <summary>
    /// Display level of one bucket, 0..1. RMS carries the shape (verse vs chorus, quiet
    /// intro, breakdown); peaks alone read as a flat block on a loud modern master, where
    /// nearly every second touches full scale (checked against real library tracks while
    /// tuning). A small share of the peak keeps sparse, spiky audio (a lone click track,
    /// plucks over silence) from vanishing under its low RMS.
    /// </summary>
    public float LevelAt(int bucket)
    {
        var rms = _rms[bucket] / 255f;
        var peak = _peaks[bucket] / 255f;
        return Math.Max(rms, peak * PeakShare);
    }

    /// <summary>Weight of the peak in <see cref="LevelAt"/>.</summary>
    internal const float PeakShare = 0.3f;

    /// <summary>
    /// Resamples the buckets to <paramref name="destination"/>.Length display bars: each
    /// bar is the mean level of the buckets its time span covers (the nearest bucket when
    /// there are more bars than buckets). The mean, not the max: a bar spans several
    /// seconds on a narrow seek bar, and the max of that many buckets flattens a loud
    /// song into a solid block.
    /// </summary>
    public void ResampleLevels(Span<float> destination)
    {
        var bars = destination.Length;
        if (bars == 0) return;
        var n = BucketCount;
        for (var i = 0; i < bars; i++)
        {
            var start = (int)((long)i * n / bars);
            var end = (int)((long)(i + 1) * n / bars);
            if (end <= start) end = Math.Min(n, start + 1);
            var sum = 0f;
            for (var b = start; b < end; b++) sum += LevelAt(b);
            destination[i] = sum / (end - start);
        }
    }
}
