using System;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// A22: releasing the prepared next track abandoned whatever segment sat in the
// standby slot. After a splice that is the OUTGOING player's audible tail, so a
// queue edit, shuffle/repeat toggle, pause, seek or settings change during an
// engine crossfade dropped the old track in one read and snapped the new one to
// full level. Only a staged segment (or a standby PrepareNext reuses) may go.
public class GaplessStandbyReleaseTests
{
    private static short[] ConstantBlock(short value, int samples) =>
        Enumerable.Repeat(value, samples).ToArray();

    // Outgoing A (≈ +0.5f, live) crossfading into B (≈ -0.5f): 800-sample fade,
    // 200 samples in. A is what the standby slot holds after the splice.
    private static (GaplessSpliceProvider provider, GaplessTrackSegment a, float[] mix) MidCrossfade()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        var b = new GaplessTrackSegment(8000, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 2000)));
        Assert.True(b.Write(ConstantBlock(-16384, 2000)));
        provider.Read(new float[100], 0, 100);
        Assert.True(provider.BeginCrossfade(100, AutoMixFadeCurve.EqualPower));
        var mix = new float[200];
        provider.Read(mix, 0, 200);
        return (provider, a, mix);
    }

    [Fact]
    public void CrossfadeTail_IsKept_AndTheBlendCarriesOn()
    {
        var (provider, a, mix) = MidCrossfade();

        Assert.False(VlcAudioPlayer.EngineStandbyReleasable(a, standbyPrepared: false, reuseStandby: false, out var abandon));
        Assert.False(abandon);
        Assert.False(a.Abandoned);

        var next = new float[200];
        provider.Read(next, 0, 200);
        Assert.True(provider.IsCrossfading);
        Assert.True(Math.Abs(next[0] - mix[199]) < 0.02f, $"blend stepped: {mix[199]} -> {next[0]}");
        for (var i = 1; i < next.Length; i++)
            Assert.True(Math.Abs(next[i] - next[i - 1]) < 0.02f, $"step of {Math.Abs(next[i] - next[i - 1]):F3} at {i}");
    }

    [Fact]
    public void AbandonedCrossfadeTail_SnapsTheIncomingToFullLevel()
    {
        // The failure the release used to cause: the tail vanishes in one read and
        // the incoming jumps from its fade-in gain straight to 1.0.
        var (provider, a, mix) = MidCrossfade();
        a.Abandon();

        var next = new float[200];
        provider.Read(next, 0, 200);
        Assert.False(provider.IsCrossfading);
        Assert.True(Math.Abs(next[0] - mix[199]) > 0.3f, $"expected a snap, got {mix[199]} -> {next[0]}");
        provider.Read(next, 0, 200);
        Assert.All(next, s => Assert.True(s < -0.45f, $"incoming sample was {s}"));
    }

    [Fact]
    public void PauseResume_MidCrossfade_FadesTheKeptTailBackIn()
    {
        // With the tail kept across a pause, the un-park must ramp it in with the
        // incoming: it resumed at full outgoing level straight out of silence.
        var provider = new GaplessSpliceProvider(8000, 1, startThresholdMs: 0, startFadeMs: 5); // 40-sample ramp
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        var b = new GaplessTrackSegment(8000, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 4000)));
        Assert.True(b.Write(ConstantBlock(-16384, 4000)));
        provider.Read(new float[200], 0, 200);
        Assert.True(provider.BeginCrossfade(500, AutoMixFadeCurve.EqualPower)); // 4000-sample fade
        provider.Read(new float[200], 0, 200);

        provider.Parked = true;
        var paused = new float[200];
        provider.Read(paused, 0, 200);
        Assert.Equal(0f, paused[199]);

        provider.Parked = false;
        var resumed = new float[200];
        provider.Read(resumed, 0, 200);
        Assert.True(provider.IsCrossfading);
        Assert.True(Math.Abs(resumed[0]) < 0.05f, $"resume stepped out of silence to {resumed[0]}");
        for (var i = 1; i < resumed.Length; i++)
            Assert.True(Math.Abs(resumed[i] - resumed[i - 1]) < 0.05f,
                $"step of {Math.Abs(resumed[i] - resumed[i - 1]):F3} at sample {i}");
        Assert.True(resumed[199] > 0.3f, $"expected the blend back near +0.4, got {resumed[199]}");
    }

    [Fact]
    public void DrainedTail_IsKept_UntilItHasPlayedOut()
    {
        // Plain gapless boundary: the outgoing input is at EOF with its last
        // moment still in the ring while the staged track waits behind it.
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        var b = new GaplessTrackSegment(8000, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 600)));
        a.MarkEndOfStream();
        Assert.True(b.Write(ConstantBlock(-16384, 2000)));
        provider.Read(new float[100], 0, 100);

        Assert.False(VlcAudioPlayer.EngineStandbyReleasable(a, standbyPrepared: false, reuseStandby: false, out _));
        var tail = new float[500];
        provider.Read(tail, 0, 500);
        Assert.All(tail, s => Assert.True(s > 0.45f, $"tail sample was {s}"));

        Assert.True(a.IsFinished);
        Assert.True(VlcAudioPlayer.EngineStandbyReleasable(a, standbyPrepared: false, reuseStandby: false, out var abandon));
        Assert.False(abandon);
    }

    [Fact]
    public void StagedSegment_IsAbandoned_EvenWhenFullyDecoded()
    {
        var staged = new GaplessTrackSegment(8000, 1, source: 1);
        Assert.True(staged.Write(ConstantBlock(8192, 400)));
        staged.MarkEndOfStream(); // short track decoded whole while staged

        Assert.True(VlcAudioPlayer.EngineStandbyReleasable(staged, standbyPrepared: true, reuseStandby: false, out var abandon));
        Assert.True(abandon);
    }

    [Fact]
    public void ReusedStandby_AbandonsALiveTail_ButLetsADrainedOnePlayOut()
    {
        var live = new GaplessTrackSegment(8000, 1, source: 0);
        Assert.True(live.Write(ConstantBlock(8192, 400)));
        Assert.True(VlcAudioPlayer.EngineStandbyReleasable(live, standbyPrepared: false, reuseStandby: true, out var abandonLive));
        Assert.True(abandonLive);

        var drained = new GaplessTrackSegment(8000, 1, source: 0);
        Assert.True(drained.Write(ConstantBlock(8192, 400)));
        drained.MarkEndOfStream();
        Assert.True(VlcAudioPlayer.EngineStandbyReleasable(drained, standbyPrepared: false, reuseStandby: true, out var abandonDrained));
        Assert.False(abandonDrained);
    }

    [Fact]
    public void ClearedOrEmptySlot_IsReleased()
    {
        // Stop()/a fresh play clear the engine first: every segment is abandoned,
        // so the release goes on to stop the standby as before.
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        provider.Enqueue(a);
        Assert.True(a.Write(ConstantBlock(8192, 400)));
        provider.Clear();

        Assert.True(VlcAudioPlayer.EngineStandbyReleasable(a, standbyPrepared: false, reuseStandby: false, out _));
        Assert.True(VlcAudioPlayer.EngineStandbyReleasable(null, standbyPrepared: false, reuseStandby: false, out var abandon));
        Assert.False(abandon);
    }
}
