using System;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Noctis.Services;

// ── True-gapless splice core ─────────────────────────────────────────────
//
// VLC 3 cannot do gapless: every input tears down and recreates its audio
// output stream, and two MediaPlayers can never be sample-aligned (independent
// clocks, no latency feedback). The only true-gapless route on this stack is
// the one mpv and VLC 4 use — ONE persistent output stream that never stops
// across track changes, fed by decode-ahead, with tracks spliced back-to-back
// in our own buffer domain so the boundary is crossed inside a single render
// read: zero inserted samples.
//
// This file is the device-independent core of that engine: per-track PCM
// segments (filled from VLC's amem callbacks on the decoder threads) and the
// splice provider a WASAPI render stream pulls from. No WASAPI/device code
// lives here so the splice semantics are unit-testable headless.
//
// Threading model per segment: one writer (that player's VLC decoder thread),
// one reader (the render thread), plus control calls (flush on seek from a VLC
// thread, abandon from the engine). A plain lock + Monitor pulses is enough —
// blocks are ~10ms of audio, contention is trivial. Writers block when the
// ring is full (this back-pressures VLC's decoder, exactly like the existing
// WasapiGainOutput.Write), and MUST be unblocked by Abandon() before the
// engine calls player.Stop() — Stop joins the decoder thread, so a writer
// left blocked here would deadlock the stop (the WasapiGainOutput backpressure
// lesson).

/// <summary>
/// One track's staged PCM at its native rate/channels: float interleaved ring
/// written by that player's amem play callback, read by the splice provider.
/// </summary>
public sealed class GaplessTrackSegment
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private int _readIdx;
    private int _writeIdx;
    private int _count;              // samples (not frames) currently buffered
    private bool _endOfStream;
    private bool _abandoned;
    private bool _started;           // first samples handed to the render side
    private long _consumedFrames;    // frames handed to the render side
    private long _basePositionMs;    // media position of the first frame after creation/flush

    public int SampleRate { get; }
    public int Channels { get; }

    // Identity of the MediaPlayer feeding this segment, so the engine can
    // discriminate callback senders (the standby's transport events must never
    // drive the shared sink).
    public object? Source { get; }

    public GaplessTrackSegment(int sampleRate, int channels, object? source, int capacitySeconds = 15, long basePositionMs = 0)
    {
        SampleRate = Math.Clamp(sampleRate, 1000, 384000);
        Channels = Math.Clamp(channels, 1, 2);
        Source = source;
        _basePositionMs = Math.Max(0, basePositionMs);
        _ring = new float[SampleRate * Channels * Math.Clamp(capacitySeconds, 2, 60)];
    }

    public bool EndOfStream { get { lock (_gate) return _endOfStream; } }
    public bool Abandoned { get { lock (_gate) return _abandoned; } }
    public bool IsFinished { get { lock (_gate) return (_endOfStream || _abandoned) && _count == 0; } }
    public int BufferedSamples { get { lock (_gate) return _count; } }

    /// <summary>Audible media position of this segment = base + consumed.</summary>
    public long PositionMs
    {
        get
        {
            lock (_gate)
                return _basePositionMs + _consumedFrames * 1000 / SampleRate;
        }
    }

    /// <summary>
    /// Append S16 interleaved PCM (the only format VLC 3 amem delivers).
    /// Blocks while the ring is full so the decoder thread is back-pressured;
    /// returns false when the segment was abandoned or the wait timed out
    /// (dead render side) — callers must simply drop the block then.
    /// </summary>
    public bool Write(ReadOnlySpan<short> pcm, int timeoutMs = 2000)
    {
        var offset = 0;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (offset < pcm.Length)
        {
            lock (_gate)
            {
                while (_count == _ring.Length)
                {
                    if (_abandoned || _endOfStream)
                        return false;
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0 || !Monitor.Wait(_gate, (int)Math.Min(remaining, 100)))
                    {
                        if (Environment.TickCount64 >= deadline)
                            return false;
                    }
                }
                if (_abandoned || _endOfStream)
                    return false;

                var free = _ring.Length - _count;
                var toCopy = Math.Min(free, pcm.Length - offset);
                for (var i = 0; i < toCopy; i++)
                {
                    _ring[_writeIdx] = pcm[offset + i] / 32768f;
                    _writeIdx = (_writeIdx + 1) % _ring.Length;
                }
                _count += toCopy;
                offset += toCopy;
                Monitor.PulseAll(_gate);
            }
        }
        return true;
    }

    /// <summary>Render-side read; returns samples copied (0 = underrun or finished).</summary>
    public int Read(float[] dest, int destOffset, int maxSamples)
    {
        lock (_gate)
        {
            var toCopy = Math.Min(_count, maxSamples);
            for (var i = 0; i < toCopy; i++)
            {
                dest[destOffset + i] = _ring[_readIdx];
                _readIdx = (_readIdx + 1) % _ring.Length;
            }
            _count -= toCopy;
            _consumedFrames += toCopy / Channels;
            if (toCopy > 0)
            {
                _started = true;
                Monitor.PulseAll(_gate);
            }
            return toCopy;
        }
    }

    /// <summary>VLC drain callback: no further samples, play out what is buffered.</summary>
    public void MarkEndOfStream()
    {
        lock (_gate)
        {
            _endOfStream = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// VLC flush callback (seek/stop): discard buffered PCM. The engine passes
    /// the seek target so position reporting stays truthful; a teardown flush
    /// AFTER drain must be ignored by the caller (it would eat the tail).
    /// </summary>
    public void Flush(long newBasePositionMs)
    {
        lock (_gate)
        {
            _readIdx = 0;
            _writeIdx = 0;
            _count = 0;
            _consumedFrames = 0;
            _basePositionMs = Math.Max(0, newBasePositionMs);
            // Re-arm the pre-buffer gate: post-seek delivery ramps exactly like
            // input start, and a once-per-life gate let the first trickle blocks
            // render against silence — the post-seek chop the gate exists to stop.
            _started = false;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// True once the segment may be rendered: already started, finished (play
    /// out whatever exists), or holding at least thresholdSamples. Rendering a
    /// fresh segment the instant its first block lands chops audio against
    /// silence while the decoder's delivery ramps — audible as a buzz at every
    /// input start. A staged next track holds seconds, so the gapless splice
    /// always passes this instantly.
    /// </summary>
    public bool ReadyToRender(int thresholdSamples)
    {
        lock (_gate)
            return _started || _endOfStream || _abandoned || _count >= thresholdSamples;
    }

    /// <summary>Kill the segment and unblock any writer (call BEFORE player.Stop()).</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _abandoned = true;
            _count = 0;
            Monitor.PulseAll(_gate);
        }
    }
}

/// <summary>
/// The persistent render source: a FIFO of segments rendered back-to-back.
/// The active→next boundary is crossed inside a single Read call, so no
/// silence is ever inserted between tracks. Segments whose rate/channels
/// differ from the sink format are adapted (WDL resampler + channel map).
/// Always returns the full requested count (silence on underrun) so the
/// device stream never stops — the WasapiGainOutput ReadFully pattern.
/// </summary>
public sealed class GaplessSpliceProvider : ISampleProvider
{
    private readonly object _gate = new();
    private readonly System.Collections.Generic.Queue<GaplessTrackSegment> _pending = new();
    private GaplessTrackSegment? _active;
    private ISampleProvider? _activeAdapted;

    public WaveFormat WaveFormat { get; }

    /// <summary>Raised (on the render thread) when the audible boundary is crossed.</summary>
    public event Action<GaplessTrackSegment>? SegmentStarted;

    private readonly int _startThresholdMs;

    // Underrun hysteresis: after a mid-track 0-read, hold silence until this much
    // audio re-buffers instead of consuming each trickle block the instant it
    // lands — instant consumption alternates audio/silence at the ~10ms read
    // cadence, audible as a ~100Hz chop buzz. EOS/Abandon bypass the hold.
    private const int UnderrunRefillMs = 50;
    // Silence must run at least this long before the next audio fades in, so the
    // resampler's few-sample seam zeros can never arm a fade at a gapless splice.
    private const int FadeArmMs = 5;

    private readonly int _startFadeSamples;
    private readonly int _fadeArmSamples;
    private int _silentSamples;        // contiguous silence emitted so far (render thread only)
    private int _fadeRemaining;        // samples left of an in-progress fade-in
    private int _refillSamplesNeeded;  // >0 while the underrun hold is armed

    public GaplessSpliceProvider(int sinkRate, int sinkChannels, int startThresholdMs = 0, int startFadeMs = 0)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            Math.Clamp(sinkRate, 1000, 384000), Math.Clamp(sinkChannels, 1, 2));
        _startThresholdMs = Math.Clamp(startThresholdMs, 0, 2000);
        _startFadeSamples = WaveFormat.SampleRate * WaveFormat.Channels * Math.Clamp(startFadeMs, 0, 100) / 1000;
        _fadeArmSamples = WaveFormat.SampleRate * WaveFormat.Channels * FadeArmMs / 1000;
        // Born silent: the very first audio the provider ever renders fades in.
        _silentSamples = _fadeArmSamples;
    }

    public GaplessTrackSegment? ActiveSegment { get { lock (_gate) return _active; } }

    public void Enqueue(GaplessTrackSegment segment)
    {
        lock (_gate)
            _pending.Enqueue(segment);
    }

    /// <summary>
    /// Drop every queued segment and the active one (full stop / new queue).
    /// The render side falls to silence until the next Enqueue.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _active?.Abandon();
            _active = null;
            _activeAdapted = null;
            while (_pending.Count > 0)
                _pending.Dequeue().Abandon();
        }
    }

    /// <summary>
    /// Drop queued segments but keep the active one playing (queue changed
    /// while the current track keeps going).
    /// </summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            while (_pending.Count > 0)
                _pending.Dequeue().Abandon();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var written = 0;
        while (written < count)
        {
            ISampleProvider? adapted;
            GaplessTrackSegment? active;
            lock (_gate)
            {
                if (_active == null || _active.IsFinished || _active.Abandoned)
                {
                    _active = null;
                    _activeAdapted = null;
                    if (_pending.Count > 0)
                    {
                        _active = _pending.Dequeue();
                        _activeAdapted = Adapt(_active);
                        _refillSamplesNeeded = 0; // fresh segment: the start gate governs
                    }
                    if (_active != null)
                        SegmentStarted?.Invoke(_active);
                }
                adapted = _activeAdapted;
                active = _active;
            }

            if (active == null || adapted == null)
                break; // nothing to play — silence-fill below

            // Pre-buffer gate: hold a fresh segment in silence until enough is
            // staged for glitch-free rendering (see ReadyToRender).
            if (!active.ReadyToRender(active.SampleRate * active.Channels * _startThresholdMs / 1000))
                break;

            // Underrun hysteresis: after a mid-track 0-read, keep padding until
            // the segment re-buffers ~UnderrunRefillMs (EOS/Abandon play out).
            if (_refillSamplesNeeded > 0)
            {
                if (active.BufferedSamples < _refillSamplesNeeded &&
                    !active.EndOfStream && !active.Abandoned)
                    break;
                _refillSamplesNeeded = 0;
            }

            var n = adapted.Read(buffer, offset + written, count - written);
            if (n > 0)
            {
                // Fade in when audio resumes after audible silence (cold start,
                // post-seek gate, underrun recovery) to mask decoder warm-up
                // garble. A gapless seam is never preceded by silence (streak 0),
                // so it stays bit-exact.
                if (_startFadeSamples > 0 && _silentSamples >= _fadeArmSamples)
                    _fadeRemaining = _startFadeSamples;
                _silentSamples = 0;
                if (_fadeRemaining > 0)
                    ApplyFadeIn(buffer, offset + written, n);
                written += n;
                continue;
            }

            // 0 from the adapter: either the segment truly finished (advance on
            // the next loop pass) or a mid-track underrun (VLC hasn't delivered
            // yet) — pad THIS call with silence rather than busy-spinning the
            // render thread, but do not advance past an unfinished segment.
            if (!active.IsFinished)
            {
                _refillSamplesNeeded = WaveFormat.SampleRate * WaveFormat.Channels * UnderrunRefillMs / 1000;
                break;
            }
        }

        var padded = count - written;
        if (padded > 0)
        {
            Array.Clear(buffer, offset + written, padded);
            _silentSamples = (int)Math.Min((long)_silentSamples + padded, int.MaxValue / 2);
        }
        return count;
    }

    // Linear per-sample ramp 0 → 1 across _startFadeSamples, resuming across
    // Read calls via _fadeRemaining. Render thread only.
    private void ApplyFadeIn(float[] buffer, int offset, int n)
    {
        var total = _startFadeSamples;
        for (var i = 0; i < n && _fadeRemaining > 0; i++, _fadeRemaining--)
        {
            buffer[offset + i] *= (float)(total - _fadeRemaining) / total;
        }
    }

    // Segment (native rate/ch) → sink format. The WDL resampler tail (a few
    // samples) is flushed naturally: it keeps returning data after the ring
    // empties until its internal buffer drains, and only then reports 0.
    private ISampleProvider Adapt(GaplessTrackSegment segment)
    {
        ISampleProvider source = new SegmentSampleProvider(segment);
        if (segment.Channels == 1 && WaveFormat.Channels == 2)
            source = new MonoToStereoSampleProvider(source);
        else if (segment.Channels == 2 && WaveFormat.Channels == 1)
            source = new StereoToMonoSampleProvider(source);
        if (source.WaveFormat.SampleRate != WaveFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, WaveFormat.SampleRate);
        return source;
    }

    private sealed class SegmentSampleProvider : ISampleProvider
    {
        private readonly GaplessTrackSegment _segment;
        public WaveFormat WaveFormat { get; }

        public SegmentSampleProvider(GaplessTrackSegment segment)
        {
            _segment = segment;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(segment.SampleRate, segment.Channels);
        }

        public int Read(float[] buffer, int offset, int count) =>
            _segment.Read(buffer, offset, count);
    }
}
