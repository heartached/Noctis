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

    /// <summary>How far <see cref="Position"/> (and PositionChanged) lead the speaker:
    /// the depth of output buffer already filled but not yet heard. Zero when the
    /// reported position is already the audible one. Lyrics subtract it.</summary>
    TimeSpan OutputLatency { get; }

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

    /// <summary>
    /// Windows only: plays to the given MMDevice endpoint id instead of the Windows
    /// default (Settings → Output → Output Device). Empty/null = follow the system
    /// default. Applies to every output path (exclusive sink, gapless engine, VLC's
    /// own mmdevice output); a device change while playing moves audio to the new
    /// endpoint. No-op on other platforms.
    /// </summary>
    void SetOutputDevice(string? deviceId) { }

    /// <summary>True while audio is actually flowing through an exclusive-mode device stream.</summary>
    bool ExclusiveModeActive { get; }

    /// <summary>
    /// Exclusive Mode is on in Settings but this session fell back to shared output (the
    /// device could not be opened, or it went away). The saved choice is kept; turning it
    /// off and on again, or the next launch, retries.
    /// </summary>
    bool ExclusiveModePaused => false;

    /// <summary>True while the active output chain feeds the shared level meters (visualizer,
    /// beat pulse) from the PCM it renders. False when the native output plays the audio
    /// unseen, so a side decode has to feed them instead.</summary>
    bool OutputTapsMeters => false;

    /// <summary>True only when the equalizer actually alters the signal. An enabled
    /// equalizer sitting on a flat curve is bypassed, so it does not count.</summary>
    bool EqualizerActive { get; }

    /// <summary>Short description of the active output path for the signal-path
    /// display, e.g. "WASAPI Exclusive, 44.1 kHz / 24-bit".</summary>
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

    /// <summary>
    /// Drops any cached ReplayGain tags and re-applies the current mode, after a scan
    /// rewrote the tags of files that may be playing or staged.
    /// </summary>
    void ReloadReplayGainTags() { }

    /// <summary>Path to the currently loaded media, or null. Used to re-apply RG.</summary>
    string? CurrentMediaPath { get; }

    /// <summary>Enables or disables the next track transition fade. When
    /// <paramref name="fadeOut"/> is false (AutoMix's no-silence handoff) the outgoing
    /// track is not faded out early. When <paramref name="overlap"/> is true (AutoMix
    /// overlap blend) both tracks play simultaneously through the crossover.</summary>
    void SetCrossfade(bool enabled, int durationSeconds, AutoMixFadeCurve fadeCurve = AutoMixFadeCurve.SmoothEase, bool fadeOut = true, bool overlap = false);

    /// <summary>GitHub #73: fade the level out before a pause lands and back in after a
    /// resume, over <paramref name="durationMs"/>. Off = the hard cut. Engines without a
    /// gain stage may ignore it.</summary>
    void SetPlayPauseFade(bool enabled, int durationMs) { }

    /// <summary>
    /// Enables gapless playback: when the next track was prepared via
    /// <see cref="PrepareNext"/> and no crossfade is active, track changes hand
    /// off to the prepared player instantly at full volume instead of the
    /// stop/parse/start path.
    /// </summary>
    void SetGapless(bool enabled);

    /// <summary>Playback speed (0.5–2.0, 1.0 = normal), pitch-preserving. Sticks across
    /// track changes until changed again; not persisted.</summary>
    void SetPlaybackRate(double rate);

    /// <summary>
    /// Pitch shift in semitones (−12 … +12) independent of speed. Gapless-engine output
    /// only: the LibVLC output paths have no pitch stage and ignore it.
    /// </summary>
    void SetPitchSemitones(double semitones);

    /// <summary>
    /// True when <see cref="SetPitchSemitones"/> is audible, i.e. playback runs through the
    /// gapless engine. False on macOS (no engine there) and when the engine fell back to a
    /// LibVLC output, so the UI can hide a Pitch control that would do nothing.
    /// </summary>
    bool SupportsPitch => false;

    /// <summary>
    /// Multi-channel upmix for 5.1/7.1 devices ("Off", "Duplicate" or "Surround" — see
    /// <see cref="UpmixMode"/>). Shared-mode gapless engine only; Exclusive Mode stays bit-exact.
    /// </summary>
    void SetUpmixMode(string mode);

    /// <summary>
    /// True when this player was started with Headphone surround on: 5.1/7.1 sources render
    /// binaurally to stereo. Fixed for the player's lifetime (a LibVLC instance option), so
    /// Settings compares it with the saved choice to say a restart is needed.
    /// </summary>
    bool HeadphoneSurroundActive => false;

    /// <summary>
    /// True when this player was started with Multichannel output on (and Headphone surround
    /// off): the output runs at the device's surround channel count. Fixed for the player's
    /// lifetime, so Settings compares it with the saved choice to say a restart is needed.
    /// </summary>
    bool MultichannelOutputActive => false;

    /// <summary>
    /// Mono Audio (L and R averaged onto both channels) and the left/right balance
    /// (-1 left only … 0 centre … +1 right only), ramped click-free in the output's sample
    /// stage. Exclusive Mode bypasses both (bit-perfect).
    /// </summary>
    void SetChannelMix(bool mono, double balance) { }

    /// <summary>
    /// True when the current output runs the sample stage that applies
    /// <see cref="SetChannelMix"/> (false in Exclusive Mode and on outputs without one).
    /// </summary>
    bool ChannelMixAvailable => false;

    /// <summary>
    /// A playback fade layer (0..1) on top of the user's volume, which it never changes:
    /// the sleep timer's fade-out. Reached linearly over <paramref name="rampMs"/>.
    /// </summary>
    void SetPlaybackFadeGain(double gain, int rampMs) { }

    /// <summary>
    /// Whether <paramref name="filePath"/> can be opened right now: false for a local file that
    /// is not on disk (moved, deleted, its drive unplugged). Paths that are not files (streams,
    /// audio CD) count as there. Asked fresh every time, so a file that comes back plays again
    /// (GitHub #154).
    /// </summary>
    bool SourceExists(string filePath) => true;

    /// <summary>Prepares a next media item for an AutoMix transition without making it active.</summary>
    void PrepareNext(string filePath, long startPositionMs = -1);

    /// <summary>True when <see cref="PrepareNext"/> also stages remote http(s) streams, so a
    /// gapless/AutoMix transition may hand off to a media-server track early. False: a remote
    /// next track waits for <see cref="TrackEnded"/> and opens cold.</summary>
    bool PreparesRemoteStreams => false;

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

    /// <summary>Music video audio: plays <paramref name="filePath"/> (a clip), or the song file
    /// <paramref name="fallbackPath"/> when the clip won't open or has no audio stream.
    /// Engines without that fallback play <paramref name="filePath"/> as-is.</summary>
    void Play(string filePath, string? fallbackPath) => Play(filePath);

    /// <summary>Pauses playback. No-op if not currently playing.</summary>
    void Pause();

    /// <summary>Resumes playback from the paused position.</summary>
    void Resume();

    /// <summary>Stops playback and unloads the media.</summary>
    void Stop();

    /// <summary>Seeks to a specific position in the current track.</summary>
    void Seek(TimeSpan position);
}
