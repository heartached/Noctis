using System;
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
        _out = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
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
    // is killed instead of closed.
    private sealed class TapProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        private readonly WaveFileWriter _writer;
        private int _sinceFlush;
        public WaveFormat WaveFormat => _inner.WaveFormat;

        public TapProvider(ISampleProvider inner, WaveFileWriter writer)
        {
            _inner = inner;
            _writer = writer;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            try
            {
                _writer.WriteSamples(buffer, offset, n);
                _sinceFlush += n;
                if (_sinceFlush >= WaveFormat.SampleRate)
                {
                    _writer.Flush();
                    _sinceFlush = 0;
                }
            }
            catch { /* diagnostic only */ }
            return n;
        }
    }
}
