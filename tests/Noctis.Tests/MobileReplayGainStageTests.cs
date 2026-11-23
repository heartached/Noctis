using Xunit;
using Noctis.Mobile.Services;

namespace Noctis.Tests;

/// <summary>
/// The phone's ReplayGain gain stage (<see cref="ReplayGainStage"/>, run by Media3AudioPlayer's
/// audio processor on synthetic buffers here): unity, a cut, a boost through the soft limiter, the
/// next song's level from its first sample at a gapless switch (not when it was prepared), a
/// settings change gliding instead of jumping, and no allocation per buffer.
/// </summary>
public class MobileReplayGainStageTests
{
    private const int Rate = 48000;

    private static ReplayGainStage Stage(int channels = 2)
    {
        var stage = new ReplayGainStage();
        stage.Configure(Rate, channels);
        return stage;
    }

    private static float[] Constant(int frames, float value, int channels = 2) =>
        Enumerable.Repeat(value, frames * channels).ToArray();

    private static int Frames(int ms) => Rate * ms / 1000;

    [Fact]
    public void Unity_LeavesTheSamplesAlone()
    {
        var stage = Stage();
        stage.SetGain("a", 1.0);
        stage.BeginStream("a");
        Assert.False(stage.Update());   // nothing to do: the processor copies the buffer as it is
        var samples = new[] { 0.5f, -0.25f, 0.999f, -1f };
        stage.Process(samples.AsSpan());
        Assert.Equal(new[] { 0.5f, -0.25f, 0.999f, -1f }, samples);
    }

    [Fact]
    public void Cut_ScalesEverySample_AfterTheShortStartGlide()
    {
        var stage = Stage();
        stage.SetGain("a", 0.5);
        stage.BeginStream("a");
        Assert.True(stage.Update());
        var buffer = Constant(Frames(20), 0.8f);
        stage.Process(buffer.AsSpan());
        // From 5 ms on (the start glide), exactly half; never past it on the way.
        Assert.All(buffer.Skip(Frames(ReplayGainStage.BoundaryRampMs) * 2), s => Assert.Equal(0.4f, s, 5));
        Assert.All(buffer, s => Assert.InRange(s, 0.4f - 1e-5f, 0.8f + 1e-5f));
    }

    [Fact]
    public void Int16_IsScaledToo()
    {
        var stage = Stage(1);
        stage.SetGain("a", 0.5);
        stage.BeginStream("a");
        stage.Update();
        var shorts = Enumerable.Repeat((short)20000, Frames(20)).ToArray();
        stage.Process(shorts.AsSpan());
        Assert.Equal(10000, shorts[^1]);
    }

    [Fact]
    public void Boost_RaisesQuietSamples_AndTheLimiterKeepsPeaksBelowFullScale()
    {
        var stage = Stage(1);
        stage.SetGain("a", Math.Pow(10, 6 / 20.0));   // +6 dB
        stage.BeginStream("a");
        stage.Update();
        var warmup = Constant(Frames(10), 0f, 1);
        stage.Process(warmup.AsSpan());

        var quiet = Constant(Frames(5), 0.1f, 1);
        stage.Process(quiet.AsSpan());
        Assert.Equal(0.19953f, quiet[^1], 4);          // below the knee: a clean +6 dB

        var loud = new[] { 0.48f, 0.5f, 1f, -1f };
        stage.Process(loud.AsSpan());
        Assert.All(loud, s => Assert.InRange(Math.Abs(s), ReplayGainStage.LimiterThreshold, 1f));
        Assert.True(loud[1] < 0.99f);                  // +6 dB on 0.5 would be 0.998: bent under it
        Assert.True(loud[1] > loud[0]);                // still monotonic: louder in, louder out
        Assert.Equal(-loud[2], loud[3], 6);            // symmetric
    }

    [Fact]
    public void Boost_Int16_NeverWraps()
    {
        var stage = Stage(1);
        stage.SetGain("a", 4.0);                       // +12 dB, the most a total can be
        stage.BeginStream("a");
        stage.Update();
        var shorts = Enumerable.Repeat(short.MaxValue, Frames(10)).Concat(Enumerable.Repeat(short.MinValue, Frames(10))).ToArray();
        stage.Process(shorts.AsSpan());
        Assert.True(shorts[Frames(9)] > 29000);
        Assert.True(shorts[^1] < -29000);
    }

    [Fact]
    public void Cut_NeverEngagesTheLimiter()
    {
        var stage = Stage(1);
        stage.SetGain("a", 0.99);
        stage.BeginStream("a");
        stage.Update();
        var full = Constant(Frames(10), 1f, 1);
        stage.Process(full.AsSpan());
        Assert.Equal(0.99f, full[^1], 5);              // a loud master just turned down, not bent
    }

    [Fact]
    public void GaplessSwitch_TakesTheNextSongsLevel_AtItsFirstSample_NotWhenItWasPrepared()
    {
        var stage = Stage(1);
        stage.SetGain("a", 0.5);
        stage.BeginStream("a");
        stage.Update();
        stage.Process(Constant(Frames(20), 1f, 1).AsSpan());

        // The next song is prepared (its level known) while "a" still plays: "a" keeps its level.
        stage.SetGain("b", 0.25);
        stage.Update();
        Assert.Equal(0.5f, stage.TargetGain);
        var stillA = Constant(Frames(20), 1f, 1);
        stage.Process(stillA.AsSpan());
        Assert.All(stillA, s => Assert.Equal(0.5f, s, 5));

        // The sink reaches "b"'s first sample: its level from there, after a 5 ms glide.
        stage.BeginStream("b");
        stage.Update();
        var b = Constant(Frames(20), 1f, 1);
        stage.Process(b.AsSpan());
        Assert.True(b[0] < 0.5f && b[0] > 0.25f);      // the glide starts at the boundary
        Assert.Equal(0.25f, b[Frames(ReplayGainStage.BoundaryRampMs)], 5);
        Assert.All(b.Skip(Frames(ReplayGainStage.BoundaryRampMs)), s => Assert.Equal(0.25f, s, 5));
        Assert.Equal("b", stage.MediaId);
    }

    [Fact]
    public void GaplessSwitch_BetweenEqualLevels_IsSeamless()
    {
        var stage = Stage(1);
        stage.SetGain("a", 0.7);
        stage.SetGain("b", 0.7);   // one album in Album mode
        stage.BeginStream("a");
        stage.Update();
        stage.Process(Constant(Frames(20), 1f, 1).AsSpan());
        stage.BeginStream("b");
        stage.Update();
        var b = Constant(Frames(5), 1f, 1);
        stage.Process(b.AsSpan());
        Assert.All(b, s => Assert.Equal(0.7f, s, 5));
    }

    [Fact]
    public void SettingsChange_Glides_NoJump()
    {
        var stage = Stage(1);
        stage.SetGain("a", 1.0);
        stage.BeginStream("a");
        stage.Update();
        stage.Process(Constant(Frames(20), 1f, 1).AsSpan());

        stage.SetGain("a", 0.5);
        stage.Refresh();
        Assert.True(stage.Update());
        var ramp = Constant(Frames(ReplayGainStage.SettingsRampMs + 10), 1f, 1);
        stage.Process(ramp.AsSpan());
        var biggestStep = ramp.Zip(ramp.Skip(1), (x, y) => Math.Abs(x - y)).Max();
        Assert.True(biggestStep < 0.001f, $"step {biggestStep}");
        Assert.Equal(0.5f, ramp[^1], 5);
        Assert.True(ramp[Frames(ReplayGainStage.SettingsRampMs) / 2] is > 0.6f and < 0.9f);
    }

    [Fact]
    public void Seek_SameSong_KeepsTheLevel()
    {
        var stage = Stage(1);
        stage.SetGain("a", 0.5);
        stage.BeginStream("a");
        stage.Update();
        stage.Process(Constant(Frames(20), 1f, 1).AsSpan());
        stage.BeginStream("a");   // a seek flushes the sink with the same item
        stage.Update();
        var after = Constant(Frames(2), 1f, 1);
        stage.Process(after.AsSpan());
        Assert.All(after, s => Assert.Equal(0.5f, s, 5));
    }

    [Fact]
    public void UnknownStreamItem_FollowsWhatThePlayerAnnounces()
    {
        var stage = Stage(1);
        stage.SetGain("a", 0.5);
        stage.SetGain("b", 0.25);
        stage.Announce("a");
        stage.BeginStream(null);   // the sink could not say whose stream this is
        stage.Update();
        stage.Process(Constant(Frames(20), 1f, 1).AsSpan());
        Assert.Equal(0.5f, stage.Gain, 5);
        stage.Announce("b");       // the player's own transition
        stage.Update();
        stage.Process(Constant(Frames(ReplayGainStage.SettingsRampMs + 5), 1f, 1).AsSpan());
        Assert.Equal(0.25f, stage.Gain, 5);
    }

    [Fact]
    public void AResetFlush_ThenTheStreamsOwnFlush_LandsOnTheNamedSong()
    {
        // Device log 11-23: a re-prepare resets the sink, whose processors are flushed with no
        // period first (StreamMetadata.DEFAULT); the stream's own flush naming the song follows.
        var stage = Stage(1);
        stage.SetGain("a", 0.5);
        stage.SetGain("b", 0.25);
        stage.Announce("a");
        stage.BeginStream(null);
        Assert.True(stage.FollowsPlayer);
        stage.BeginStream("b");
        Assert.False(stage.FollowsPlayer);
        stage.Update();
        var b = Constant(Frames(10), 1f, 1);
        stage.Process(b.AsSpan());
        Assert.Equal("b", stage.MediaId);
        Assert.Equal(0.25f, b[Frames(ReplayGainStage.BoundaryRampMs)], 5);
        stage.Announce("a");   // a late player report never pulls a named stream back
        stage.Update();
        Assert.Equal(0.25f, stage.TargetGain);
    }

    [Fact]
    public void FollowingThePlayer_SwitchesWithTheShortGlide()
    {
        var stage = Stage(1);
        stage.SetGain("a", 1.0);
        stage.SetGain("b", 0.5);
        stage.Announce("a");
        stage.BeginStream(null);
        stage.Update();
        stage.Process(Constant(Frames(10), 1f, 1).AsSpan());
        stage.Announce("b");
        stage.Update();
        var after = Constant(Frames(ReplayGainStage.BoundaryRampMs) + 1, 1f, 1);
        stage.Process(after.AsSpan());
        Assert.Equal(0.5f, after[^1], 5);
    }

    [Fact]
    public void Labels_NameTheSongInTheLog_AndGoWithIt()
    {
        var stage = Stage(1);
        stage.SetGain("content://x/1", 0.5, "La Santa");
        Assert.Equal("La Santa", stage.LabelFor("content://x/1"));
        stage.SetGain("content://x/1", 0.4);   // a refresh keeps the name
        Assert.Equal("La Santa", stage.LabelFor("content://x/1"));
        Assert.Equal("content://x/2", stage.LabelFor("content://x/2"));
        stage.Retain(Array.Empty<string>());
        Assert.Equal("content://x/1", stage.LabelFor("content://x/1"));
    }

    [Fact]
    public void Retain_DropsSongsNoLongerQueued()
    {
        var stage = Stage(1);
        stage.SetGain("a", 0.5);
        stage.SetGain("b", 0.25);
        stage.Retain(new[] { "b" });
        Assert.Equal(1f, stage.GainFor("a"));
        Assert.Equal(0.25f, stage.GainFor("b"));
        Assert.Equal(1f, stage.GainFor(null));
    }

    [Fact]
    public void Processing_AllocatesNothingPerBuffer()
    {
        var stage = Stage();
        stage.SetGain("a", 2.0);
        stage.BeginStream("a");
        var floats = Constant(1024, 0.6f);
        var shorts = new short[2048];
        stage.Update();
        stage.Process(floats.AsSpan());
        stage.Process(shorts.AsSpan());
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            stage.Update();
            stage.Process(floats.AsSpan());
            stage.Process(shorts.AsSpan());
        }
        stage.Refresh();
        stage.Update();
        stage.Process(floats.AsSpan());
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void FormatChange_KeepsTheLevel_AndRampsInTheNewRate()
    {
        var stage = Stage(2);
        stage.SetGain("a", 0.5);
        stage.BeginStream("a");
        stage.Update();
        stage.Process(Constant(Frames(20), 1f).AsSpan());
        stage.Configure(Rate, 1);
        var mono = Constant(4, 1f, 1);
        stage.Process(mono.AsSpan());
        Assert.All(mono, s => Assert.Equal(0.5f, s, 5));
    }
}
