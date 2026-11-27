using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// GitHub #139 log: GaplessEngine.RenderStall lines on Linux carried 23-45 ms GC pauses.
// The render thread is managed, so a GC suspends it whatever its priority; this pins
// that the steady-state render chain itself (splice -> mute gate -> beat tap -> level ->
// float-to-bytes, the GaplessSink order) adds no garbage that could trigger those GCs.
public class RenderChainAllocationTests
{
    private readonly ITestOutputHelper _out;
    public RenderChainAllocationTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)] // a 44.1 kHz input converted by the engine's rate stage
    public void SteadyStateRenderRead_AllocatesAlmostNothing(int sinkRate)
    {
        const int rate = 44100, channels = 2, periodMs = 10;
        var provider = new GaplessSpliceProvider(sinkRate, channels, startThresholdMs: 200, startFadeMs: 5);
        var segment = new GaplessTrackSegment(rate, channels, source: null, capacitySeconds: 30);
        provider.Enqueue(segment);
        var block = new short[rate * channels]; // 1 s of a 441 Hz tone
        for (int i = 0; i < rate; i++)
            block[2 * i] = block[2 * i + 1] = (short)(12000 * Math.Sin(2 * Math.PI * 441 * i / rate));
        for (int s = 0; s < 25; s++) Assert.True(segment.Write(block));

        ISampleProvider chain = new MuteGateProvider(provider);
        chain = new BeatTapProvider(chain, 100);
        chain = new OutputLevelProvider(chain);
        var bytes = GaplessSink.ToWaveProvider(chain); // what GaplessSink hands its WasapiOut
        var buffer = new byte[sinkRate * periodMs / 1000 * channels * 4];

        for (int i = 0; i < 100; i++) bytes.Read(buffer, 0, buffer.Length); // warm-up (JIT, first-read state)
        const int reads = 1000; // 10 s of audio
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < reads; i++) bytes.Read(buffer, 0, buffer.Length);
        var perRead = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)reads;
        _out.WriteLine($"render chain: {perRead:F1} bytes allocated per 10 ms read");

        // NAudio's SampleToWaveProvider wrapped the buffer in a WaveBuffer per call (~40 B);
        // the sink's own adapter makes that view once (11-27), so the read is garbage-free.
        Assert.True(perRead == 0, $"render chain allocated {perRead:F1} B per read");
    }
}
