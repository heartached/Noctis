using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Abstraction over the audio playback engine.
/// The only component that touches the native audio backend (libVLC).
/// ViewModels interact with playback exclusively through this interface.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Fires when the current track finishes playing naturally.</summary>
    event EventHandler? TrackEnded;

    /// <summary>Fires when playback position changes (roughly 4 times per second).</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>Fires when an error occurs during playback.</summary>
    event EventHandler<string>? PlaybackError;

    /// <summary>Fires when the actual duration is resolved from the decoder (may differ from metadata).</summary>
    event EventHandler<TimeSpan>? DurationResolved;

    /// <summary>Current playback state.</summary>
    PlaybackState State { get; }

    /// <summary>Duration of the currently loaded media.</summary>
    TimeSpan Duration { get; }

    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Volume level from 0 to 100.</summary>
    int Volume { get; set; }

    /// <summary>Per-track volume adjustment (-100 to +100). Applied on top of the user volume.</summary>
    int VolumeAdjust { get; set; }

    /// <summary>Flush pending volume to VLC immediately (call on drag-end).</summary>
    void CommitVolume();

    /// <summary>Whether audio output is muted.</summary>
    bool IsMuted { get; set; }

    /// <summary>Enables or disables the sound enhancer (equalizer boost).</summary>
    void SetSoundEnhancer(bool enabled, int level);

    /// <summary>Enables or disables loudness normalization.</summary>
    void SetNormalization(bool enabled);

    /// <summary>Enables or disables crossfade between tracks.</summary>
    void SetCrossfade(bool enabled, int durationSeconds);

    /// <summary>Applies the advanced 10-band equalizer.</summary>
    /// <param name="enabled">Whether to enable the EQ.</param>
    /// <param name="presetIndex">VLC preset index (0+), or -1 for custom bands.</param>
    /// <param name="customBands">10-element array of band amplitudes in dB (-12 to +12). Used when presetIndex is -1.</param>
    void SetAdvancedEqualizer(bool enabled, int presetIndex, float[] customBands);

    /// <summary>Loads and begins playing an audio file.</summary>
    void Play(string filePath);

    /// <summary>Pauses playback. No-op if not currently playing.</summary>
    void Pause();

    /// <summary>Resumes playback from the paused position.</summary>
    void Resume();

    /// <summary>Stops playback and unloads the media.</summary>
    void Stop();

    /// <summary>Seeks to a specific position in the current track.</summary>
    void Seek(TimeSpan position);
}
