using System;
using System.Threading;
using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Post-buffer mute for the gapless engine (Discord "Mute button unresponsive on
/// Windows", 2026-08-17). The engine stages seconds of decoded PCM in a ring ahead of
/// the device, so LibVLC's own mute — software gain applied to the blocks it hands us,
/// i.e. BEFORE the ring — was only heard once the staged audio drained (~2 s), and
/// unmute the same again because the ring was by then full of zeros. This gate sits in
/// the render chain, after the ring, so mute and unmute take effect within the device
/// buffer (~100 ms).
///
/// Unmuted at rest it is a pure pass-through (no multiply), so the bit-exact splice
/// stays bit-exact. Each transition ramps the gain linearly per FRAME over a few
/// milliseconds so neither edge clicks. It also holds a rebuilt output silent until the
/// owner has put the user volume on the new audio session (<see cref="Hold"/>). Runs on
/// the render thread; the flag and the hold deadline are the only cross-thread state.
/// </summary>
internal sealed class MuteGateProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _stepPerFrame;
    private volatile bool _muted;
    private float _gain = 1f; // render thread only
    private long _holdUntilMs; // Environment.TickCount64 deadline of a hold; 0 = none

    public MuteGateProvider(ISampleProvider source, int rampMs = 8)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = Math.Max(1, source.WaveFormat.Channels);
        var rampFrames = Math.Max(1, source.WaveFormat.SampleRate * Math.Max(1, rampMs) / 1000);
        _stepPerFrame = 1f / rampFrames;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Set from any thread; the next render pass ramps toward it.</summary>
    public bool IsMuted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Current applied gain (1 = open, 0 = fully muted); for diagnostics and tests.</summary>
    public float CurrentGain => _gain;

    /// <summary>
    /// Silence from the next read on, with no ramp down, until <see cref="ReleaseHold"/> or
    /// <paramref name="maxMs"/> pass; the gain then ramps back up. Only for an output that
    /// has not rendered yet (a rebuilt one): snapping to 0 mid-stream would click.
    /// </summary>
    public void Hold(int maxMs) => Interlocked.Exchange(ref _holdUntilMs, Environment.TickCount64 + Math.Max(1, maxMs));

    /// <summary>Ends a hold. Returns false when none was set.</summary>
    public bool ReleaseHold() => Interlocked.Exchange(ref _holdUntilMs, 0) != 0;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        var target = _muted ? 0f : 1f;
        var holdUntil = Interlocked.Read(ref _holdUntilMs);
        if (holdUntil != 0 && Environment.TickCount64 < holdUntil)
        {
            _gain = 0f; // nothing played before the hold, so nothing to ramp down from
            target = 0f;
        }
        if (_gain == target)
        {
            if (target == 0f)
                buffer.AsSpan(offset, read).Clear(); // float span: clears SAMPLES, never bytes
            return read;
        }

        // Mid-ramp: one gain value per frame so all channels move together.
        for (var i = 0; i < read; i += _channels)
        {
            if (_gain < target) _gain = Math.Min(target, _gain + _stepPerFrame);
            else if (_gain > target) _gain = Math.Max(target, _gain - _stepPerFrame);

            var end = Math.Min(i + _channels, read);
            for (var s = i; s < end; s++)
                buffer[offset + s] *= _gain;
        }
        return read;
    }
}
