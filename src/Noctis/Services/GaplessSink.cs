using System;
using System.Diagnostics;
using System.IO;
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
public sealed class GaplessSink : IDisposable
{
    private readonly WasapiOut _out;
    private readonly WaveFileWriter? _tap; // NOCTIS_ENGINE_TAP diagnostic capture

    public GaplessSpliceProvider Provider { get; }
    public int SampleRate { get; }
    public int Channels { get; }

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
        renderSource = new StallProbe(renderSource);
        // 100ms buffer: enough margin for the 31-47ms scheduler-quantum stalls
        // the field logs showed, while keeping click-to-ear latency low — at
        // 200ms every timeline click played the OLD position for 200ms first,
        // which read as a skip/pause. (The old "buzz at 50ms" was actually the
        // Array.Clear pad bug, not buffer starvation; an underrun now renders a
        // clean declicked pad, not stale audio.)
        _out = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        _out.Init(new SampleToWaveProvider(renderSource));
        // Render immediately and forever: the provider always returns full
        // buffers (silence when idle), so the stream never stops between
        // tracks — the property true gapless depends on.
        _out.Play();
    }

    public void Pause()
    {
        try { _out.Pause(); } catch { /* device transitional */ }
    }

    public void Resume()
    {
        try { _out.Play(); } catch { /* device transitional */ }
    }

    public void Dispose()
    {
        try { Provider.Clear(); } catch { }
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
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
