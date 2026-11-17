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

    /// <summary>FFT window in frames at 44.1/48 kHz (~43 ms, 23 Hz bins); scaled with the rate
    /// (<see cref="WindowScale"/>) so the bins keep that width at 96 and 192 kHz.</summary>
    public const int WindowFrames = 2048;

    /// <summary>Bass FFT window in frames at 44.1/48 kHz (~170 ms, 5.9 Hz bins), scaled like
    /// <see cref="WindowFrames"/>. Read only for the bands narrower than one short-window bin
    /// (below ~150-200 Hz): 23 Hz bins can't tell 30, 50 and 80 Hz apart (GitHub #205).</summary>
    public const int BassWindowFrames = 8192;

    /// <summary>The bass window is low-passed and decimated by this (times the rate scale) before
    /// its FFT: the bass bands only need content under a few hundred Hz, so a 512-point FFT at
    /// ~3 kHz gives the same 5.9 Hz bins as 8192 points at 48 kHz for a fraction of the work.</summary>
    private const int BassDecimation = 16;
    private const int BassFftSize = BassWindowFrames / BassDecimation;

    /// <summary>Lowest and highest band edges (Hz) for the log-spaced band mapping: the audible
    /// range (GitHub #196: 40 Hz..16 kHz cut both ends off). The top is capped below Nyquist.</summary>
    public const double MinHz = 20;
    public const double MaxHz = 20000;

    // Silence floor / full-scale span in dB for the 0..1 band normalisation.
    private const double FloorDb = -60;
    private const double CeilDb = -6;

    /// <summary>Full spectrum (GitHub #205): bands above 1 kHz tilted up toward the treble, as a
    /// studio analyser does, over a deeper floor, so a quiet top octave stays on screen. Pink
    /// noise (equal energy per octave, music's long-term balance) falls 3 dB per octave per bin,
    /// but a wide treble band takes the loudest of many bins, which already makes up about half
    /// of that: 1.5 dB per octave is what reads pink noise level from 1 to 20 kHz
    /// (SpectrumMeterTests). Below 1 kHz there is no tilt (GitHub #249): it pulled the lowest
    /// bands down, so every band reads at least as high as with the option off.</summary>
    public const double FullSpectrumTiltDbPerOctave = 1.5;
    private const double FullSpectrumFloorDb = -80;
    private const double TiltPivotHz = 1000;

    // Power of two: the 192 kHz bass window (32768 frames) plus the output latency it walks back by.
    private const int RingFrames = 131072;
    private readonly float[] _ring = new float[RingFrames];
    private long _head;            // frames written so far (next write index = _head & mask)
    private int _rate;
    private int _latencyMs;
    private long _headFeedMsBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);

    private readonly Func<double> _nowMs;

    // Reader scratch, sized per rate scale on first use; guarded by _readGate.
    private readonly object _readGate = new();
    private double[] _re = Array.Empty<double>(), _im = Array.Empty<double>(), _hann = Array.Empty<double>();
    private readonly double[] _bassRe = new double[BassFftSize], _bassIm = new double[BassFftSize];
    private readonly double[] _bassHann = BuildHann(BassFftSize);

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
        // Above stereo (Multichannel output) a sum over 2, like a downmix: a stereo song on a 5.1
        // engine (FL/FR only) reads exactly as on a stereo one instead of a third as loud.
        var inv = 1.0f / Math.Min(channels, 2);
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
    /// audio is flowing. <paramref name="fullSpectrum"/> applies the analyser weighting
    /// (<see cref="FullSpectrumTiltDbPerOctave"/>).
    /// </summary>
    public bool TryRead(double nowMs, Span<float> bands, bool fullSpectrum = false)
    {
        bands.Clear();
        if (bands.Length == 0 || !IsLive(nowMs)) return false;

        var head = Volatile.Read(ref _head);
        var rate = _rate;
        if (rate <= 0) return true;
        var scale = WindowScale(rate);
        var size = WindowFrames * scale;
        if (head < size) return true; // live but nothing heard yet

        // Frame currently at the speaker: the head is latencyMs ahead, less time already elapsed.
        var headFeedMs = BitConverter.Int64BitsToDouble(Volatile.Read(ref _headFeedMsBits));
        var aheadMs = Math.Max(0.0, _latencyMs - (nowMs - headFeedMs));
        var heard = head - (long)(aheadMs * rate / 1000.0);
        heard = Math.Clamp(heard, size, head);
        // Never reach back further than the ring holds (the writer may be overwriting).
        var oldestSafe = head - RingFrames + size;
        if (heard < oldestSafe) heard = oldestSafe;

        // One reader at a time: the visualizer and the row EQ read on the UI thread, but the
        // plugin API (ISpectrumSource) may call from any thread, and the scratch is resized per rate.
        lock (_readGate)
        {
            if (_re.Length != size)
            {
                _re = new double[size];
                _im = new double[size];
                _hann = BuildHann(size);
            }
            Window(heard - size, _re, _im, _hann);
            Fft.Forward(_re, _im);

            // The bass window, only when a band needs finer bins than the short one has. Centred on
            // the short window's centre so the bass lands with the rest; reaches into the frames
            // already rendered but not yet heard where the output runs that far ahead.
            double[]? bassRe = null, bassIm = null;
            var decimation = BassDecimation * scale;
            var span = BassFftSize * decimation + decimation; // + the low-pass tail
            if (NeedsBassWindow(rate, size, bands.Length))
            {
                var end = Math.Min(head, heard - size / 2 + span / 2);
                var start = end - span;
                if (start >= 0 && start >= head - RingFrames)
                {
                    DecimatedWindow(start, decimation);
                    Fft.Forward(_bassRe, _bassIm);
                    bassRe = _bassRe;
                    bassIm = _bassIm;
                }
            }

            MapBands(_re, _im, bassRe, bassIm, rate, rate / (double)decimation, bands, fullSpectrum);
        }
        return true;
    }

    /// <summary>Window size multiplier for a sample rate: 1 up to 48 kHz, 2 at 88.2/96 kHz, 4 at
    /// 176.4/192 kHz, and so on, so a bin stays ~21-23 Hz wide at every rate. A fixed 2048 frames
    /// gave 94 Hz bins at 192 kHz: every band under ~94 Hz read the first bin (GitHub #205).</summary>
    public static int WindowScale(int sampleRate)
    {
        var scale = 1;
        while (sampleRate / scale > 64000) scale *= 2;
        return scale;
    }

    /// <summary>The narrowest (first) band is narrower than one bin of the short window.</summary>
    private static bool NeedsBassWindow(int sampleRate, int size, int bandCount)
    {
        var top = Math.Min(MaxHz, (size / 2 - 1) * (double)sampleRate / size);
        var firstHi = MinHz * Math.Pow(top / MinHz, 1.0 / bandCount);
        return firstHi - MinHz < (double)sampleRate / size;
    }

    /// <summary>The bass window: each output sample is a triangle-weighted mean of 2D-1 input
    /// frames (two cascaded D-frame moving averages, nulls at multiples of the decimated rate,
    /// so what would fold into the bass bands is ~40 dB down), then the Hann window.</summary>
    private void DecimatedWindow(long start, int decimation)
    {
        var mask = RingFrames - 1;
        var taps = 2 * decimation - 1;
        var gain = 1.0 / ((double)decimation * decimation);
        for (var j = 0; j < BassFftSize; j++)
        {
            var at = start + (long)j * decimation;
            double sum = 0;
            for (var m = 0; m < taps; m++)
            {
                var w = m < decimation ? m + 1 : taps - m;
                sum += w * _ring[(int)((at + m) & mask)];
            }
            _bassRe[j] = sum * gain * _bassHann[j];
            _bassIm[j] = 0;
        }
    }

    private void Window(long start, double[] re, double[] im, double[] hann)
    {
        var mask = RingFrames - 1;
        for (var n = 0; n < re.Length; n++)
        {
            re[n] = _ring[(int)((start + n) & mask)] * hann[n];
            im[n] = 0;
        }
    }

    /// <summary>Single-window band mapping (no bass window, no weighting). Exposed for tests.</summary>
    public static void MapBands(double[] re, double[] im, int sampleRate, Span<float> bands)
        => MapBands(re, im, null, null, sampleRate, sampleRate, bands, fullSpectrum: false);

    /// <summary>
    /// Log-spaced band levels from FFT results: each band spans [edge_k, edge_k+1) Hz, takes the
    /// peak magnitude of the bins centred within, and maps dB onto 0..1 between the silence floor
    /// and full scale. A band narrower than one bin of the short window reads the bass window
    /// (<paramref name="bassRe"/>, finer bins) when there is one. A band still narrower than one
    /// bin holds no bin centre of its own: it reads the spectrum at its centre frequency,
    /// interpolated between the two bins around it, so neighbouring bands don't repeat one bin
    /// as a flat plateau (GitHub #196). Bins are normalised so a full-scale sine reads ~0 dB in
    /// either window. <paramref name="fullSpectrum"/> adds the analyser weighting.
    /// </summary>
    internal static void MapBands(double[] re, double[] im, double[]? bassRe, double[]? bassIm,
        int sampleRate, double bassSampleRate, Span<float> bands, bool fullSpectrum)
    {
        var binHz = (double)sampleRate / re.Length;
        var floorDb = fullSpectrum ? FullSpectrumFloorDb : FloorDb;
        var count = bands.Length;
        var logMin = Math.Log(MinHz);
        var logSpan = Math.Log(Math.Min(MaxHz, (re.Length / 2 - 1) * binHz)) - logMin;

        for (var b = 0; b < count; b++)
        {
            var lo = Math.Exp(logMin + logSpan * b / count);
            var hi = Math.Exp(logMin + logSpan * (b + 1) / count);
            // The decimated bass window is only flat well under its Nyquist.
            var peak = hi - lo < binHz && bassRe != null && bassIm != null && hi < bassSampleRate / 8
                ? BandPeak(bassRe, bassIm, bassSampleRate, lo, hi)
                : BandPeak(re, im, sampleRate, lo, hi);
            var db = peak <= 1e-9 ? floorDb : 20 * Math.Log10(peak);
            // Lift only above the pivot: a tilt below it took up to 8.5 dB off the 20 Hz band, so
            // loud bass read LOWER with Full spectrum on than off (GitHub #249). Silence stays at the
            // floor: tilting it drew a 0.06-0.09 shelf over empty treble bands.
            if (fullSpectrum && peak > 1e-9)
                db += FullSpectrumTiltDbPerOctave * Math.Max(0, Math.Log2(Math.Sqrt(lo * hi) / TiltPivotHz));
            bands[b] = (float)Math.Clamp((db - floorDb) / (CeilDb - floorDb), 0, 1);
        }
    }

    /// <summary>Peak magnitude in [lo, hi) Hz of one FFT result (see <see cref="MapBands(double[], double[], double[], double[], int, double, Span{float}, bool)"/>).</summary>
    private static double BandPeak(double[] re, double[] im, double sampleRate, double lo, double hi)
    {
        var n = re.Length;
        var half = n / 2;
        var binHz = sampleRate / n;
        // Hann window halves the coherent gain; a full-scale sine puts amplitude n/4 in its bin.
        var norm = 4.0 / n;
        if (hi - lo < binHz)
        {
            var pos = Math.Sqrt(lo * hi) / binHz;
            var k0 = Math.Clamp((int)Math.Floor(pos), 1, half - 2);
            var frac = Math.Clamp(pos - k0, 0, 1);
            var m0 = Math.Sqrt(re[k0] * re[k0] + im[k0] * im[k0]);
            var m1 = Math.Sqrt(re[k0 + 1] * re[k0 + 1] + im[k0 + 1] * im[k0 + 1]);
            return (m0 + (m1 - m0) * frac) * norm;
        }
        double peak = 0;
        var binLo = Math.Max(1, (int)Math.Ceiling(lo / binHz));
        var binHi = Math.Min(half - 1, Math.Max(binLo, (int)Math.Ceiling(hi / binHz) - 1));
        for (var k = binLo; k <= binHi; k++)
        {
            var mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * norm;
            if (mag > peak) peak = mag;
        }
        return peak;
    }

    private static double[] BuildHann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }
}
