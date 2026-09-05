using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Live beat pulse derived from the samples the app actually renders. Fed from the
/// output chain (after mute/gain, so what drives the pulse is what is heard) on the
/// render thread; read from the UI thread once per frame by the flowing-artwork
/// background, which breathes the blurred cover on every beat.
///
/// Detection is deliberately cheap — a one-pole bass low-pass, 20 ms energy blocks,
/// and an onset whenever a block's energy jumps sharply over the previous block AND
/// well above its slow running average (with a short refractory period so one kick
/// never double-fires). No FFT, no allocations, a few dozen flops per frame.
///
/// The renderer runs ahead of the speaker by the output buffer depth, so every block
/// is stamped with the time it will be HEARD (feed time + latency) and readers ask for
/// "the pulse at now" — the beat lands on the visual when it lands on the ear.
/// </summary>
public sealed class BeatMeter
{
    /// <summary>Process-wide meter every output chain feeds and every surface reads.</summary>
    public static BeatMeter Shared { get; } = new();

    /// <summary>Envelope decay time constant (ms): a pulse of 1 falls to ~37% here.</summary>
    public const double DecayMs = 160;

    /// <summary>No feed for this long means no live audio — readers fall back to the
    /// BPM grid (or nothing). Comfortably above any render quantum, below "paused".</summary>
    public const double LiveWindowMs = 400;

    // Bass band: kicks and bass hits live below this; hi-hats and vocals are what we
    // do NOT want driving the artwork.
    private const double BassCutoffHz = 150;
    // 20 ms: over a whole cycle of the lowest bass so a steady tone's block RMS
    // doesn't wobble (10 ms blocks read a held 55 Hz note as a string of "rises").
    private const double BlockMs = 20;
    private const double RefractoryMs = 110;
    // A real hit jumps this much block-to-block; a sustained note never does.
    private const double RiseRatio = 1.3;
    // Slow average of block energy the onset test compares against.
    private const double AverageTauMs = 600;
    private const double OnsetRatio = 2.0;
    // Below this RMS a block is silence-ish; keeps fade tails/noise from firing.
    private const double NoiseFloor = 0.004;

    private readonly Func<double> _nowMs;

    // Render-thread state.
    private double _lp;
    private double _blockSum;
    private int _blockFrames;
    private int _blockTargetFrames;
    private int _blockRate;
    private double _prevEnergy;
    private double _avgEnergy;
    private double _envelope;
    private double _lastOnsetMs = double.NegativeInfinity;

    // Ring of (due time, envelope) — the writer publishes the index after the entry.
    private const int RingSize = 128; // 2.56 s of 20 ms blocks
    private readonly double[] _ringDueMs = new double[RingSize];
    private readonly double[] _ringPulse = new double[RingSize];
    private int _ringWrite = -1;
    private long _lastFeedMsBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);
    private int _onsetCount;

    public BeatMeter() : this(null) { }

    /// <summary>Clock injection for tests; null uses the wall clock.</summary>
    public BeatMeter(Func<double>? nowMs)
    {
        _nowMs = nowMs ?? (static () => Stopwatch.GetElapsedTime(0).TotalMilliseconds);
    }

    /// <summary>Milliseconds on the meter's own clock.</summary>
    public double NowMs => _nowMs();

    /// <summary>Onsets detected since construction (diagnostics/tests).</summary>
    public int OnsetCount => Volatile.Read(ref _onsetCount);

    /// <summary>The raw envelope after the most recent block (tests).</summary>
    public double Envelope => _envelope;

    /// <summary>True while samples arrived within <see cref="LiveWindowMs"/>.</summary>
    public bool IsLive(double nowMs)
        => nowMs - BitConverter.Int64BitsToDouble(Volatile.Read(ref _lastFeedMsBits)) <= LiveWindowMs;

    /// <summary>
    /// Render-thread entry point: interleaved float frames as rendered. <paramref name="latencyMs"/>
    /// is how far ahead of the speaker this point in the chain runs.
    /// </summary>
    public void Feed(float[] buffer, int offset, int count, int channels, int sampleRate, int latencyMs)
    {
        if (count <= 0 || channels <= 0 || sampleRate <= 0) return;
        var now = _nowMs();
        Volatile.Write(ref _lastFeedMsBits, BitConverter.DoubleToInt64Bits(now));

        if (_blockRate != sampleRate)
        {
            _blockRate = sampleRate;
            _blockTargetFrames = Math.Max(1, (int)(sampleRate * BlockMs / 1000));
            _blockSum = 0;
            _blockFrames = 0;
        }

        // One-pole low-pass coefficient for the bass band at this rate.
        var a = 1.0 - Math.Exp(-2.0 * Math.PI * BassCutoffHz / sampleRate);
        var lp = _lp;
        var frames = count / channels;
        var end = offset + frames * channels;
        var inv = 1.0 / channels;

        // Blocks complete mid-buffer; stamp each with where it sits inside this read so
        // a long buffer still yields evenly-timed entries.
        var blockDurationMs = 1000.0 * _blockTargetFrames / sampleRate;
        var frameIndex = 0;

        for (var i = offset; i < end; i += channels)
        {
            double mono = 0;
            for (var ch = 0; ch < channels; ch++) mono += buffer[i + ch];
            mono *= inv;
            lp += a * (mono - lp);
            _blockSum += lp * lp;
            _blockFrames++;
            frameIndex++;

            if (_blockFrames >= _blockTargetFrames)
            {
                var blockEndMs = now + 1000.0 * frameIndex / sampleRate;
                CompleteBlock(blockEndMs, blockDurationMs, latencyMs);
            }
        }
        _lp = lp;
    }

    private void CompleteBlock(double blockEndMs, double blockDurationMs, int latencyMs)
    {
        var energy = Math.Sqrt(_blockSum / _blockFrames);
        _blockSum = 0;
        _blockFrames = 0;

        // Onset: energy jumps sharply over the previous block AND above its slow
        // average, outside the refractory window. A sustained bass note fires once
        // (silence → tone) and then plateaus: no block-to-block jump, and the
        // average catches up within AverageTauMs.
        var threshold = Math.Max(NoiseFloor, OnsetRatio * _avgEnergy);
        if (energy > threshold && energy > _prevEnergy * RiseRatio && blockEndMs - _lastOnsetMs >= RefractoryMs)
        {
            _lastOnsetMs = blockEndMs;
            _envelope = 1.0;
            Interlocked.Increment(ref _onsetCount);
        }
        else
        {
            _envelope *= Math.Exp(-blockDurationMs / DecayMs);
        }

        _avgEnergy += (energy - _avgEnergy) * Math.Min(1.0, blockDurationMs / AverageTauMs);
        _prevEnergy = energy;

        var next = (_ringWrite + 1) & (RingSize - 1);
        _ringDueMs[next] = blockEndMs + latencyMs;
        _ringPulse[next] = _envelope;
        Volatile.Write(ref _ringWrite, next);
    }

    /// <summary>
    /// UI-thread read: the pulse (0..1) being heard at <paramref name="nowMs"/>, decayed
    /// continuously between blocks. False when no live audio is flowing.
    /// </summary>
    public bool TryRead(double nowMs, out double pulse)
    {
        pulse = 0;
        if (!IsLive(nowMs)) return false;

        var write = Volatile.Read(ref _ringWrite);
        if (write < 0) return true; // live but nothing rendered yet

        // Newest entry already due; walk back at most one ring of entries.
        for (var n = 0; n < RingSize; n++)
        {
            var idx = (write - n) & (RingSize - 1);
            var due = _ringDueMs[idx];
            if (due <= nowMs)
            {
                pulse = _ringPulse[idx] * Math.Exp(-(nowMs - due) / DecayMs);
                return true;
            }
        }
        return true; // everything is still in the future (just started): silence for now
    }
}

/// <summary>
/// Pass-through <see cref="ISampleProvider"/> that feeds <see cref="BeatMeter"/> and
/// <see cref="SpectrumMeter"/> with whatever flows through it. Sits at the end of an
/// output chain, after gain and mute.
/// </summary>
public sealed class BeatTapProvider : ISampleProvider
{
    private readonly ISampleProvider _inner;
    private readonly BeatMeter _meter;
    private readonly SpectrumMeter _spectrum;
    private readonly int _latencyMs;

    public BeatTapProvider(ISampleProvider inner, int latencyMs, BeatMeter? meter = null, SpectrumMeter? spectrum = null)
    {
        _inner = inner;
        _latencyMs = latencyMs;
        _meter = meter ?? BeatMeter.Shared;
        _spectrum = spectrum ?? SpectrumMeter.Shared;
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            try
            {
                _meter.Feed(buffer, offset, read, WaveFormat.Channels, WaveFormat.SampleRate, _latencyMs);
                _spectrum.Feed(buffer, offset, read, WaveFormat.Channels, WaveFormat.SampleRate, _latencyMs);
            }
            catch
            {
                // A visual nicety must never break the render thread.
            }
        }
        return read;
    }
}
