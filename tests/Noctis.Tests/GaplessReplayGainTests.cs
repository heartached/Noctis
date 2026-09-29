using System;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// A18: on the splice engine ReplayGain was one OS-session level, re-read for the
// incoming track when the queue advanced (~0.5 s before the audible boundary) —
// so the outgoing's last stretch, or its whole crossfade tail, played at the
// incoming track's level. The attenuation now renders on each segment: these
// lock that each track keeps its own level exactly up to the seam and through a
// crossfade, and that a setting change mid-play slews instead of stepping.
public class GaplessReplayGainTests
{
    private static short[] ConstantBlock(short value, int samples) =>
        Enumerable.Repeat(value, samples).ToArray();

    [Fact]
    public void Splice_EachSegmentKeepsItsOwnGain_ExactlyUpToTheSeam()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0) { Gain = 0.5f };
        var b = new GaplessTrackSegment(8000, 1, source: 1) { Gain = 0.25f };
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 100)));  // +0.5f
        a.MarkEndOfStream();
        Assert.True(b.Write(ConstantBlock(16384, 100)));  // +0.5f
        b.MarkEndOfStream();

        var buffer = new float[200];
        provider.Read(buffer, 0, 200);

        // A's last sample is still at A's level, B's first already at B's: the
        // level change lands on the boundary sample, not before it.
        Assert.All(buffer.Take(100), s => Assert.Equal(0.25f, s, 5));
        Assert.All(buffer.Skip(100), s => Assert.Equal(0.125f, s, 5));
    }

    [Fact]
    public void Crossfade_OutgoingTailKeepsItsOwnGain()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0) { Gain = 0.5f };
        var b = new GaplessTrackSegment(8000, 1, source: 1) { Gain = 1f };
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(ConstantBlock(16384, 2000)));   // +0.5f, still live
        Assert.True(b.Write(ConstantBlock(-16384, 2000)));  // -0.5f, staged
        var warm = new float[100];
        provider.Read(warm, 0, 100);
        Assert.All(warm, s => Assert.Equal(0.25f, s, 5));

        // 50 ms at 8 kHz mono = 400 samples of fade.
        Assert.True(provider.BeginCrossfade(50, AutoMixFadeCurve.EqualPower));
        var mix = new float[400];
        provider.Read(mix, 0, 400);

        for (var i = 0; i < mix.Length; i++)
        {
            var (outGain, inGain) = AutoMixFadeMath.GetFadeFactors(i / 400.0, AutoMixFadeCurve.EqualPower);
            var expected = (float)(0.25 * outGain + -0.5 * inGain);
            Assert.True(Math.Abs(mix[i] - expected) < 0.002f, $"sample {i} was {mix[i]}, expected {expected}");
        }
    }

    [Fact]
    public void Gain_ChangedMidPlay_SlewsInsteadOfStepping()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0);
        provider.Enqueue(a);
        Assert.True(a.Write(ConstantBlock(16384, 1000)));  // +0.5f
        var before = new float[100];
        provider.Read(before, 0, 100);
        Assert.All(before, s => Assert.Equal(0.5f, s, 5));

        a.Gain = 0.5f;
        var after = new float[400];
        provider.Read(after, 0, 400);

        Assert.True(after[0] > 0.49f, $"first sample after the change was {after[0]}");
        for (var i = 1; i < after.Length; i++)
            Assert.True(Math.Abs(after[i] - after[i - 1]) < 0.01f, $"step of {Math.Abs(after[i] - after[i - 1]):F4} at {i}");
        Assert.Equal(0.25f, after[^1], 5);
    }

    [Fact]
    public void UnityGain_IsBitExact()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0) { Gain = 1f };
        provider.Enqueue(a);
        var pcm = Enumerable.Range(0, 200).Select(i => (short)(i * 97 - 9000)).ToArray();
        Assert.True(a.Write(pcm));
        a.MarkEndOfStream();

        var buffer = new float[200];
        provider.Read(buffer, 0, 200);

        for (var i = 0; i < pcm.Length; i++)
            Assert.Equal(pcm[i] / 32768f, buffer[i]);
    }
}
