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
/// Gated by NOCTIS_WASAPI=1 and off by default. <see cref="TryCreate"/> returns
/// null on non-Windows or device-init failure so the caller falls back to the
/// existing LibVLC output path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiGainOutput : IDisposable
{
    private const int BytesPerSample = 4; // FL32

    // Render format is matched to the default device's mix format (sample rate)
    // so WASAPI shared mode accepts it without a format-unsupported failure (the
    // usual cause of a silent WASAPI path on 44.1kHz endpoints). LibVLC is told
    // this exact rate/channels via SetAudioFormat and downmixes/resamples to it.
    public int SampleRate { get; }
    public int Channels { get; }

    private readonly BufferedWaveProvider _buffer;
    private readonly GainSampleProvider _gain;
    private readonly WasapiOut _out;
    private volatile bool _disposed;
    private long _bytesWritten;
    private int _writeCount;

    public static WasapiGainOutput? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return new WasapiGainOutput(); }
        catch (Exception ex)
        {
            Diag($"TryCreate FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private WasapiGainOutput()
    {
        Diag("=== WasapiGainOutput init ===");

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

        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        _buffer = new BufferedWaveProvider(format)
        {
            // Bounded queue between LibVLC's decode thread and the WASAPI render
            // thread. Large enough to ride out GC/disk jitter, small enough that
            // seek/track-change latency stays low. Write() applies backpressure
            // rather than overflowing.
            BufferDuration = TimeSpan.FromMilliseconds(1000),
            DiscardOnBufferOverflow = false,
            ReadFully = true, // return silence (not 0) when idle so WasapiOut keeps running
        };
        _gain = new GainSampleProvider(_buffer.ToSampleProvider(), Channels, SampleRate);
        _out = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        _out.Init(_gain);
        _out.Play();
        Diag($"render format: FL32 {SampleRate}Hz {Channels}ch | WasapiOut state={_out.PlaybackState}");
    }

    /// <summary>
    /// Set the target amplitude (0..1). Applied per-sample at the output with a
    /// short interpolation, so it is click-free at any change speed and audible
    /// within roughly one render quantum (~10ms) — i.e. real-time.
    /// </summary>
    public void SetGainTarget(float target) => _gain.SetTarget(target);

    /// <summary>
    /// Enqueue interleaved FL32 PCM from LibVLC's audio play callback. Blocks
    /// briefly when the buffer is full to pace LibVLC's decoder and bound latency.
    /// </summary>
    public void Write(byte[] data, int count)
    {
        if (_disposed) return;

        // Backpressure: wait for the render thread to drain space instead of
        // throwing on overflow. Capped so teardown can't deadlock the audio thread.
        var deadline = Environment.TickCount64 + 2000;
        while (!_disposed &&
               _buffer.BufferLength - _buffer.BufferedBytes < count &&
               Environment.TickCount64 < deadline)
        {
            Thread.Sleep(2);
        }

        if (_disposed) return;
        try
        {
            _buffer.AddSamples(data, 0, count);
            _bytesWritten += count;
            // Log the first few writes and then periodically, so we can confirm
            // PCM is actually flowing and WasapiOut is rendering it.
            var n = ++_writeCount;
            if (n <= 3 || n % 200 == 0)
                Diag($"write #{n}: {count}B | buffered={_buffer.BufferedBytes}B | state={_out.PlaybackState} | total={_bytesWritten}B");
        }
        catch (Exception ex)
        {
            Diag($"AddSamples threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Pause()
    {
        if (_disposed) return;
        try { _out.Pause(); } catch { }
    }

    public void Resume()
    {
        if (_disposed) return;
        try { _out.Play(); } catch { }
    }

    public void Flush()
    {
        if (_disposed) return;
        try { _buffer.ClearBuffer(); } catch { }
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
    private static readonly object _diagGate = new();
    private static string? _diagPath;

    internal static void Diag(string msg)
    {
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
    /// </summary>
    private sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _channels;
        private float _current = 1f;
        private volatile float _target = 1f;
        private readonly float _step; // per-frame gain step to reach target in ~15ms
        private long _readCount;
        private long _setCount;

        public GainSampleProvider(ISampleProvider src, int channels, int sampleRate)
        {
            _src = src;
            _channels = channels;
            _step = 1f / (sampleRate * 0.015f);
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public void SetTarget(float target)
        {
            target = Math.Clamp(target, 0f, 1f);
            _target = target;
            if (++_setCount <= 8 || _setCount % 100 == 0)
                Diag($"SetTarget #{_setCount}: {target:F4}");
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _src.Read(buffer, offset, count);
            var target = _target;
            var cur = _current;
            var step = _step;
            var peak = 0f;

            for (var i = 0; i + _channels <= read; i += _channels)
            {
                if (cur < target) cur = Math.Min(target, cur + step);
                else if (cur > target) cur = Math.Max(target, cur - step);

                for (var ch = 0; ch < _channels; ch++)
                {
                    var idx = offset + i + ch;
                    var s = buffer[idx];
                    var a = s < 0 ? -s : s;
                    if (a > peak) peak = a;
                    buffer[idx] = s * cur;
                }
            }

            _current = cur;
            if (++_readCount % 400 == 1)
                Diag($"Read #{_readCount}: frames={read / _channels} srcPeak={peak:F4} gain={cur:F4} target={target:F4}");
            return read;
        }
    }
}
