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
///     Negotiates the device's native bit depth (24 → 16 → float32); LibVLC's
///     FL32 PCM is converted at the output, which is lossless for integer
///     sources at the same rate as long as no DSP is active.
///
/// Both return null on non-Windows or device-init failure so the caller can
/// fall back to another output path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiGainOutput : IDisposable
{
    private const int BytesPerSample = 4; // FL32

    // AUDCLNT_E_DEVICE_IN_USE: another client holds the endpoint exclusively.
    private const int HrDeviceInUse = unchecked((int)0x8889000A);

    // Render format. Shared mode matches the default device's mix format (sample
    // rate) so WASAPI accepts it without a format-unsupported failure; exclusive
    // mode uses the source rate handed in by the audio-setup callback. LibVLC is
    // told this exact rate/channels and downmixes/resamples to it if needed.
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
        _out.Init(_gain);
        _out.Play();
        Diag($"render format: FL32 {SampleRate}Hz {Channels}ch | WasapiOut state={_out.PlaybackState}");
    }

    private WasapiGainOutput(int sampleRate, int channels)
    {
        Diag($"=== WasapiGainOutput init (exclusive, {sampleRate}Hz {channels}ch) ===");

        SampleRate = sampleRate;
        Channels = channels;
        IsExclusive = true;

        (_buffer, _gain) = CreateInputChain(SampleRate, Channels);

        // Exclusive mode requires a device-native format. Prefer 24-bit (covers
        // hi-res sources), then 16-bit, then float32; first Init that the driver
        // accepts wins. The gain stage stays in float upstream — at unity gain a
        // float multiply by 1.0 is bit-exact, so integer sources stay bit-perfect.
        Exception? lastError = null;
        foreach (var bits in new[] { 24, 16, 32 })
        {
            IWaveProvider rendered = bits switch
            {
                24 => new SampleToWaveProvider24(_gain),
                16 => new SampleToWaveProvider16(_gain),
                _ => new SampleToWaveProvider(_gain),
            };

            var attempt = new WasapiOut(AudioClientShareMode.Exclusive, useEventSync: true, latency: 100);
            try
            {
                attempt.Init(rendered);
                attempt.Play();
                _out = attempt;
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
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        var buffer = new BufferedWaveProvider(format)
        {
            // Bounded queue between LibVLC's decode thread and the WASAPI render
            // thread. Large enough to ride out GC/disk jitter, small enough that
            // seek/track-change latency stays low. Write() applies backpressure
            // rather than overflowing.
            BufferDuration = TimeSpan.FromMilliseconds(1000),
            DiscardOnBufferOverflow = false,
            ReadFully = true, // return silence (not 0) when idle so WasapiOut keeps running
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
