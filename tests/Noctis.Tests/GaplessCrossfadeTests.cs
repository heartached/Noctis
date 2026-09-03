using System;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Discord (roge, 08-19): with Crossfade on, the last N seconds of a playlist track
// were cut and the next track started dry. Under the Windows splice engine the
// queue advanced early (as designed) but the outgoing segment was simply
// abandoned — the engine had no way to blend two segments. These lock the
// mixed crossfade the provider now renders: outgoing × fade-out + incoming ×
// fade-in across the fade length, then the tail is dropped.
public class GaplessCrossfadeTests
{
    private static short[] ConstantBlock(short value, int samples) =>
        Enumerable.Repeat(value, samples).ToArray();

    private static (GaplessSpliceProvider provider, GaplessTrackSegment a, GaplessTrackSegment b) Stage()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        var b = new GaplessTrackSegment(8000, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 2000)));   // ≈ +0.5f, still live (no EOS)
        Assert.True(b.Write(ConstantBlock(-16384, 2000)));  // ≈ -0.5f, staged
        var warm = new float[100];
        provider.Read(warm, 0, 100);                         // A is audible
        Assert.All(warm, s => Assert.True(s > 0.4f));
        return (provider, a, b);
    }

    [Fact]
    public void BeginCrossfade_BlendsOutgoingIntoIncoming_ThenDropsTheTail()
    {
        var (provider, a, b) = Stage();

        // 50 ms at 8 kHz mono = 400 samples of fade.
        Assert.True(provider.BeginCrossfade(50, AutoMixFadeCurve.EqualPower));
        Assert.Same(b, provider.ActiveSegment);

        var mix = new float[400];
        provider.Read(mix, 0, 400);

        // Start: all A. Middle: equal-power crossover cancels (+0.5·cos45° − 0.5·sin45° ≈ 0).
        // End: all B. No step anywhere — the blend is continuous.
        Assert.True(mix[0] > 0.45f, $"start was {mix[0]}");
        Assert.True(Math.Abs(mix[200]) < 0.05f, $"midpoint was {mix[200]}");
        Assert.True(mix[399] < -0.45f, $"end was {mix[399]}");
        for (var i = 1; i < mix.Length; i++)
            Assert.True(Math.Abs(mix[i] - mix[i - 1]) < 0.02f, $"step of {Math.Abs(mix[i] - mix[i - 1]):F3} at {i}");

        // Fade complete: the outgoing tail is abandoned and only B renders.
        Assert.True(a.Abandoned);
        var after = new float[100];
        provider.Read(after, 0, 100);
        Assert.All(after, s => Assert.True(s < -0.45f, $"post-fade sample was {s}"));
    }

    [Fact]
    public void BeginCrossfade_WithoutStagedNext_ReturnsFalse_AndKeepsPlaying()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        provider.Enqueue(a);
        Assert.True(a.Write(ConstantBlock(16384, 500)));
        var buffer = new float[100];
        provider.Read(buffer, 0, 100);

        Assert.False(provider.BeginCrossfade(50, AutoMixFadeCurve.EqualPower));
        Assert.False(a.Abandoned);
        provider.Read(buffer, 0, 100);
        Assert.All(buffer, s => Assert.True(s > 0.4f));
    }

    [Fact]
    public void Clear_DuringCrossfade_AbandonsBothAndFallsSilent()
    {
        var (provider, a, b) = Stage();
        Assert.True(provider.BeginCrossfade(100, AutoMixFadeCurve.SmoothEase));
        var mix = new float[200];
        provider.Read(mix, 0, 200);

        provider.Clear();
        Assert.True(a.Abandoned);
        Assert.True(b.Abandoned);
        var after = new float[200];
        provider.Read(after, 0, 200);
        // Declick ramp aside, the buffer settles at silence.
        Assert.True(Math.Abs(after[199]) < 0.001f, $"expected silence, got {after[199]}");
    }

    [Fact]
    public void Crossfade_OutgoingRunsDry_PadsTheTailWithSilence()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        var b = new GaplessTrackSegment(8000, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 150)));    // only 50 samples left after warm-up
        a.MarkEndOfStream();
        Assert.True(b.Write(ConstantBlock(-16384, 2000)));
        var warm = new float[100];
        provider.Read(warm, 0, 100);

        Assert.True(provider.BeginCrossfade(50, AutoMixFadeCurve.EqualPower)); // 400-sample fade
        var mix = new float[400];
        provider.Read(mix, 0, 400);

        // After A's 50 remaining samples the tail contributes nothing; B keeps fading in.
        Assert.True(mix[0] > 0.45f);
        Assert.True(mix[399] < -0.45f, $"end was {mix[399]}");
        Assert.True(mix[300] < -0.3f, $"late fade was {mix[300]}");
        var after = new float[50];
        provider.Read(after, 0, 50);
        Assert.All(after, s => Assert.True(s < -0.45f));
    }

    [Fact]
    public void BeginCrossfade_WhileOneIsInFlight_DropsTheOlderTail()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        var b = new GaplessTrackSegment(8000, 1, source: 1);
        var c = new GaplessTrackSegment(8000, 1, source: 0);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 2000)));
        Assert.True(b.Write(ConstantBlock(-16384, 2000)));
        var buffer = new float[100];
        provider.Read(buffer, 0, 100);

        Assert.True(provider.BeginCrossfade(500, AutoMixFadeCurve.EqualPower));
        provider.Read(buffer, 0, 100);
        provider.Enqueue(c);
        Assert.True(c.Write(ConstantBlock(8192, 2000)));    // ≈ +0.25f
        Assert.True(provider.BeginCrossfade(50, AutoMixFadeCurve.EqualPower));

        Assert.True(a.Abandoned);
        Assert.Same(c, provider.ActiveSegment);
        var mix = new float[400];
        provider.Read(mix, 0, 400);
        Assert.True(mix[399] > 0.2f, $"expected C after the fade, got {mix[399]}");
        Assert.True(b.Abandoned);
    }
}
