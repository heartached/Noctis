using System;
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
        Provider = new GaplessSpliceProvider(SampleRate, Channels, startThresholdMs: 200);
        _out = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        _out.Init(new SampleToWaveProvider(Provider));
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
    }
}
