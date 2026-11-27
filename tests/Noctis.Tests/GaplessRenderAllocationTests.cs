using System;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-27 "weird audio cut off": the render thread is a managed thread, so every GC
/// suspends it. Whatever it allocates per device period feeds the gen0 budget the
/// app's other threads also spend. These pin the render chain GaplessSink builds
/// (splice, channel mix, mute gate, beat tap, output level, then the byte adapter the
/// WasapiOut reads) to zero managed allocations per read while music plays.
/// </summary>
public class GaplessRenderAllocationTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private readonly ITestOutputHelper _out;

    public GaplessRenderAllocationTests(ITestOutputHelper output) => _out = output;

    private static (GaplessSpliceProvider Provider, GaplessTrackSegment Segment) PlayingProvider()
    {
        var provider = new GaplessSpliceProvider(Rate, Channels, startThresholdMs: 0, startFadeMs: 5);
        var seg = new GaplessTrackSegment(Rate, Channels, source: null, capacitySeconds: 30);
        provider.Enqueue(seg);
        var block = Enumerable.Range(0, Rate * Channels * 20).Select(i => (short)((i % 200) * 100 - 10000)).ToArray();
        Assert.True(seg.Write(block));
        return (provider, seg);
    }

    private static ISampleProvider RenderChain(ISampleProvider provider)
    {
        // Same order as GaplessSink's constructor (the tap and the stall probe aside).
        ISampleProvider chain = new ChannelMixProvider(provider, new ChannelMixSettings());
        chain = new MuteGateProvider(chain);
        chain = new BeatTapProvider(chain, GaplessSink.OutputLatencyMs);
        return new OutputLevelProvider(chain);
    }

    [Fact]
    public void SampleChain_DoesNotAllocate_PerRead()
    {
        var (provider, _) = PlayingProvider();
        var chain = RenderChain(provider);
        var buf = new float[480 * Channels]; // one 10 ms WASAPI period
        for (var i = 0; i < 50; i++) chain.Read(buf, 0, buf.Length); // JIT warm-up, start fade

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++) chain.Read(buf, 0, buf.Length);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"sample chain: {allocated} bytes over 500 reads");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ByteAdapter_TheWasapiOutReads_DoesNotAllocate_PerRead()
    {
        var (provider, _) = PlayingProvider();
        var adapter = GaplessSink.ToWaveProvider(RenderChain(provider));
        var bytes = new byte[480 * Channels * 4];
        for (var i = 0; i < 50; i++) adapter.Read(bytes, 0, bytes.Length);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++) adapter.Read(bytes, 0, bytes.Length);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"byte adapter + chain: {allocated} bytes over 500 reads");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ByteAdapter_ReadsTheSameSamples_AsNAudiosSampleToWaveProvider()
    {
        var (a, _) = PlayingProvider();
        var (b, _) = PlayingProvider();
        var ours = GaplessSink.ToWaveProvider(a);
        var naudio = new SampleToWaveProvider(b);
        Assert.Equal(naudio.WaveFormat, ours.WaveFormat);
        var x = new byte[3840 + 8];
        var y = new byte[3840 + 8];
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(naudio.Read(y, 8, 3840), ours.Read(x, 8, 3840));
            Assert.Equal(y, x);
        }
    }
}
