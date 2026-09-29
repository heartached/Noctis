namespace Noctis.Services.Waveform;

/// <summary>
/// Streaming reduction of decoded mono samples to a <see cref="WaveformData"/>.
///
/// The decoder does not know the sample count up front (a duration tag can be wrong or
/// missing), so samples are first folded into fixed-size blocks (peak + sum of squares);
/// when the block table fills, adjacent blocks merge pairwise and the block size doubles.
/// Memory therefore stays bounded (<see cref="MaxBlocks"/> blocks) for any length, from a
/// two-second jingle to a ten-hour audiobook. <see cref="Build"/> then maps the blocks
/// onto the requested number of equal-time buckets and normalizes them. No I/O, no
/// threads — unit-tested directly.
/// </summary>
public sealed class WaveformAccumulator
{
    /// <summary>Buckets per track: enough for a seek bar ~3000px wide at 3px per bar.</summary>
    public const int DefaultBucketCount = 1024;

    internal const int InitialBlockSize = 256;
    internal const int MaxBlocks = 1 << 16; // even, so a pairwise merge is exact

    private float[] _blockPeak = new float[4096];
    private double[] _blockSumSq = new double[4096];
    private int _blocks;
    private int _blockSize = InitialBlockSize;

    private float _curPeak;
    private double _curSumSq;
    private int _curCount;

    /// <summary>Samples fed so far.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Current block size in samples (doubles on each compaction).</summary>
    internal int BlockSize => _blockSize;

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            // A NaN/∞ from a broken decode must not poison the whole track's scale.
            var a = float.IsFinite(s) ? Math.Abs(s) : 0f;
            if (a > _curPeak) _curPeak = a;
            _curSumSq += (double)a * a;
            if (++_curCount == _blockSize) CommitBlock();
        }
        SampleCount += samples.Length;
    }

    private void CommitBlock()
    {
        if (_blocks == _blockPeak.Length)
        {
            var grown = Math.Min(MaxBlocks, _blockPeak.Length * 2);
            Array.Resize(ref _blockPeak, grown);
            Array.Resize(ref _blockSumSq, grown);
        }
        _blockPeak[_blocks] = _curPeak;
        _blockSumSq[_blocks] = _curSumSq;
        _blocks++;
        _curPeak = 0;
        _curSumSq = 0;
        _curCount = 0;
        // Compact as soon as the table fills, before the next block starts: every stored
        // block then spans exactly _blockSize samples.
        if (_blocks == MaxBlocks) Compact();
    }

    /// <summary>Merges blocks pairwise and doubles the block size (the table is full).</summary>
    private void Compact()
    {
        var half = _blocks / 2;
        for (var i = 0; i < half; i++)
        {
            _blockPeak[i] = Math.Max(_blockPeak[2 * i], _blockPeak[2 * i + 1]);
            _blockSumSq[i] = _blockSumSq[2 * i] + _blockSumSq[2 * i + 1];
        }
        _blocks = half;
        _blockSize *= 2;
    }

    /// <summary>
    /// Reduces everything fed so far to at most <paramref name="bucketCount"/> buckets
    /// (fewer when the audio has fewer blocks than that). Null when nothing was fed.
    /// </summary>
    public WaveformData? Build(int bucketCount = DefaultBucketCount)
    {
        if (bucketCount <= 0) throw new ArgumentOutOfRangeException(nameof(bucketCount));
        if (SampleCount == 0) return null;

        // The trailing partial block counts with its real sample count.
        var hasTail = _curCount > 0;
        var total = _blocks + (hasTail ? 1 : 0);
        var n = Math.Min(bucketCount, total);

        var peaks = new float[n];
        var rms = new float[n];
        float maxPeak = 0, maxRms = 0;
        for (var b = 0; b < n; b++)
        {
            var start = (int)((long)b * total / n);
            var end = (int)((long)(b + 1) * total / n);
            float peak = 0;
            double sumSq = 0;
            long count = 0;
            for (var j = start; j < end; j++)
            {
                float p;
                double sq;
                int c;
                if (j < _blocks) { p = _blockPeak[j]; sq = _blockSumSq[j]; c = _blockSize; }
                else { p = _curPeak; sq = _curSumSq; c = _curCount; }
                if (p > peak) peak = p;
                sumSq += sq;
                count += c;
            }
            var r = count > 0 ? (float)Math.Sqrt(sumSq / count) : 0f;
            peaks[b] = peak;
            rms[b] = r;
            if (peak > maxPeak) maxPeak = peak;
            if (r > maxRms) maxRms = r;
        }

        return new WaveformData(Quantize(peaks, maxPeak), Quantize(rms, maxRms));
    }

    private static byte[] Quantize(float[] values, float max)
    {
        var bytes = new byte[values.Length];
        if (max <= 0) return bytes; // digital silence: a flat line
        for (var i = 0; i < values.Length; i++)
            bytes[i] = (byte)Math.Clamp((int)Math.Round(values[i] / max * 255f), 0, 255);
        return bytes;
    }
}
