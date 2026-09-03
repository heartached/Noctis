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
    private bool _cutPending;        // a Flush cut live audio; renderer must declick the junction
    private bool _flushRearmed;      // gate re-armed by a seek flush (warm decoder), not a cold start
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
                _flushRearmed = false;
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
            _cutPending = true;
            _flushRearmed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>True while the pre-buffer gate is re-armed by a Flush (seek)
    /// rather than a fresh segment start: the decoder is already warm, so the
    /// renderer may use a much shorter refill threshold than a cold start.</summary>
    public bool GateRearmedByFlush
    {
        get { lock (_gate) return _flushRearmed; }
    }

    /// <summary>
    /// One-shot: true if a <see cref="Flush"/> cut this segment since the last
    /// render read. The ring can refill past the pre-buffer gate BEFORE the next
    /// read arrives (likely at large device buffers), in which case the renderer
    /// sees no silence at all — the cut is only observable through this flag.
    /// </summary>
    public bool ConsumeCut()
    {
        lock (_gate)
        {
            var cut = _cutPending;
            _cutPending = false;
            return cut;
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
/// Render-thread replay detector (diagnostic, NOCTIS_ENGINE_TAP-gated): hashes
/// 96-sample windows (stride 48) of the observed stream and warns when a
/// bit-exact non-silent window recurs within ~100ms — the field buzz signature
/// (a ~10ms fragment of already-played audio rendered again at seeks/skips).
/// Placed at two depths (raw ring output vs provider output) to bisect the
/// layer that introduces the duplication.
/// </summary>
internal sealed class ReplayDetector
{
    private const int WindowSamples = 96;
    private const int Stride = 48;
    private const int MaxLagSamples = 9600; // 100ms @48k mono-sample count
    private readonly string _label;
    private readonly long[] _hashes = new long[512];
    private readonly long[] _positions = new long[512];
    private int _next;
    private long _pos;
    private long _lastLogTick;

    private ReplayDetector(string label) => _label = label;

    public static ReplayDetector? CreateIfEnabled(string label) =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOCTIS_ENGINE_TAP"))
            ? null : new ReplayDetector(label);

    public void Observe(float[] buffer, int offset, int n)
    {
        for (var i = 0; i + WindowSamples <= n; i += Stride)
        {
            long h = 1469598103934665603;
            var silent = true;
            for (var j = 0; j < WindowSamples; j++)
            {
                var v = buffer[offset + i + j];
                if (v != 0f) silent = false;
                h = (h ^ BitConverter.SingleToInt32Bits(v)) * 1099511628211;
            }
            var winPos = _pos + i;
            if (!silent)
            {
                for (var k = 0; k < _hashes.Length; k++)
                {
                    if (_hashes[k] == h && winPos - _positions[k] is > 0 and <= MaxLagSamples)
                    {
                        var now = Environment.TickCount64;
                        if (now - _lastLogTick > 250)
                        {
                            _lastLogTick = now;
                            DebugLogger.Warn(DebugLogger.Category.Playback, $"GaplessEngine.Replay{_label}",
                                $"lagMs={(winPos - _positions[k]) / 96.0:F1}, posSamples={winPos}");
                        }
                        break;
                    }
                }
                _hashes[_next] = h;
                _positions[_next] = winPos;
                _next = (_next + 1) % _hashes.Length;
            }
        }
        _pos += n;
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
    // Post-seek gate: the flush re-arms the pre-buffer, but the decoder is warm
    // — 50ms of refill renders glitch-free where a cold start needs the full
    // startThresholdMs. Anything longer is an audible pause per timeline click.
    private const int FlushRearmThresholdMs = 50;
    // Silence must run at least this long before the next audio fades in, so the
    // resampler's few-sample seam zeros can never arm a fade at a gapless splice.
    private const int FadeArmMs = 5;

    private readonly int _startFadeSamples;
    private readonly int _fadeArmSamples;
    private int _silentSamples;        // contiguous silence emitted so far (render thread only)
    private int _fadeRemaining;        // samples left of an in-progress fade-in
    private int _refillSamplesNeeded;  // >0 while the underrun hold is armed
    private readonly float[] _lastFrame = new float[2]; // last emitted frame, for the cut declick
    private int _declickRemaining;     // samples left of an in-progress cut ramp-to-zero
    private bool _cutFadePending;      // a cut junction is due a fade-in regardless of silence streak (render thread only)
    private volatile bool _pendingCutSignal; // cut raised off the render thread (Clear / abandon-swap)
    private readonly ReplayDetector? _ringDetector = ReplayDetector.CreateIfEnabled("Ring"); // raw adapter output, pre-fade
    private readonly bool _readTrace = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOCTIS_ENGINE_TAP"));
    private long _lastTraceTick;

    // Mixed crossfade (transition-mode advance under the engine): the outgoing
    // segment keeps rendering as a fading tail ADDED to the new active segment
    // for _fadeTotalSamples, then is abandoned. Without this the engine could
    // only butt-splice or cut, so an early transition advance cut the last
    // fade-length seconds of every playlist track dead.
    private GaplessTrackSegment? _fading;
    private ISampleProvider? _fadingAdapted;
    private Noctis.Models.AutoMixFadeCurve _fadeCurve;
    private int _fadeTotalSamples;
    private int _fadeElapsedSamples;   // render thread only
    private volatile bool _crossfadeArmed; // BeginCrossfade swapped the active off the render thread
    private float[]? _fadeScratch;

    // Playback speed (podcast/audiobook island): a WSOLA stretch is the LAST
    // adapter stage, pulling media frames at the rate, so the segment's
    // PositionMs stays media time and LibVLC keeps decoding at 1×. 1.0 is a
    // pass-through — the gapless seam stays bit-exact.
    private double _playbackRate = 1.0;

    /// <summary>Playback speed, 0.5–2.0; 1.0 = untouched.</summary>
    public double PlaybackRate
    {
        get => Volatile.Read(ref _playbackRate);
        set => Volatile.Write(ref _playbackRate, TempoStretchProvider.ClampRate(value));
    }

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
    /// <summary>
    /// Start a mixed crossfade from the active segment into the next queued one:
    /// the next segment becomes active NOW (position/bookkeeping follow it) while
    /// the outgoing keeps rendering as a fading tail mixed underneath for
    /// <paramref name="durationMs"/>, after which it is abandoned. False when
    /// nothing is active or nothing is staged — the caller then cuts as before.
    /// The outgoing decoder must keep feeding its segment for the fade length.
    /// </summary>
    public bool BeginCrossfade(int durationMs, Noctis.Models.AutoMixFadeCurve curve)
    {
        lock (_gate)
        {
            if (_active == null || _active.Abandoned || _pending.Count == 0)
                return false;
            // A fade still in flight: its tail is stale now — drop it.
            _fading?.Abandon();
            _fading = _active;
            _fadingAdapted = _activeAdapted;
            _active = _pending.Dequeue();
            _activeAdapted = Adapt(_active);
            _fadeCurve = curve;
            _fadeTotalSamples = Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels * Math.Clamp(durationMs, 1, 20000) / 1000);
            _fadeElapsedSamples = 0;
            _crossfadeArmed = true;
            return true;
        }
    }

    /// <summary>True while an outgoing tail is still being mixed underneath the active segment.</summary>
    public bool IsCrossfading { get { lock (_gate) return _fading != null; } }

    public void Clear()
    {
        lock (_gate)
        {
            if (_active != null)
                _pendingCutSignal = true; // live audio cut off the render thread
            _active?.Abandon();
            _active = null;
            _activeAdapted = null;
            _fading?.Abandon();
            _fading = null;
            _fadingAdapted = null;
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
        if (_crossfadeArmed)
        {
            // BeginCrossfade promoted the staged segment off the render thread:
            // a fresh active is governed by its own start gate, not a leftover
            // underrun hold, and the audible boundary is announced like a splice.
            _crossfadeArmed = false;
            _refillSamplesNeeded = 0;
            GaplessTrackSegment? promoted;
            lock (_gate) promoted = _active;
            if (promoted != null)
                SegmentStarted?.Invoke(promoted);
        }
        while (written < count)
        {
            ISampleProvider? adapted;
            GaplessTrackSegment? active;
            lock (_gate)
            {
                if (_active == null || _active.IsFinished || _active.Abandoned)
                {
                    // A track-change abandon cuts LIVE audio (a natural end-of-
                    // stream drain does not): the junction into whatever renders
                    // next must declick+fade exactly like a seek flush.
                    if (_active is { Abandoned: true })
                        _pendingCutSignal = true;
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

            // A cut (seek flush / track abandon / clear) may be followed by a
            // refill that lands BEFORE this read — then no silence is ever
            // rendered and the streak-based declick/fade below never engages,
            // butt-splicing unrelated waveforms (an audible click). The cut
            // event itself is the only reliable signal; consume it here, before
            // the gate, so the pad path inherits the armed ramp when it holds.
            var cutNow = active.ConsumeCut();
            if (_pendingCutSignal) { _pendingCutSignal = false; cutNow = true; }
            if (cutNow && _startFadeSamples > 0)
            {
                if (_silentSamples == 0)
                    _declickRemaining = _startFadeSamples; // ramp the live tail
                _cutFadePending = true;                    // and fade what follows
            }

            // Pre-buffer gate: hold a fresh segment in silence until enough is
            // staged for glitch-free rendering (see ReadyToRender). After a seek
            // flush the decoder is warm and delivery is already ramped — use a
            // short refill threshold there, or every timeline click pauses for
            // the full cold-start pre-buffer.
            var gateMs = active.GateRearmedByFlush ? FlushRearmThresholdMs : _startThresholdMs;
            if (!active.ReadyToRender(active.SampleRate * active.Channels * gateMs / 1000))
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

            // The junction ramp of a fast-refill cut: play the last live frame
            // down to zero BEFORE the first post-cut audio. (When the gate holds
            // instead, the silence pad below runs this same ramp.)
            if (_cutFadePending && _declickRemaining > 0)
            {
                var rampCh = WaveFormat.Channels;
                var run = Math.Min(_declickRemaining, count - written);
                for (var i = 0; i < run; i++)
                {
                    buffer[offset + written + i] =
                        _lastFrame[i % rampCh] * ((float)_declickRemaining / _startFadeSamples);
                    _declickRemaining--;
                }
                written += run;
                if (_declickRemaining == 0)
                {
                    // The emitted tail is now zero; a later silence pad must not
                    // ramp again from the stale pre-cut frame.
                    _lastFrame[0] = 0f;
                    _lastFrame[1] = 0f;
                }
                continue;
            }

            var n = adapted.Read(buffer, offset + written, count - written);
            if (n > 0)
            {
                _ringDetector?.Observe(buffer, offset + written, n);
                // Fade in when audio resumes after audible silence (cold start,
                // post-seek gate, underrun recovery) or across a cut junction, to
                // mask decoder warm-up garble and unrelated-waveform steps. A
                // NATURAL gapless seam is neither (no silence streak, no cut),
                // so it stays bit-exact.
                if (_startFadeSamples > 0 && (_silentSamples >= _fadeArmSamples || _cutFadePending))
                {
                    _fadeRemaining = _startFadeSamples;
                    _cutFadePending = false;
                }
                _silentSamples = 0;
                if (_fadeRemaining > 0)
                    ApplyFadeIn(buffer, offset + written, n);
                written += n;
                // Remember the tail frame for the cut declick; audio resuming
                // cancels any ramp still pending from a previous cut.
                _declickRemaining = 0;
                var tailCh = WaveFormat.Channels;
                if (n >= tailCh)
                    for (var c = 0; c < tailCh; c++)
                        _lastFrame[c] = buffer[offset + written - tailCh + c];
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
            var pos = offset + written;
            // NEVER Array.Clear here: the render buffer arrives as NAudio's
            // WaveBuffer pun (a byte[] reinterpreted as float[]), and Array.Clear
            // uses the array's RUNTIME type — it cleared `padded` BYTES (a quarter
            // of the region), leaving the rest playing stale device-buffer audio:
            // a ~10ms replay loop at every seek/track cut (the engine buzz).
            // Element stores go through the static float[] type and are safe.
            for (var i = 0; i < padded; i++)
                buffer[pos + i] = 0f;
            // Declick a hard cut: a seek flush / abandon stops LIVE audio inside
            // a stream that never stops, so there is no OS stream-stop ramp to
            // hide the edge — an instant step to zero is an audible click/buzz.
            // Ramp the pad from the last emitted frame down to silence instead.
            // Natural seams never pad, so true gapless is untouched.
            if (_startFadeSamples > 0 && _silentSamples == 0)
                _declickRemaining = _startFadeSamples;
            if (_declickRemaining > 0)
            {
                var ch = WaveFormat.Channels;
                var run = Math.Min(_declickRemaining, padded);
                for (var i = 0; i < run; i++)
                {
                    buffer[pos + i] = _lastFrame[i % ch] * ((float)_declickRemaining / _startFadeSamples);
                    _declickRemaining--;
                }
            }
            _silentSamples = (int)Math.Min((long)_silentSamples + padded, int.MaxValue / 2);
        }
        MixFadingTail(buffer, offset, count);
        if (_readTrace && Environment.TickCount64 - _lastTraceTick > 250)
        {
            _lastTraceTick = Environment.TickCount64;
            DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.ReadTrace",
                $"offset={offset}, count={count}, written={written}, padded={padded}, bufLen={buffer.Length}, declick={_declickRemaining}, fade={_fadeRemaining}, silent={_silentSamples}, active={(_active != null ? 1 : 0)}");
        }
        return count;
    }

    // Crossfade render: the buffer holds the new active segment's audio (or
    // silence) for this read; scale it by the fade-in factor and add the
    // outgoing tail scaled by the fade-out factor, sample-locked so both
    // sides advance together. The tail is dropped when the fade completes or
    // it runs dry (EOS drained). Render thread only.
    private void MixFadingTail(float[] buffer, int offset, int count)
    {
        GaplessTrackSegment? fading;
        ISampleProvider? adapted;
        lock (_gate)
        {
            fading = _fading;
            adapted = _fadingAdapted;
        }
        if (fading == null || adapted == null)
            return;

        var scratch = _fadeScratch;
        if (scratch == null || scratch.Length < count)
            _fadeScratch = scratch = new float[Math.Max(count, 4096)];
        var got = 0;
        if (!fading.Abandoned)
        {
            while (got < count)
            {
                var n = adapted.Read(scratch, got, count - got);
                if (n <= 0) break; // underrun or drained: the rest of the tail is silence
                got += n;
            }
        }
        for (var i = got; i < count; i++)
            scratch[i] = 0f;

        var total = _fadeTotalSamples;
        var elapsed = _fadeElapsedSamples;
        var ch = WaveFormat.Channels;
        for (var i = 0; i < count; i += ch)
        {
            var progress = Math.Min(1.0, (elapsed + i) / (double)total);
            var (outGain, inGain) = AutoMixFadeMath.GetFadeFactors(progress, _fadeCurve);
            var end = Math.Min(count, i + ch);
            for (var c = i; c < end; c++)
                buffer[offset + c] = (float)(buffer[offset + c] * inGain + scratch[c] * outGain);
        }
        _fadeElapsedSamples = (int)Math.Min((long)elapsed + count, int.MaxValue / 2);

        if (_fadeElapsedSamples >= total || fading.IsFinished || fading.Abandoned)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_fading, fading))
                {
                    _fading = null;
                    _fadingAdapted = null;
                }
            }
            fading.Abandon();
        }
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
        source = new TempoStretchProvider(source, () => Volatile.Read(ref _playbackRate));
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
