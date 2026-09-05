using System;
using System.Diagnostics;
using System.Threading;
using Noctis.Services.AudioAnalysis;

namespace Noctis.Services;

/// <summary>
/// Live spectrum derived from the samples the app actually renders. Fed from the same
/// post-gain tap as <see cref="BeatMeter"/> on the render thread; read from the UI thread
/// once per frame by the audio visualizer.
///
/// The render thread only copies mono frames into a ring (a few flops per sample, no
/// allocations). The FFT runs on the reader's thread against a window that ends at the
/// frame being HEARD right now: the renderer runs ahead of the speaker by the output
/// buffer depth, so the newest frames in the ring are still in the future and the read
/// walks back by the latency (minus the time elapsed since the last feed).
/// </summary>
public sealed class SpectrumMeter
{
    /// <summary>Process-wide meter every output chain feeds and the visualizer reads.</summary>
    public static SpectrumMeter Shared { get; } = new();

    /// <summary>No feed for this long means no live audio — the visualizer decays to rest.</summary>
    public const double LiveWindowMs = BeatMeter.LiveWindowMs;

    /// <summary>FFT window in frames (~43 ms at 48 kHz — enough bass resolution for the lowest band).</summary>
    public const int WindowFrames = 2048;

    /// <summary>Lowest and highest band edges (Hz) for the log-spaced band mapping.</summary>
    public const double MinHz = 40;
    public const double MaxHz = 16000;

    // Silence floor / full-scale span in dB for the 0..1 band normalisation.
    private const double FloorDb = -60;
    private const double CeilDb = -6;

    private const int RingFrames = 16384; // power of two, ~340 ms at 48 kHz
    private readonly float[] _ring = new float[RingFrames];
    private long _head;            // frames written so far (next write index = _head & mask)
    private int _rate;
    private int _latencyMs;
    private long _headFeedMsBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);

    private readonly Func<double> _nowMs;

    // Reader scratch (UI thread only).
    private readonly double[] _re = new double[WindowFrames];
    private readonly double[] _im = new double[WindowFrames];
    private readonly double[] _hann = BuildHann(WindowFrames);

    public SpectrumMeter() : this(null) { }

    /// <summary>Clock injection for tests; null uses the wall clock.</summary>
    public SpectrumMeter(Func<double>? nowMs)
    {
        _nowMs = nowMs ?? (static () => Stopwatch.GetElapsedTime(0).TotalMilliseconds);
    }

    /// <summary>Milliseconds on the meter's own clock.</summary>
    public double NowMs => _nowMs();

    /// <summary>True while samples arrived within <see cref="LiveWindowMs"/>.</summary>
    public bool IsLive(double nowMs)
        => nowMs - BitConverter.Int64BitsToDouble(Volatile.Read(ref _headFeedMsBits)) <= LiveWindowMs;

    /// <summary>
    /// Render-thread entry point: interleaved float frames as rendered. <paramref name="latencyMs"/>
    /// is how far ahead of the speaker this point in the chain runs.
    /// </summary>
    public void Feed(float[] buffer, int offset, int count, int channels, int sampleRate, int latencyMs)
    {
        if (count <= 0 || channels <= 0 || sampleRate <= 0) return;
        var frames = count / channels;
        var inv = 1.0f / channels;
        var head = _head;
        var mask = RingFrames - 1;
        var end = offset + frames * channels;
        for (var i = offset; i < end; i += channels)
        {
            float mono = 0;
            for (var ch = 0; ch < channels; ch++) mono += buffer[i + ch];
            _ring[(int)(head & mask)] = mono * inv;
            head++;
        }
        _rate = sampleRate;
        _latencyMs = latencyMs;
        // Publish the head after the samples; the feed time stamps the head frame.
        Volatile.Write(ref _head, head);
        Volatile.Write(ref _headFeedMsBits, BitConverter.DoubleToInt64Bits(_nowMs()));
    }

    /// <summary>
    /// UI-thread read: fills <paramref name="bands"/> with 0..1 log-spaced band levels for the
    /// audio being heard at <paramref name="nowMs"/>. Returns false (bands zeroed) when no live
    /// audio is flowing.
    /// </summary>
    public bool TryRead(double nowMs, Span<float> bands)
    {
        bands.Clear();
        if (bands.Length == 0 || !IsLive(nowMs)) return false;

        var head = Volatile.Read(ref _head);
        var rate = _rate;
        if (rate <= 0 || head < WindowFrames) return true; // live but nothing heard yet

        // Frame currently at the speaker: the head is latencyMs ahead, less time already elapsed.
        var headFeedMs = BitConverter.Int64BitsToDouble(Volatile.Read(ref _headFeedMsBits));
        var aheadMs = Math.Max(0.0, _latencyMs - (nowMs - headFeedMs));
        var heard = head - (long)(aheadMs * rate / 1000.0);
        heard = Math.Clamp(heard, WindowFrames, head);
        // Never reach back further than the ring holds (the writer may be overwriting).
        var oldestSafe = head - RingFrames + WindowFrames;
        if (heard < oldestSafe) heard = oldestSafe;

        var mask = RingFrames - 1;
        var start = heard - WindowFrames;
        for (var n = 0; n < WindowFrames; n++)
        {
            _re[n] = _ring[(int)((start + n) & mask)] * _hann[n];
            _im[n] = 0;
        }
        Fft.Forward(_re, _im);

        MapBands(_re, _im, rate, bands);
        return true;
    }

    /// <summary>
    /// Log-spaced band levels from an FFT result: each band spans [edge_k, edge_k+1) Hz, takes the
    /// peak magnitude within, and maps dB onto 0..1 between the silence floor and full scale.
    /// Bins are normalised so a full-scale sine reads ~0 dB. Exposed for tests.
    /// </summary>
    public static void MapBands(double[] re, double[] im, int sampleRate, Span<float> bands)
    {
        var n = re.Length;
        var half = n / 2;
        var binHz = (double)sampleRate / n;
        // Hann window halves the coherent gain; a full-scale sine puts amplitude n/4 in its bin.
        var norm = 4.0 / n;
        var count = bands.Length;
        var logMin = Math.Log(MinHz);
        var logSpan = Math.Log(MaxHz) - logMin;

        for (var b = 0; b < count; b++)
        {
            var lo = Math.Exp(logMin + logSpan * b / count);
            var hi = Math.Exp(logMin + logSpan * (b + 1) / count);
            var binLo = Math.Max(1, (int)Math.Floor(lo / binHz));
            var binHi = Math.Min(half - 1, Math.Max(binLo, (int)Math.Ceiling(hi / binHz) - 1));
            double peak = 0;
            for (var k = binLo; k <= binHi; k++)
            {
                var mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * norm;
                if (mag > peak) peak = mag;
            }
            var db = peak <= 1e-9 ? FloorDb : 20 * Math.Log10(peak);
            bands[b] = (float)Math.Clamp((db - FloorDb) / (CeilDb - FloorDb), 0, 1);
        }
    }

    private static double[] BuildHann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }
}
