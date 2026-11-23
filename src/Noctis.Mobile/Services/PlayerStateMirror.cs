using Noctis.Models;

namespace Noctis.Mobile.Services;

/// <summary>
/// What the phone player's own State becomes when ExoPlayer changes without being asked
/// (Media3AudioPlayer's OnIsPlayingChanged / OnPlayWhenReadyChanged): audio focus loss,
/// headphone unplug, the lock screen's Pause and Play. Kept apart from Media3 so it can be tested.
/// </summary>
public static class PlayerStateMirror
{
    /// <summary>
    /// IsPlaying changed. No PlayWhenReady check: Media3 only clears it for a PERMANENT focus
    /// loss. A transient loss (e.g. an incoming call) is signalled as a playback-suppression
    /// reason with PlayWhenReady left true — requiring it false here would miss that case and
    /// leave State stuck on Playing over a frozen clock. <paramref name="ready"/> (StateReady)
    /// is still required for the pause: it is what stops our own Stop() (which drives the state
    /// to Idle) from re-entering here and being misread as an external pause.
    /// </summary>
    public static PlaybackState OnIsPlayingChanged(PlaybackState state, bool isPlaying, bool ready)
    {
        if (!isPlaying && state == PlaybackState.Playing && ready) return PlaybackState.Paused;
        if (isPlaying && state == PlaybackState.Paused) return PlaybackState.Playing;
        return state;
    }

    /// <summary>
    /// PlayWhenReady changed. Cleared means the player was paused (a permanent focus loss, a
    /// session Pause) in whatever state it is in. While a stream is still buffering IsPlaying is
    /// already false, so no IsPlaying change follows: without this State stayed Playing over a
    /// paused player, and the next tap only "paused" it. Set is left to
    /// <see cref="OnIsPlayingChanged"/>, which waits until audio really moves. Our own Stop()
    /// does not touch PlayWhenReady, and our own Pause() lands on Paused anyway.
    /// </summary>
    public static PlaybackState OnPlayWhenReadyChanged(PlaybackState state, bool playWhenReady) =>
        !playWhenReady && state == PlaybackState.Playing ? PlaybackState.Paused : state;
}
