using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The per-player (Linux/macOS) volume path has no OS-session handle to re-assert,
/// so a track's open volume rides libvlc_audio_set_volume — which silently returns
/// -1 (LibVLCSharp discards it) whenever the target player has no live audio
/// output. A gapless handoff's un-park write lands in exactly that state on a
/// long-idle pre-rolled standby, and PulseAudio then opens the new stream at
/// whatever level the server's stream-restore remembers for the app — 0, because
/// the handoff zeroes the outgoing stream moments earlier. Net result: the whole
/// next track "plays but no audio" until the user forces a restart. The reassert
/// loop is the safety net: for a short window after every track start it rewrites
/// the target whenever the player's readable volume disagrees with it.
/// </summary>
public class VlcVolumeReassertTests
{
    private const int WindowMs = 200;
    private const int TickMs = 50; // → 4 ticks

    private sealed class Player
    {
        public int Volume;
        public readonly List<int> Writes = new();
        public int Reads;
        public int Sleeps;

        public int Read() { Reads++; return Volume; }
        public void Write(int v) { Writes.Add(v); Volume = v; }
        public void Sleep() => Sleeps++;
    }

    [Fact]
    public void Reassert_RewritesALostOpenVolume()
    {
        // The f7uh report: the un-park write was swallowed inside libvlc and the
        // pulse stream was born at stream-restore's remembered 0. The first tick
        // that sees the mismatch must put the target back.
        var p = new Player { Volume = 0 };

        VlcAudioPlayer.RunPlayerVolumeReassert(
            WindowMs, TickMs, () => true, () => false, () => 80, p.Read, p.Write, p.Sleep);

        Assert.Equal(80, p.Volume);
        Assert.Equal(new[] { 80 }, p.Writes.Distinct());
    }

    [Fact]
    public void Reassert_CorrectsALateClobber()
    {
        // The stream can connect (and inherit the wrong level) ticks after the
        // loop started while the readback still echoed the cached target — the
        // loop must keep watching, not stop at first agreement.
        var p = new Player { Volume = 80 };
        var tick = 0;
        bool Continue()
        {
            if (++tick == 3) p.Volume = 0; // restore-level imposed at connect
            return true;
        }

        VlcAudioPlayer.RunPlayerVolumeReassert(
            WindowMs, TickMs, Continue, () => false, () => 80, p.Read, p.Write, p.Sleep);

        Assert.Equal(80, p.Volume);
        Assert.Equal(new[] { 80 }, p.Writes);
    }

    [Fact]
    public void Reassert_LeavesAConvergedPlayerAlone()
    {
        var p = new Player { Volume = 80 };

        VlcAudioPlayer.RunPlayerVolumeReassert(
            WindowMs, TickMs, () => true, () => false, () => 80, p.Read, p.Write, p.Sleep);

        Assert.Empty(p.Writes);
    }

    [Fact]
    public void Reassert_StopsWhenTheSessionChanges()
    {
        // A skip mid-window starts a new session with its own reassert; the old
        // loop must exit instead of writing a stale target at the new track.
        var p = new Player { Volume = 0 };
        var ticks = 0;

        VlcAudioPlayer.RunPlayerVolumeReassert(
            WindowMs, TickMs, () => ++ticks <= 2, () => false, () => 80, p.Read, p.Write, p.Sleep);

        Assert.Equal(new[] { 80 }, p.Writes);
        Assert.Equal(2, p.Sleeps); // exited on the session check, not the full window
    }

    [Fact]
    public void Reassert_SkipsTicksWhileTheSliderRampIsGliding()
    {
        // The slider ramp glides _player.Volume through intermediate values; a
        // concurrent rewrite of the final target would make the glide jump. Ticks
        // hold off while the ramp is busy — both converge on the same target.
        var p = new Player { Volume = 0 };

        VlcAudioPlayer.RunPlayerVolumeReassert(
            WindowMs, TickMs, () => true, () => true, () => 80, p.Read, p.Write, p.Sleep);

        Assert.Equal(0, p.Reads);
        Assert.Empty(p.Writes);
        Assert.Equal(WindowMs / TickMs, p.Sleeps);
    }

    [Fact]
    public void Reassert_ChasesTheCurrentTargetNotTheCapturedOne()
    {
        // The user can move the volume slider inside the window; the loop must
        // re-read the target each tick or it would undo the user's change.
        var p = new Player { Volume = 0 };
        var target = 80;
        var tick = 0;
        bool Continue()
        {
            if (++tick == 3) { target = 60; p.Volume = 0; }
            return true;
        }

        VlcAudioPlayer.RunPlayerVolumeReassert(
            WindowMs, TickMs, Continue, () => false, () => target, p.Read, p.Write, p.Sleep);

        Assert.Equal(60, p.Volume);
        Assert.Equal(new[] { 80, 60 }, p.Writes);
    }
}
