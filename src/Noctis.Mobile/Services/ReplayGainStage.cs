using System.Collections.Concurrent;

namespace Noctis.Mobile.Services;

/// <summary>
/// The phone's ReplayGain gain stage: the sound itself is scaled, inside Media3's audio sink
/// (Media3AudioPlayer's ReplayGainAudioProcessor hands it every PCM buffer on ExoPlayer's playback
/// thread), not the player's volume. Two reasons. The player's volume stops at 1, so a quiet song's
/// positive gain could never be heard; here it is, with a soft limiter so a boost never goes past
/// full scale. And the sink knows the exact sample a gapless song starts at (it is reconfigured
/// there, <see cref="BeginStream"/>), where a volume set from the player's transition callback
/// lands late, after the next song's first notes played at the old level.
///
/// Levels are amplitude factors per media id (the library path), set on the main thread when an
/// item is built (<see cref="SetGain"/>), so the song queued behind the playing one has its level
/// ready but takes it only when its first sample reaches the sink. A changed setting re-sets the
/// queued items and calls <see cref="Refresh"/>: the playing song glides to its new level over
/// <see cref="SettingsRampMs"/>. A new song starts with a <see cref="BoundaryRampMs"/> glide from the
/// last level, short enough to be inaudible and long enough that two gapless songs at different
/// levels can never click. A seek (the sink flushes with the same item) changes nothing; nothing
/// but a format change touches the channel layout. No allocation per buffer.
/// </summary>
public sealed class ReplayGainStage
{
    /// <summary>A setting changed mid-song: the level glides this long instead of jumping.</summary>
    public const int SettingsRampMs = 60;

    /// <summary>A new song's level, glided into from its first sample.</summary>
    public const int BoundaryRampMs = 5;

    /// <summary>-1 dBFS: above it a boosted sample bends smoothly towards full scale, never past it.
    /// A cut (gain at or under 1) can't clip and never goes through the limiter.</summary>
    public const float LimiterThreshold = 0.891f;

    private const float Headroom = 1f - LimiterThreshold;

    private readonly ConcurrentDictionary<string, float> _gains = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _labels = new(StringComparer.Ordinal);
    private int _generation;
    private volatile string? _announced;

    // Playback thread only.
    private int _seenGeneration;
    private string? _mediaId;
    private bool _followAnnounced;
    private int _sampleRate = 44100, _channels = 2;
    private float _gain = 1f, _target = 1f, _step;
    private int _rampFrames, _channel;

    // ── Main thread ──

    /// <summary>The level <paramref name="mediaId"/> plays at, from its next stream start (or the
    /// next <see cref="Refresh"/> while it plays).</summary>
    public void SetGain(string mediaId, double linear, string? label = null)
    {
        _gains[mediaId] = double.IsFinite(linear) && linear > 0 ? (float)linear : 1f;
        if (label != null) _labels[mediaId] = label;
    }

    /// <summary>The song's name given with its level, for the log; the id when none was.</summary>
    public string LabelFor(string? mediaId) =>
        mediaId == null ? "?" : _labels.TryGetValue(mediaId, out var label) ? label : mediaId;

    /// <summary>Forgets every level but those of <paramref name="mediaIds"/> (the items still queued).</summary>
    public void Retain(IEnumerable<string> mediaIds)
    {
        var keep = new HashSet<string>(mediaIds, StringComparer.Ordinal);
        foreach (var key in _gains.Keys)
            if (!keep.Contains(key)) _gains.TryRemove(key, out _);
        foreach (var key in _labels.Keys)
            if (!keep.Contains(key)) _labels.TryRemove(key, out _);
    }

    /// <summary>A setting changed: the playing song glides to its (re-set) level.</summary>
    public void Refresh() => Interlocked.Increment(ref _generation);

    /// <summary>The item the player says is playing, for a stream the sink could not name
    /// (<see cref="BeginStream"/> with null). The player says so when the song is heard, while the
    /// sink is already the AudioTrack's buffer further on: a fallback only. Its switch glides
    /// over <see cref="BoundaryRampMs"/> like a stream start.</summary>
    public void Announce(string? mediaId) => _announced = mediaId;

    /// <summary>The level set for <paramref name="mediaId"/>; 1 when none is.</summary>
    public float GainFor(string? mediaId) => mediaId != null && _gains.TryGetValue(mediaId, out var g) ? g : 1f;

    // ── Playback thread ──

    /// <summary>The item whose samples the stage is scaling now.</summary>
    public string? MediaId => _mediaId;

    /// <summary>True while the stage follows <see cref="Announce"/> (the sink did not name the stream).</summary>
    public bool FollowsPlayer => _followAnnounced;
    public float Gain => _gain;
    public float TargetGain => _target;

    /// <summary>The PCM format from here on (a format change; the level carries over).</summary>
    public void Configure(int sampleRate, int channels)
    {
        if (sampleRate > 0) _sampleRate = sampleRate;
        if (channels > 0) _channels = channels;
        _channel = 0;
    }

    /// <summary>The samples from here on are <paramref name="mediaId"/>'s (the sink's stream start:
    /// a new song, gapless or not, or a seek in the same one). Null: the sink could not say whose,
    /// follow <see cref="Announce"/> instead.</summary>
    public void BeginStream(string? mediaId)
    {
        _followAnnounced = mediaId == null;
        _mediaId = mediaId ?? _announced;
        _seenGeneration = Volatile.Read(ref _generation);
        _channel = 0;
        RampTo(GainFor(_mediaId), BoundaryRampMs);
    }

    /// <summary>Takes up a refreshed level (or, following the player, a new item). False when the
    /// samples pass as they are (unity, no glide): the caller can copy the buffer untouched.</summary>
    public bool Update()
    {
        if (_followAnnounced)
        {
            var announced = _announced;
            if (!string.Equals(announced, _mediaId, StringComparison.Ordinal))
            {
                _mediaId = announced;
                _seenGeneration = Volatile.Read(ref _generation);
                RampTo(GainFor(announced), BoundaryRampMs);
            }
        }
        var generation = Volatile.Read(ref _generation);
        if (generation != _seenGeneration)
        {
            _seenGeneration = generation;
            RampTo(GainFor(_mediaId), SettingsRampMs);
        }
        return _rampFrames > 0 || _gain != 1f;
    }

    /// <summary>Scales interleaved float samples in place.</summary>
    public void Process(Span<float> samples)
    {
        if (_rampFrames == 0 && _gain <= 1f)
        {
            var g = _gain;
            for (var i = 0; i < samples.Length; i++) samples[i] *= g;
            return;
        }
        for (var i = 0; i < samples.Length; i++)
        {
            var g = FrameGain();
            samples[i] = Shape(samples[i] * g, g);
        }
    }

    /// <summary>Scales interleaved 16-bit samples in place (Media3's default PCM).</summary>
    public void Process(Span<short> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var g = FrameGain();
            var y = Shape(samples[i] * (1f / 32768f) * g, g) * 32768f;
            samples[i] = y >= 32767f ? short.MaxValue : y <= -32768f ? short.MinValue : (short)MathF.Round(y);
        }
    }

    /// <summary>The gain for the next sample: it steps once per frame (all channels alike) while a glide runs.</summary>
    private float FrameGain()
    {
        if (_channel == 0 && _rampFrames > 0)
        {
            _gain += _step;
            if (--_rampFrames == 0) _gain = _target;
        }
        if (++_channel >= _channels) _channel = 0;
        return _gain;
    }

    private void RampTo(float target, int ms)
    {
        _target = target;
        if (target == _gain)
        {
            _rampFrames = 0;
            return;
        }
        var frames = Math.Max(1, (int)((long)_sampleRate * ms / 1000));
        _rampFrames = frames;
        _step = (target - _gain) / frames;
    }

    /// <summary>A boosted sample through the soft limiter; a cut sample as it is.</summary>
    private static float Shape(float y, float gain) => gain <= 1f ? y : SoftLimit(y);

    /// <summary>Unchanged up to <see cref="LimiterThreshold"/>, then a tanh knee that meets it with the
    /// same slope and approaches full scale without reaching it: memoryless, so nothing to flush.</summary>
    internal static float SoftLimit(float y)
    {
        var a = MathF.Abs(y);
        if (a <= LimiterThreshold) return y;
        var bent = LimiterThreshold + Headroom * MathF.Tanh((a - LimiterThreshold) / Headroom);
        return y < 0 ? -bent : bent;
    }
}
