using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Noctis.Services;

// Persistent shared-mode WASAPI stream for the true-gapless splice engine
// (NOCTIS_GAPLESS_ENGINE=1). One WasapiOut opened at the device mix format
// runs for the player's lifetime; GaplessSpliceProvider feeds it per-track
// segments rendered back-to-back, so the track boundary is crossed inside a
// single render read — zero inserted samples. The stream lives in the process
// audio session, so the existing WindowsSessionVolume machinery keeps owning
// the user's volume/mute unchanged.
//
// The stream does NOT survive its endpoint: unplugging the device (or moving
// the app to another output in Windows' sound panel) kills WasapiOut's render
// thread with AUDCLNT_E_DEVICE_INVALIDATED and nothing restarts it — VLC keeps
// decoding into a sink nobody drains (field log: endless "buffer too late" +
// PtsGap after KeepAlive.DeviceChanged, silence until app restart). The sink
// therefore self-heals: PlaybackStopped-with-error and a default-device watch
// both rebuild the output on the current default endpoint, reusing the same
// provider chain (NAudio inserts a DMO resampler when the new device's mix
// format differs, so the VLC-facing format stays fixed).
public sealed class GaplessSink : IDisposable
{
    private readonly object _gate = new();
    private WasapiOut _out;
    private string? _deviceId; // endpoint the current output was opened against
    private readonly WaveFileWriter? _tap; // NOCTIS_ENGINE_TAP diagnostic capture
    private readonly ISampleProvider _renderSource;
    private readonly MuteGateProvider _muteGate;

    /// <summary>User mute, applied post-buffer in the render chain (instant, click-free).
    /// Survives output rebuilds: the gate is upstream of the WasapiOut that gets replaced.</summary>
    public bool IsMuted
    {
        get => _muteGate.IsMuted;
        set => _muteGate.IsMuted = value;
    }
    private readonly StallProbe _probe;
    private readonly Timer _deviceWatch;
    private volatile bool _desiredPlaying = true;
    private volatile bool _disposed;
    private int _rebuilding; // interlocked 0/1

    private const int DeviceCheckIntervalMs = 2000;

    public GaplessSpliceProvider Provider { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>
    /// Raised after the output was rebuilt on a (new) device. The fresh WASAPI
    /// stream registers a brand-new audio session at Windows' default level —
    /// the owner must re-assert the user volume or playback jumps to 100%.
    /// </summary>
    public event Action? Rebuilt;

    public static GaplessSink? TryCreate()
    {
        try
        {
            return new GaplessSink();
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.SinkFailed", $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private GaplessSink()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mix = device.AudioClient.MixFormat;
        SampleRate = Math.Clamp(mix.SampleRate, 8000, 384000);
        Channels = mix.Channels >= 2 ? 2 : 1;
        _deviceId = device.ID;
        // 200ms pre-buffer before a FRESH segment renders (kills the input-start
        // buzz of chopping ramping delivery against silence); the gapless splice
        // is unaffected — a staged segment holds seconds and passes instantly.
        // 5ms fade-in whenever audio resumes after silence (start, post-seek,
        // underrun recovery) masks decoder warm-up garble at segment heads; the
        // seam is never preceded by silence, so true gapless stays bit-exact.
        Provider = new GaplessSpliceProvider(SampleRate, Channels, startThresholdMs: 200, startFadeMs: 5);
        // NOCTIS_ENGINE_TAP=1 (or =<path>): capture exactly what the engine
        // renders to a WAV so glitches can be inspected sample-by-sample
        // instead of by ear. Diagnostic only — never breaks rendering.
        ISampleProvider renderSource = Provider;
        // Mute lives HERE, after the staged ring, not in LibVLC: VLC's mute is software
        // gain on the blocks it hands us, so with seconds staged ahead it was heard ~2 s
        // late (and unmute ~2 s late again, the ring being full of zeros by then). The
        // gate ramps over a few ms and is a pure pass-through when open.
        _muteGate = new MuteGateProvider(renderSource);
        renderSource = _muteGate;
        // Beat pulse for the flowing-artwork lyrics background: tapped post-mute so a
        // muted player shows a still backdrop, stamped with this sink's output depth
        // so the visual beat lands when the audible one does.
        renderSource = new BeatTapProvider(renderSource, OutputLatencyMs);
        var tap = Environment.GetEnvironmentVariable("NOCTIS_ENGINE_TAP");
        if (!string.IsNullOrEmpty(tap))
        {
            try
            {
                var tapPath = tap == "1" ? Path.Combine(Path.GetTempPath(), "noctis-engine-tap.wav") : tap;
                _tap = new WaveFileWriter(tapPath, Provider.WaveFormat);
                renderSource = new TapProvider(Provider, _tap);
                DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.TapOpen", tapPath);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.TapFailed", ex.Message);
            }
        }
        // Always-on stall probe: the render thread is MANAGED — a scheduler
        // stall freezes it, the device buffer underruns, and the glitch is
        // audible in the air but invisible to any PCM tap. Logging inter-read
        // gaps turns those into session-log lines with timestamps. It also
        // boosts the render thread on first read (see StallProbe).
        _probe = new StallProbe(renderSource);
        _renderSource = _probe;
        _out = CreateOutput();
        // Render immediately and forever: the provider always returns full
        // buffers (silence when idle), so the stream never stops between
        // tracks — the property true gapless depends on.
        _out.Play();
        // Never let the callback throw: an unhandled Timer exception kills the process.
        _deviceWatch = new Timer(_ =>
        {
            try { CheckDefaultDevice(); } catch { /* next tick retries */ }
        }, null, DeviceCheckIntervalMs, DeviceCheckIntervalMs);
    }

    // 100ms buffer: enough margin for the 31-47ms scheduler-quantum stalls
    // the field logs showed, while keeping click-to-ear latency low — at
    // 200ms every timeline click played the OLD position for 200ms first,
    // which read as a skip/pause. (The old "buzz at 50ms" was actually the
    // Array.Clear pad bug, not buffer starvation; an underrun now renders a
    // clean declicked pad, not stale audio.)
    /// <summary>Requested WasapiOut buffer depth. Also the lead of the segment's
    /// consumed-frame position over the speaker (see VlcAudioPlayer.OutputLatency).</summary>
    public const int OutputLatencyMs = 100;

    // ── Multi-channel upmix (Settings → Audio) ──
    // Read at output creation, so a change takes effect through the same rebuild
    // path a device swap uses. Static because the sink is created before any
    // settings object reaches the player and recreated on device changes.
    private static int s_upmixMode = (int)Services.UpmixMode.Off;

    /// <summary>Speaker-fill mode for 5.1/7.1 devices; changing it needs <see cref="RequestRebuild"/> on a live sink.</summary>
    public static UpmixMode UpmixMode
    {
        get => (UpmixMode)Volatile.Read(ref s_upmixMode);
        set => Volatile.Write(ref s_upmixMode, (int)value);
    }

    /// <summary>Parses the persisted setting name; unknown values mean Off.</summary>
    public static UpmixMode ParseUpmixMode(string? name) =>
        Enum.TryParse<UpmixMode>(name, ignoreCase: true, out var mode) ? mode : Services.UpmixMode.Off;

    /// <summary>Recreates the WASAPI output (same device) so a new upmix mode applies. Playback continues from the staged ring.</summary>
    public void RequestRebuild()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _rebuilding, 1) == 0)
            Task.Run(RebuildLoop);
    }

    /// <summary>Channels in the default device's mix format, or 2 when it cannot be read.</summary>
    private static int DeviceMixChannels()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioClient.MixFormat.Channels;
        }
        catch { return 2; }
    }

    private WasapiOut CreateOutput()
    {
        var wasapiOut = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: OutputLatencyMs);
        try
        {
            ISampleProvider render = _renderSource;
            var mode = UpmixMode;
            if (mode != Services.UpmixMode.Off)
            {
                var deviceChannels = DeviceMixChannels();
                if (deviceChannels > Channels)
                {
                    // Shared mode takes IEEE float at the mix format's channel count natively,
                    // so the upmixed stream needs no resampler and stays sample-accurate.
                    render = new UpmixSampleProvider(_renderSource, deviceChannels, mode);
                    DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.Upmix", $"mode={mode}, channels={deviceChannels}");
                }
            }
            wasapiOut.Init(new SampleToWaveProvider(render));
        }
        catch
        {
            try { wasapiOut.Dispose(); } catch { }
            throw;
        }
        wasapiOut.PlaybackStopped += OnPlaybackStopped;
        return wasapiOut;
    }

    // The render thread died. Without an exception it's our own Stop/Dispose;
    // with one the endpoint is gone (unplug, per-app reroute, driver reset) —
    // rebuild on whatever the default endpoint is now.
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || e.Exception == null) return;
        // A stopped event can arrive queued (sync-context post) after its
        // output was already replaced — never rebuild a healthy sink over it.
        WasapiOut current;
        lock (_gate) current = _out;
        if (!ReferenceEquals(sender, current)) return;
        DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.DeviceLost",
            $"{e.Exception.GetType().Name}: {e.Exception.Message}");
        if (Interlocked.Exchange(ref _rebuilding, 1) == 0)
            Task.Run(RebuildLoop);
    }

    // Default render endpoint moved while our stream is still alive on the old
    // one (device switch in Windows with the old device still present). WASAPI
    // streams never migrate on their own; VLC's native mmdevice aout follows
    // the default, so the engine must too.
    private void CheckDefaultDevice()
    {
        if (_disposed || Volatile.Read(ref _rebuilding) == 1) return;
        string currentId;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            currentId = device.ID;
        }
        catch
        {
            return; // no endpoint right now — the PlaybackStopped path owns recovery
        }
        string? boundId;
        lock (_gate) boundId = _deviceId;
        if (boundId == null || currentId == boundId) return;
        DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.DeviceChanged");
        if (Interlocked.Exchange(ref _rebuilding, 1) == 0)
            Task.Run(RebuildLoop);
    }

    private void RebuildLoop()
    {
        try
        {
            WasapiOut oldOut;
            lock (_gate) oldOut = _out;
            oldOut.PlaybackStopped -= OnPlaybackStopped;
            try { oldOut.Stop(); } catch { }
            try { oldOut.Dispose(); } catch { }

            var attempt = 0;
            while (!_disposed)
            {
                attempt++;
                WasapiOut? newOut = null;
                try
                {
                    newOut = CreateOutput();
                    string? id = null;
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        id = device.ID;
                    }
                    catch { /* watch just compares against null → no false trigger */ }
                    // New render thread: re-apply priority + MMCSS on first read.
                    _probe.RearmBoost();
                    if (_desiredPlaying) newOut.Play();
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            newOut.PlaybackStopped -= OnPlaybackStopped;
                            try { newOut.Stop(); } catch { }
                            try { newOut.Dispose(); } catch { }
                            return;
                        }
                        _out = newOut;
                        _deviceId = id;
                    }
                    DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.SinkRebuilt",
                        $"attempt={attempt}, playing={_desiredPlaying}");
                    try { Rebuilt?.Invoke(); } catch { /* subscriber's problem, not the sink's */ }
                    return;
                }
                catch (Exception ex)
                {
                    if (newOut != null)
                    {
                        newOut.PlaybackStopped -= OnPlaybackStopped;
                        try { newOut.Dispose(); } catch { }
                    }
                    DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.SinkRebuildRetry",
                        $"attempt={attempt}, {ex.GetType().Name}: {ex.Message}");
                }
                // 250ms → 4s backoff, then keep trying every 4s: "unplugged the
                // only device" stays recoverable whenever one comes back.
                Thread.Sleep(Math.Min(4000, 250 * (1 << Math.Min(attempt - 1, 4))));
            }
        }
        finally
        {
            Volatile.Write(ref _rebuilding, 0);
        }
    }

    public void Pause()
    {
        _desiredPlaying = false;
        WasapiOut current;
        lock (_gate) current = _out;
        try { current.Pause(); } catch { /* device transitional */ }
    }

    public void Resume()
    {
        _desiredPlaying = true;
        WasapiOut current;
        lock (_gate) current = _out;
        try { current.Play(); } catch { /* device transitional */ }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _deviceWatch.Dispose(); } catch { }
        try { Provider.Clear(); } catch { }
        WasapiOut current;
        lock (_gate) current = _out;
        current.PlaybackStopped -= OnPlaybackStopped;
        try { current.Stop(); } catch { }
        try { current.Dispose(); } catch { }
        try { _tap?.Dispose(); } catch { }
    }

    // Tee for the diagnostic tap: forwards renders and appends them to the WAV,
    // flushing about once a second so the file stays readable even if the app
    // is killed instead of closed. TapClock lines anchor file-time to wall-time.
    private sealed class TapProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        private readonly WaveFileWriter _writer;
        private int _sinceFlush;
        private long _totalSamples;
        private long _sinceClock;
        public WaveFormat WaveFormat => _inner.WaveFormat;

        public TapProvider(ISampleProvider inner, WaveFileWriter writer)
        {
            _inner = inner;
            _writer = writer;
        }

        private readonly ReplayDetector? _outDetector = ReplayDetector.CreateIfEnabled("Out");
        // Sentinel canary: pre-fill the (WasapiOut-reused) buffer with an
        // inaudible magic value before the provider fills it. Any surviving
        // sentinel = the provider left that region unwritten — the previous
        // period's stale samples would otherwise play again (the 10ms replay).
        private const float GapSentinel = 3.0e-38f;
        private long _lastGapLogTick;

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = GapSentinel;
            var n = _inner.Read(buffer, offset, count);
            var leaked = 0; var firstAt = -1;
            for (var i = 0; i < count; i++)
            {
                if (buffer[offset + i] == GapSentinel)
                {
                    leaked++;
                    if (firstAt < 0) firstAt = i;
                    buffer[offset + i] = 0f; // don't play the canary
                }
            }
            if (leaked > 0 && Environment.TickCount64 - _lastGapLogTick > 250)
            {
                _lastGapLogTick = Environment.TickCount64;
                DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.GapLeak",
                    $"samples={leaked}, firstAt={firstAt}, n={n}, offset={offset}, count={count}, bufLen={buffer.Length}");
            }
            _outDetector?.Observe(buffer, offset, n);
            try
            {
                _writer.WriteSamples(buffer, offset, n);
                _sinceFlush += n;
                _totalSamples += n;
                _sinceClock += n;
                if (_sinceFlush >= WaveFormat.SampleRate)
                {
                    _writer.Flush();
                    _sinceFlush = 0;
                }
                if (_sinceClock >= (long)WaveFormat.SampleRate * WaveFormat.Channels * 10)
                {
                    _sinceClock = 0;
                    DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.TapClock",
                        $"fileMs={_totalSamples * 1000 / ((long)WaveFormat.SampleRate * WaveFormat.Channels)}");
                }
            }
            catch { /* diagnostic only */ }
            return n;
        }
    }

    // Runs ON the render thread. First read: boost the thread the way VLC's
    // native aout is boosted — highest CLR priority plus MMCSS "Pro Audio" —
    // so UI/decoder/artwork bursts can't preempt rendering into an underrun.
    // Afterwards: log any read gap that eats a meaningful share of the buffer;
    // the log write also runs here, but only after the damage is already done.
    private sealed class StallProbe : ISampleProvider
    {
        [System.Runtime.InteropServices.DllImport("avrt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        private readonly ISampleProvider _inner;
        private long _lastReadTick;
        private bool _boosted;
        public WaveFormat WaveFormat => _inner.WaveFormat;

        public StallProbe(ISampleProvider inner)
        {
            _inner = inner;
        }

        // The boost latches per render thread; a rebuilt WasapiOut spins up a
        // fresh thread that must boost itself again on its first read.
        public void RearmBoost()
        {
            _boosted = false;
            _lastReadTick = 0; // don't count the dead-sink gap as a render stall
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (!_boosted)
            {
                _boosted = true;
                try
                {
                    System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;
                    uint taskIndex = 0;
                    var handle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
                    DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.RenderBoost",
                        $"mmcss={(handle != IntPtr.Zero ? "ok" : "failed")}");
                }
                catch { /* boost is best-effort */ }
            }

            // Stopwatch, not TickCount64: the 15.6ms system tick cannot resolve a
            // ~10ms read cadence (a "47ms" gap was a 3-tick quantization artifact).
            // 25ms threshold: 2+ missed engine periods — the priority-inversion
            // stalls behind the buzz are 31-47ms and were invisible at 120ms.
            var now = Stopwatch.GetTimestamp();
            var last = _lastReadTick;
            _lastReadTick = now;
            var gapMs = (now - last) * 1000.0 / Stopwatch.Frequency;
            if (last != 0 && gapMs > 25)
            {
                try
                {
                    DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.RenderStall",
                        $"gapMs={gapMs:F1}");
                }
                catch { /* diagnostic only */ }
            }
            return _inner.Read(buffer, offset, count);
        }
    }
}
