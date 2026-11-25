using Noctis.Mobile.Services;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone player's State against what ExoPlayer reports without being asked
/// (<see cref="PlayerStateMirror"/>, run by Media3AudioPlayer). A permanent audio-focus loss
/// while a stream was still buffering left State on Playing over a paused player: buffering
/// had already made IsPlaying false, so no IsPlaying change came, and the next tap only paused.
/// </summary>
public class MobilePlayerStateMirrorTests
{
    [Fact]
    public void PlayWhenReadyCleared_WhileBuffering_Pauses()
    {
        // Buffering: IsPlaying is false already and the player is not READY, so this changes nothing.
        Assert.Equal(PlaybackState.Playing, PlayerStateMirror.OnIsPlayingChanged(PlaybackState.Playing, isPlaying: false, ready: false));
        // The focus loss clears PlayWhenReady: that is the pause.
        Assert.Equal(PlaybackState.Paused, PlayerStateMirror.OnPlayWhenReadyChanged(PlaybackState.Playing, playWhenReady: false));
    }

    [Fact]
    public void PlayWhenReadySet_LeavesStateToIsPlaying()
    {
        // A session Play while paused and buffering: Playing once audio really moves, not before.
        Assert.Equal(PlaybackState.Paused, PlayerStateMirror.OnPlayWhenReadyChanged(PlaybackState.Paused, playWhenReady: true));
        Assert.Equal(PlaybackState.Playing, PlayerStateMirror.OnIsPlayingChanged(PlaybackState.Paused, isPlaying: true, ready: true));
    }

    [Fact]
    public void Stopped_StaysStopped()
    {
        // Our own Stop() idles the player: nothing it reports afterwards is an outside pause.
        Assert.Equal(PlaybackState.Stopped, PlayerStateMirror.OnPlayWhenReadyChanged(PlaybackState.Stopped, playWhenReady: false));
        Assert.Equal(PlaybackState.Stopped, PlayerStateMirror.OnIsPlayingChanged(PlaybackState.Stopped, isPlaying: false, ready: false));
    }

    [Fact]
    public void TransientLoss_WhileReady_StillPausesThroughIsPlaying()
    {
        // A call: PlayWhenReady stays true (a suppression reason), IsPlaying goes false while READY.
        Assert.Equal(PlaybackState.Playing, PlayerStateMirror.OnPlayWhenReadyChanged(PlaybackState.Playing, playWhenReady: true));
        Assert.Equal(PlaybackState.Paused, PlayerStateMirror.OnIsPlayingChanged(PlaybackState.Playing, isPlaying: false, ready: true));
    }
}
