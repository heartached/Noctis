using System;
using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Field canary (2026-08-13) reported 720/960 samples per read left UNWRITTEN
// by the provider even at idle — which the pad path should make impossible.
// Reproduce the exact call shape in isolation to find the leaking branch.
public class GaplessGapCanaryTests
{
    private const float Sentinel = 3.0e-38f;

    private static int Leaked(GaplessSpliceProvider provider, int count)
    {
        var buf = new float[count];
        for (var i = 0; i < count; i++) buf[i] = Sentinel;
        var n = provider.Read(buf, 0, count);
        Assert.Equal(count, n);
        return buf.Count(v => v == Sentinel);
    }

    [Fact]
    public void IdleProvider_FillsEverySample_ThroughNAudioWaveBufferPun()
    {
        // The field render buffer is NAudio's WaveBuffer pun: a byte[] whose
        // reference is reinterpreted as float[]. Element writes work, but
        // Array.Clear uses the array's RUNTIME type (byte[]) and clears N BYTES
        // instead of N floats — so the silence pad only zeroed a quarter of its
        // region and the rest replayed stale device-buffer audio: the buzz.
        var provider = new GaplessSpliceProvider(48000, 2, startThresholdMs: 200, startFadeMs: 5);
        var bytes = new byte[960 * 4];
        var punned = new NAudio.Wave.WaveBuffer(bytes).FloatBuffer;
        for (var i = 0; i < 960; i++) punned[i] = Sentinel;

        var n = provider.Read(punned, 0, 960);

        Assert.Equal(960, n);
        for (var i = 0; i < 960; i++)
            Assert.True(punned[i] != Sentinel, $"sample {i} left unwritten (stale in the field)");
    }

    [Fact]
    public void IdleProvider_WritesEveryRequestedSample()
    {
        var provider = new GaplessSpliceProvider(48000, 2, startThresholdMs: 200, startFadeMs: 5);
        for (var r = 0; r < 5; r++)
            Assert.Equal(0, Leaked(provider, 960));
    }

    [Fact]
    public void PlayingProvider_WritesEveryRequestedSample()
    {
        var provider = new GaplessSpliceProvider(48000, 2, startThresholdMs: 200, startFadeMs: 5);
        var seg = new GaplessTrackSegment(48000, 2, source: null, capacitySeconds: 20);
        provider.Enqueue(seg);
        seg.Write(Enumerable.Repeat((short)12000, 48000).ToArray());
        for (var r = 0; r < 20; r++)
            Assert.Equal(0, Leaked(provider, 960));
        seg.Flush(1000);                                              // seek cut
        Assert.Equal(0, Leaked(provider, 960));                       // gate closed: pad
        seg.Write(Enumerable.Repeat((short)-12000, 48000).ToArray()); // fast refill
        for (var r = 0; r < 20; r++)
            Assert.Equal(0, Leaked(provider, 960));
    }
}
