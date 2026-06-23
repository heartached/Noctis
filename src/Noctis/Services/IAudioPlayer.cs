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

    /// <summary>Fires when the active output path changes (exclusive engaged, fell back to shared, ...)
    /// with a short human-readable status. May fire on a non-UI thread.</summary>
    event EventHandler<string>? OutputModeChanged;

    /// <summary>Current playback state.</summary>
    PlaybackState State { get; }

    /// <summary>Duration of the currently loaded media.</summary>
    TimeSpan Duration { get; }

    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Monotonic playback session id, incremented for every Play request.</summary>
    long CurrentSessionId { get; }

    /// <summary>Volume level from 0 to 100.</summary>
    int Volume { get; set; }

    /// <summary>Per-track volume adjustment (-100 to +100). Applied on top of the user volume.</summary>
    int VolumeAdjust { get; set; }

    /// <summary>Pending seek position in ms to apply after next Play() call. -1 = disabled.</summary>
    long PendingSeekMs { get; set; }

    /// <summary>Flush pending volume to VLC immediately (call on drag-end).</summary>
    void CommitVolume();

    /// <summary>Whether audio output is muted.</summary>
    bool IsMuted { get; set; }

    /// <summary>Enables or disables loudness normalization.</summary>
    void SetNormalization(bool enabled);

    /// <summary>
    /// Enables Windows WASAPI exclusive-mode output for bit-perfect playback.
    /// Falls back to shared mode (with an <see cref="OutputModeChanged"/> notice)
    /// when the device refuses the exclusive open. No-op on other platforms.
    /// </summary>
    void SetExclusiveMode(bool enabled);

    /// <summary>True while audio is actually flowing through an exclusive-mode device stream.</summary>
    bool ExclusiveModeActive { get; }

    /// <summary>Short description of the active output path for the signal-path
    /// display, e.g. "WASAPI Exclusive — 44.1 kHz / 24-bit".</summary>
    string OutputDescription { get; }

    /// <summary>ReplayGain currently applied to the output in dB. 0 = bypass
    /// (mode off, or no tags on the current track).</summary>
    double ReplayGainAppliedDb { get; }

    /// <summary>
    /// Apply ReplayGain to the currently loaded track based on its tags. Reads
    /// REPLAYGAIN_TRACK_GAIN / REPLAYGAIN_ALBUM_GAIN from the file and scales
    /// the output volume by 10^((gain + preampDb)/20). Pass "Off" to clear.
    /// </summary>
    /// <param name="mode">"Off", "Track", "Album", or "Auto".</param>
    /// <param name="preampDb">Pre-amp in dB.</param>
    void ApplyReplayGain(string mode, double preampDb);

    /// <summary>Path to the currently loaded media, or null. Used to re-apply RG.</summary>
    string? CurrentMediaPath { get; }

    /// <summary>Enables or disables the next track transition fade. When
    /// <paramref name="fadeOut"/> is false (AutoMix's no-silence handoff) the outgoing
    /// track is not faded out early. When <paramref name="overlap"/> is true (AutoMix
    /// overlap blend) both tracks play simultaneously through the crossover.</summary>
    void SetCrossfade(bool enabled, int durationSeconds, AutoMixFadeCurve fadeCurve = AutoMixFadeCurve.SmoothEase, bool fadeOut = true, bool overlap = false);

    /// <summary>
    /// Enables gapless playback: when the next track was prepared via
    /// <see cref="PrepareNext"/> and no crossfade is active, track changes hand
    /// off to the prepared player instantly at full volume instead of the
    /// stop/parse/start path.
    /// </summary>
    void SetGapless(bool enabled);

    /// <summary>Prepares a next media item for an AutoMix transition without making it active.</summary>
    void PrepareNext(string filePath, long startPositionMs = -1);

    /// <summary>Cancels and releases any prepared inactive media item.</summary>
    void CancelPreparedNext();

    /// <summary>Applies the equalizer curve to the output.</summary>
    /// <param name="enabled">Whether to enable the EQ.</param>
    /// <param name="bands">10-element array of graphic band amplitudes in dB (-12 to +12),
    /// typically produced by <see cref="ParametricEqMath.MapToGraphicBands"/>.</param>
    /// <param name="preampDb">Pre-amplification in dB (-20 to +20).</param>
    void SetAdvancedEqualizer(bool enabled, float[] bands, float preampDb);

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
