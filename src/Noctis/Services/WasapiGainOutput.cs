using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Noctis.Services;

/// <summary>
/// Windows-only audio sink that delivers click-free, real-time volume.
///
/// Why this exists: both of LibVLC's gain paths (its float_mixer volume and the
/// Windows session volume via ISimpleAudioVolume) apply gain as a STEPPED block
/// multiply at audio-buffer boundaries — not interpolated per sample. A fast
/// slider drag therefore changes the gain faster than those steps can stay
/// inaudible, producing the crackle/zipper. Slowing the ramp removes the crackle
/// but makes the slider laggy. There is no value of "tick/step" that is both
/// instant and silent, because the artifact is the step discontinuity itself.
///
/// The fix is to apply volume as a PER-SAMPLE interpolated gain inside the audio
/// stream (the way Spotify/Apple Music do). LibVLC's only mechanism for that is
/// <c>SetAudioCallbacks</c>, which hands us the decoded PCM and disables LibVLC's
/// own output entirely — so we render the PCM ourselves via WASAPI and apply the
/// gain in the render path. EQ and ReplayGain are applied by LibVLC upstream of
/// the callback, so the PCM we receive already includes them; we apply only the
/// user's volume.
///
/// Two entry points:
///   - <see cref="TryCreate"/> — shared mode at the device mix rate. Used by the
///     experimental NOCTIS_WASAPI=1 volume path, and as the graceful fallback
///     when an exclusive open fails (device busy / format unsupported).
///   - <see cref="TryCreateExclusive"/> — WASAPI exclusive mode at the SOURCE
///     sample rate for bit-perfect output (Settings > Audio > Exclusive Mode).
///     Negotiates the device's native bit depth (24 → 16 → float32).
///
/// Input is S16N PCM: VLC 3.x's amem output delivers ONLY 16-bit native-endian
/// samples — the dynamic setup callback hard-rejects other formats (strcmp in
/// amem.c) and the fixed-format API's format string is ignored (the
/// "/* TODO: amem-format */" branch reads only the rate/channels vars). At
/// unity gain the int16→float→int16/24 path is bit-exact, so 16-bit sources
/// stay bit-perfect at the source rate; >16-bit content is truncated to
/// 16-bit upstream by VLC — a hard LibVLC 3.x limitation.
///
/// Both return null on non-Windows or device-init failure so the caller can
/// fall back to another output path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiGainOutput : IDisposable
{
    private const int BytesPerSample = 2; // S16N from LibVLC's amem output

    // AUDCLNT_E_DEVICE_IN_USE: another client holds the endpoint exclusively.
    private const int HrDeviceInUse = unchecked((int)0x8889000A);

    // Render format. Shared mode matches the default device's mix format (sample
    // rate) so WASAPI accepts it without a format-unsupported failure; exclusive
    // mode uses the source rate of the track being started. LibVLC is told this
    // exact rate/channels and downmixes/resamples to it if needed.
    public int SampleRate { get; }
    public int Channels { get; }
    public bool IsExclusive { get; }
    /// <summary>Bit depth handed to the device (16/24 PCM or 32 float).</summary>
    public int BitsPerSample { get; }

    private readonly BufferedWaveProvider _buffer;
    private readonly GainSampleProvider _gain;
    private readonly WasapiOut _out;
    private volatile bool _disposed;
    private long _bytesWritten;
    private int _writeCount;

    /// <summary>
    /// Set when the render thread stops unexpectedly (endpoint removed, device switched,
    /// driver fault). Once faulted the sink drops writes instead of spinning, and
    /// <see cref="Faulted"/> lets the player tear it down and fall back.
    /// </summary>
    private volatile bool _faulted;

    /// <summary>True once the output device has gone away. The sink is unusable.</summary>
    public bool IsFaulted => _faulted;

    /// <summary>
    /// Raised on NAudio's thread when playback stops for any reason other than a normal
    /// Dispose — i.e. the endpoint is gone. The player uses this to drop exclusive mode
    /// and re-open on the current default device instead of going permanently silent.
    /// </summary>
    public event Action<WasapiGainOutput>? Faulted;

    private void HookPlaybackStopped()
    {
        _out.PlaybackStopped += (_, e) =>
        {
            if (_disposed) return;
            _faulted = true;
            Diag($"PlaybackStopped (device lost?): {e.Exception?.Message ?? "no exception"}");
            // Raised on NAudio's render thread: logged off it. Once per sink by nature.
            DebugLogger.LogOffThread(DebugLogger.Category.Playback, DebugLogger.Level.Warn, "Exclusive.PlaybackStopped",
                $"exclusive={IsExclusive}, {(e.Exception == null ? "no exception" : $"{e.Exception.GetType().Name}: {e.Exception.Message}")}");
            try { Faulted?.Invoke(this); } catch { /* never throw on NAudio's thread */ }
        };
    }

    public static WasapiGainOutput? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        // Silent test mode (NOCTIS_AOUT=dummy) opens no device: callers take their fallbacks.
        if (NullWavePlayer.SilentMode) return null;
        try { return new WasapiGainOutput(); }
        catch (Exception ex)
        {
            Diag($"TryCreate FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Open the default render device in WASAPI exclusive mode at the given
    /// source rate. Returns null with a human-readable reason on failure
    /// (device held exclusively elsewhere, rate/format not supported, ...).
    /// </summary>
    public static WasapiGainOutput? TryCreateExclusive(int sampleRate, int channels, out string? failureReason)
    {
        failureReason = null;
        if (!OperatingSystem.IsWindows())
        {
            failureReason = "not supported on this platform";
            return null;
        }
        if (NullWavePlayer.SilentMode)
        {
            failureReason = "silent test mode (NOCTIS_AOUT=dummy)";
            return null;
        }
        try { return new WasapiGainOutput(sampleRate, channels); }
        catch (Exception ex)
        {
            failureReason = (ex as System.Runtime.InteropServices.COMException)?.HResult == HrDeviceInUse
                ? "audio device is in use by another app"
                : $"device rejected {sampleRate / 1000.0:0.#} kHz exclusive output";
            Diag($"TryCreateExclusive FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private WasapiGainOutput()
    {
        Diag("=== WasapiGainOutput init (shared) ===");

        int rate = 48000, channels = 2;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
            Diag($"device mix format: {mix.Encoding} {mix.SampleRate}Hz {mix.Channels}ch {mix.BitsPerSample}bit");
            rate = mix.SampleRate;
            channels = mix.Channels >= 2 ? 2 : 1; // render stereo (or mono); LibVLC downmixes
        }
        catch (Exception ex)
        {
            Diag($"mix-format query failed, defaulting 48k/2ch: {ex.Message}");
        }

        SampleRate = rate;
        Channels = channels;
        IsExclusive = false;
        BitsPerSample = 32;

        (_buffer, _gain) = CreateInputChain(SampleRate, Channels);
        _out = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        // Post-gain beat tap for the flowing-artwork lyrics background (see BeatMeter).
        _out.Init(new BeatTapProvider(_gain, 50));
        HookPlaybackStopped();
        _out.Play();
        Diag($"input S16N {SampleRate}Hz {Channels}ch | WasapiOut state={_out.PlaybackState}");
    }

    private WasapiGainOutput(int sampleRate, int channels)
    {
        Diag($"=== WasapiGainOutput init (exclusive, {sampleRate}Hz {channels}ch) ===");

        SampleRate = sampleRate;
        Channels = channels;
        IsExclusive = true;

        (_buffer, _gain) = CreateInputChain(SampleRate, Channels);

        // Exclusive mode requires a device-native format. Prefer 24-bit, then
        // 16-bit, then float32; first Init that the driver accepts wins. The
        // gain stage stays in float upstream — at unity gain the S16 input maps
        // bit-exactly onto the 16/24-bit device formats.
        Exception? lastError = null;
        foreach (var bits in new[] { 24, 16, 32 })
        {
            // Post-gain beat tap for the flowing-artwork lyrics background (see BeatMeter).
            var tapped = new BeatTapProvider(_gain, 100);
            IWaveProvider rendered = bits switch
            {
                24 => new SampleToWaveProvider24(tapped),
                16 => new SampleToWaveProvider16(tapped),
                _ => new SampleToWaveProvider(tapped),
            };

            var attempt = new WasapiOut(AudioClientShareMode.Exclusive, useEventSync: true, latency: 100);
            try
            {
                attempt.Init(rendered);
                _out = attempt;
                HookPlaybackStopped();
                attempt.Play();
                BitsPerSample = bits;
                Diag($"exclusive open OK: {bits}-bit {SampleRate}Hz {Channels}ch | state={attempt.PlaybackState}");
                return;
            }
            catch (Exception ex)
            {
                Diag($"exclusive {bits}-bit init failed: {ex.GetType().Name}: {ex.Message}");
                try { attempt.Dispose(); } catch { }
                lastError = ex;
                // Device held exclusively elsewhere — no format will succeed.
                if ((ex as System.Runtime.InteropServices.COMException)?.HResult == HrDeviceInUse)
                    break;
            }
        }

        throw lastError ?? new InvalidOperationException("exclusive WASAPI init failed");
    }

    private static (BufferedWaveProvider buffer, GainSampleProvider gain) CreateInputChain(int sampleRate, int channels)
    {
        // 16-bit PCM in: everything LibVLC 3.x's amem hands us is S16N.
        var format = new WaveFormat(sampleRate, 16, channels);
        var buffer = new BufferedWaveProvider(format)
        {
            // Bounded queue between LibVLC's decode thread and the WASAPI render
            // thread. Large enough to ride out GC/disk jitter, small enough that
            // seek/track-change latency stays low. Write() applies backpressure
            // rather than overflowing.
            BufferDuration = TimeSpan.FromMilliseconds(1000),
            DiscardOnBufferOverflow = false,
            // Short reads when dry, so the gain stage sees an underrun/flush and
            // declicks it; the gain stage pads to full buffers so WasapiOut keeps running.
            ReadFully = false,
        };
        return (buffer, new GainSampleProvider(buffer.ToSampleProvider(), channels, sampleRate));
    }

    /// <summary>
    /// Set the target amplitude (0..1). Applied per-sample at the output with a
    /// short interpolation, so it is click-free at any change speed and audible
    /// within roughly one render quantum (~10ms) — i.e. real-time.
    /// </summary>
    public void SetGainTarget(float target) => _gain.SetTarget(target);

    /// <summary>
    /// Enqueue interleaved S16N PCM from LibVLC's audio play callback. Blocks
    /// briefly when the buffer is full to pace LibVLC's decoder and bound latency.
    /// </summary>
    public void Write(byte[] data, int count)
    {
        if (_disposed || _faulted) return;

        // Backpressure: wait for the render thread to drain space instead of
        // throwing on overflow. Capped so teardown can't deadlock the audio thread.
        //
        // The fault check matters: if the endpoint goes away (USB DAC unplugged,
        // Bluetooth disconnect, default device switched) NAudio's render thread stops
        // and the buffer never drains again. Without it, every LibVLC audio callback
        // then paid the full 2s spin — permanent silence with the transport still
        // running, and _player.Stop() (which joins the aout thread) blocked behind the
        // stalled callback while holding _playbackLock.
        var deadline = Environment.TickCount64 + 2000;
        while (!_disposed && !_faulted &&
               _buffer.BufferLength - _buffer.BufferedBytes < count &&
               Environment.TickCount64 < deadline)
        {
            Thread.Sleep(2);
        }

        if (_disposed || _faulted) return;
        try
        {
            _buffer.AddSamples(data, 0, count);
            _bytesWritten += count;
            // Log the first few writes and then periodically, so we can confirm
            // PCM is actually flowing and WasapiOut is rendering it.
            var n = ++_writeCount;
            if (DiagEnabled && (n <= 3 || n % 200 == 0))
                Diag($"write #{n}: {count}B | buffered={_buffer.BufferedBytes}B | state={_out.PlaybackState} | total={_bytesWritten}B");
        }
        catch (Exception ex)
        {
            Diag($"AddSamples threw: {ex.GetType().Name}: {ex.Message}");
            NoteWriteDropped(count, ex);
        }
    }

    private int _writeDrops;
    private long _lastWriteDropLogTick;

    // A block the buffer refused (still full after the 2 s backpressure wait): that PCM is
    // lost. Mirrored from the opt-in Diag file into DebugLogger; Write runs on libvlc's
    // audio thread, so the line goes off-thread, at most one per 250 ms.
    private void NoteWriteDropped(int bytes, Exception ex)
    {
        var drops = Interlocked.Increment(ref _writeDrops);
        if (!DebugLogger.IsEnabled) return;
        var now = Environment.TickCount64;
        if (now - _lastWriteDropLogTick < 250) return;
        _lastWriteDropLogTick = now;
        DebugLogger.LogOffThread(DebugLogger.Category.Playback, DebugLogger.Level.Warn, "Exclusive.WriteDropped",
            $"exclusive={IsExclusive}, bytes={bytes}, drops={drops}, {ex.GetType().Name}");
    }

    // Pause/Resume park the render chain instead of pausing WasapiOut. NAudio's
    // WasapiOut.Pause() only stops FILLING the stream — IAudioClient keeps
    // running. Shared mode then mixes silence, but in exclusive event-driven
    // mode the hardware keeps cycling the client-owned DMA buffer that nobody
    // refills, replaying the last ~100ms of stale PCM as CONSTANT STATIC for
    // the whole pause; resume then re-engaged the fill loop out of phase over
    // stale buffers (crackles/stutters). Parking keeps the stream fed with
    // freshly written silence — no state change, no starvation, and resume
    // continues click-free at the exact held sample.
    public void Pause()
    {
        if (_disposed) return;
        _gain.Park();
    }

    public void Resume()
    {
        if (_disposed) return;
        _gain.Unpark();
    }

    public void Flush()
    {
        if (_disposed) return;
        // Seek/stop cut: the gain stage ramps the last emitted frame down and
        // fades the refill in (see GainSampleProvider.Cut).
        try { _gain.Cut(_buffer.ClearBuffer); } catch { }
    }

    public void Drain()
    {
        if (_disposed) return;
        var deadline = Environment.TickCount64 + 1500;
        while (!_disposed && _buffer.BufferedBytes > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(5);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
    }

    // ── Diagnostics ─────────────────────────────────────────────────
    // Appends to noctis_wasapi.log on the Desktop (or user profile) so the silent-
    // path failure can be read directly. Best-effort; never throws.
    //
    // OPT-IN ONLY (NOCTIS_WASAPI_LOG=1), matching NOCTIS_VLC_LOG. This used to be
    // unconditional and was called from GainSampleProvider.Read — the WASAPI render
    // callback — and from LibVLC's audio thread, so every user who enabled Exclusive
    // Mode got an unbounded log dropped on their Desktop, and any disk stall (AV scan,
    // OneDrive-redirected Desktop, spinning disk) blocked the render thread inside its
    // buffer-fill deadline. That underrun is exactly what Exclusive Mode exists to avoid.
    private static readonly object _diagGate = new();
    private static string? _diagPath;

    /// <summary>True when NOCTIS_WASAPI_LOG=1. Lets hot paths skip building the message.</summary>
    internal static bool DiagEnabled { get; } =
        Environment.GetEnvironmentVariable("NOCTIS_WASAPI_LOG") == "1";

    internal static void Diag(string msg)
    {
        if (!DiagEnabled) return;
        try
        {
            if (_diagPath == null)
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                _diagPath = Path.Combine(dir, "noctis_wasapi.log");
            }
            lock (_diagGate)
                File.AppendAllText(_diagPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { /* diagnostics must never break playback */ }
    }

    /// <summary>
    /// Applies a per-sample interpolated gain in the render path. Each frame nudges
    /// the applied gain toward the target by a fixed slew, so even an instant
    /// 0→1 target jump is rendered as a continuous ~15ms amplitude ramp — no
    /// waveform discontinuity, hence no click, regardless of how fast the slider
    /// moves. Because it runs at the output (not at decode time), the change is
    /// heard within one render quantum, not after the whole queued buffer.
    ///
    /// Also owns pause (<see cref="Park"/>/<see cref="Unpark"/>): parked, it
    /// fades to zero on the same slew, then emits full buffers of true silence
    /// WITHOUT consuming the source, holding the pre-pause tail for a
    /// sample-exact resume. The stream itself never pauses — see the note on
    /// <see cref="WasapiGainOutput.Pause"/> for the exclusive-mode DMA-loop
    /// static this design exists to avoid.
    ///
    /// Also declicks cuts (<see cref="Cut"/>) and underruns (the source
    /// short-reads): a seek/stop flush or a dry queue stops LIVE audio inside a
    /// stream that never stops, and exclusive mode has no OS mixer to soften the
    /// edge. The last emitted frame is ramped to silence and whatever follows is
    /// faded in (~5ms each, the GaplessSpliceProvider pattern). Uninterrupted
    /// audio never ramps, so steady-state output stays bit-exact. Internal for tests.
    /// </summary>
    internal sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _channels;
        private float _current = 1f;
        private volatile float _target = 1f;
        private volatile bool _parked;
        private float _parkGain = 1f; // render-thread only, slews toward _parked ? 0 : 1
        private readonly float _step; // per-frame gain step to reach target in ~15ms
        private long _readCount;
        private long _setCount;

        // Cut/underrun declick. The gate makes a Cut (source clear + mark) atomic
        // against the render read's mark check + source read; the rest is
        // render-thread only.
        private readonly object _readGate = new();
        private bool _cutPending;               // guarded by _readGate
        private readonly int _rampFrames;       // ~5ms
        private readonly float[] _lastFrame;    // last emitted (post-gain) frame
        private int _declickLeft;               // tail-ramp frames still to emit
        private int _fadeLeft;                  // fade-in frames still to apply
        private bool _fadePending;              // fade in the next source audio

        public GainSampleProvider(ISampleProvider src, int channels, int sampleRate)
        {
            _src = src;
            _channels = channels;
            _step = 1f / (sampleRate * 0.015f);
            _rampFrames = Math.Max(1, sampleRate / 200);
            _lastFrame = new float[channels];
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public void SetTarget(float target)
        {
            target = Math.Clamp(target, 0f, 1f);
            _target = target;
            if (++_setCount <= 8 || _setCount % 100 == 0)
                Diag($"SetTarget #{_setCount}: {target:F4}");
        }

        public void Park() => _parked = true;

        public void Unpark() => _parked = false;

        /// <summary>
        /// A seek/stop flush: <paramref name="clearSource"/> drops the queued
        /// source and the cut is marked under the read gate, so a render read
        /// sees either the pre-cut queue or the marked cut — never post-cut audio
        /// butt-joined to the pre-cut tail.
        /// </summary>
        public void Cut(Action clearSource)
        {
            lock (_readGate)
            {
                _cutPending = true;
                clearSource();
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var parked = _parked;
            var park = _parkGain;

            // Fully parked: full buffers of silence, source untouched. The
            // explicit loop (not Array.Clear) matters — the buffer can be a
            // WaveBuffer-punned float[] whose Length lies about its element count.
            if (parked && park == 0f && _declickLeft == 0)
            {
                for (var i = 0; i < count; i++) buffer[offset + i] = 0f;
                return count;
            }

            var written = 0;
            int toConsume, read;
            lock (_readGate)
            {
                // A cut since the last read: what is queued now is unrelated to
                // the last emitted frame.
                if (_cutPending)
                {
                    _cutPending = false;
                    BeginDeclick();
                }
                // Play a pending tail ramp out before any fresh audio.
                if (_declickLeft > 0)
                    written = EmitDeclick(buffer, offset, count);

                // Fading out: consume only the ~15ms the ramp still needs, so the
                // rest of the pre-pause tail stays queued for resume instead of
                // being eaten at zero gain.
                toConsume = count - written;
                if (parked)
                {
                    var rampFramesLeft = (int)MathF.Ceiling(park / _step);
                    toConsume = Math.Min(toConsume, rampFramesLeft * _channels);
                }

                read = toConsume > 0 ? _src.Read(buffer, offset + written, toConsume) : 0;
            }
            var target = _target;
            var cur = _current;
            var parkTarget = parked ? 0f : 1f;
            var step = _step;
            var peak = 0f;
            if (read > 0 && _fadePending)
            {
                _fadePending = false;
                _fadeLeft = _rampFrames;
            }
            var fadeLeft = _fadeLeft;

            for (var i = 0; i + _channels <= read; i += _channels)
            {
                if (cur < target) cur = Math.Min(target, cur + step);
                else if (cur > target) cur = Math.Max(target, cur - step);
                if (park < parkTarget) park = Math.Min(parkTarget, park + step);
                else if (park > parkTarget) park = Math.Max(parkTarget, park - step);
                // Fade-in after a cut/underrun, 0 → 1; exactly 1 otherwise.
                var fade = 1f;
                if (fadeLeft > 0)
                    fade = (float)(_rampFrames - fadeLeft--) / _rampFrames;

                for (var ch = 0; ch < _channels; ch++)
                {
                    var idx = offset + written + i + ch;
                    var s = buffer[idx];
                    var a = s < 0 ? -s : s;
                    if (a > peak) peak = a;
                    buffer[idx] = s * cur * park * fade;
                }
            }

            _current = cur;
            _parkGain = park;
            _fadeLeft = fadeLeft;
            if (read >= _channels)
                for (var ch = 0; ch < _channels; ch++)
                    _lastFrame[ch] = buffer[offset + written + read - _channels + ch];
            written += read;

            // The source ran dry (mid-track underrun, or drained after a stop):
            // ramp the tail down instead of stepping to the pad's zeros.
            if (read < toConsume)
                BeginDeclick();

            // Pad the rest with the tail ramp then silence, and claim the full
            // count, so WasapiOut's render loop keeps running — a starved
            // exclusive stream loops its stale DMA buffer.
            if (written < count && _declickLeft > 0)
                written += EmitDeclick(buffer, offset + written, count - written);
            for (var i = written; i < count; i++) buffer[offset + i] = 0f;
            // Diag() is a no-op unless NOCTIS_WASAPI_LOG=1, but keep the interpolation
            // itself off the render thread's hot path when logging is off.
            if (DiagEnabled && ++_readCount % 400 == 1)
                Diag($"Read #{_readCount}: frames={read / _channels} srcPeak={peak:F4} gain={cur:F4} target={target:F4}");
            return count;
        }

        // Arms the junction: ramp the last emitted frame down (unless a ramp is
        // already running or the output is already silent) and fade in the
        // next source audio.
        private void BeginDeclick()
        {
            _fadePending = true;
            if (_declickLeft > 0) return;
            for (var ch = 0; ch < _channels; ch++)
            {
                if (_lastFrame[ch] != 0f)
                {
                    _declickLeft = _rampFrames;
                    return;
                }
            }
        }

        // Writes the tail ramp (last emitted frame → 0) into at most maxSamples,
        // resuming across reads; returns the samples written. Element stores
        // only — the buffer can be the byte[]-punned render buffer.
        private int EmitDeclick(float[] buffer, int pos, int maxSamples)
        {
            var frames = Math.Min(_declickLeft, maxSamples / _channels);
            for (var f = 0; f < frames; f++, _declickLeft--)
            {
                var g = (float)_declickLeft / _rampFrames;
                for (var ch = 0; ch < _channels; ch++)
                    buffer[pos + f * _channels + ch] = _lastFrame[ch] * g;
            }
            // The emitted tail is now silent: a later cut must not ramp from it again.
            if (_declickLeft == 0)
                for (var ch = 0; ch < _channels; ch++)
                    _lastFrame[ch] = 0f;
            return frames * _channels;
        }
    }
}
