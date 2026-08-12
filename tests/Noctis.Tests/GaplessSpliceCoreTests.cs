using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Headless verification of the true-gapless splice core: the track boundary
// must be crossed inside a single render read with ZERO inserted samples, and
// the transport rules (underrun vs finished, flush, abandon, backpressure)
// must hold — these are the semantics the audible seam depends on.
public class GaplessSpliceCoreTests
{
    private static short[] ConstantBlock(short value, int samples) =>
        Enumerable.Repeat(value, samples).ToArray();

    [Fact]
    public void Splice_CrossesBoundaryWithZeroInsertedSamples()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: null);
        var b = new GaplessTrackSegment(8000, 1, source: null);
        provider.Enqueue(a);
        provider.Enqueue(b);

        Assert.True(a.Write(ConstantBlock(16384, 100)));  // ≈ +0.5f
        a.MarkEndOfStream();
        Assert.True(b.Write(ConstantBlock(-16384, 100))); // ≈ -0.5f
        b.MarkEndOfStream();

        var buffer = new float[200];
        var read = provider.Read(buffer, 0, 200);

        Assert.Equal(200, read);
        // Track A occupies exactly [0,100), track B exactly [100,200): no
        // silence (0-sample) may exist anywhere across the boundary.
        Assert.All(buffer.Take(100), s => Assert.True(s > 0.4f, $"A sample was {s}"));
        Assert.All(buffer.Skip(100), s => Assert.True(s < -0.4f, $"B sample was {s}"));
    }

    [Fact]
    public void Underrun_MidTrack_PadsSilence_ButDoesNotAdvance()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: null);
        var b = new GaplessTrackSegment(8000, 1, source: null);
        provider.Enqueue(a);
        provider.Enqueue(b);

        Assert.True(a.Write(ConstantBlock(16384, 50)));
        // A is NOT end-of-stream: the gap after 50 samples is an underrun.
        Assert.True(b.Write(ConstantBlock(-16384, 50)));
        b.MarkEndOfStream();

        var buffer = new float[100];
        provider.Read(buffer, 0, 100);

        Assert.All(buffer.Take(50), s => Assert.True(s > 0.4f));
        Assert.All(buffer.Skip(50), s => Assert.Equal(0f, s)); // silence, not track B
        Assert.Same(a, provider.ActiveSegment);                 // still on A
    }

    [Fact]
    public void SegmentStarted_FiresOnAudibleBoundary()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: "playerA");
        var b = new GaplessTrackSegment(8000, 1, source: "playerB");
        var started = new System.Collections.Generic.List<object?>();
        provider.SegmentStarted += s => started.Add(s.Source);
        provider.Enqueue(a);
        provider.Enqueue(b);

        a.Write(ConstantBlock(1000, 10));
        a.MarkEndOfStream();
        b.Write(ConstantBlock(1000, 10));
        b.MarkEndOfStream();

        var buffer = new float[40];
        provider.Read(buffer, 0, 40);

        Assert.Equal(new object?[] { "playerA", "playerB" }, started);
    }

    [Fact]
    public void Flush_ResetsBufferAndPosition()
    {
        var seg = new GaplessTrackSegment(1000, 1, source: null, capacitySeconds: 2, basePositionMs: 0);
        seg.Write(ConstantBlock(1000, 500));
        var dest = new float[500];
        seg.Read(dest, 0, 500);
        Assert.Equal(500, seg.PositionMs); // 500 frames @1kHz = 500ms

        seg.Flush(30_000); // seek to 30s
        Assert.Equal(30_000, seg.PositionMs);
        Assert.Equal(0, seg.BufferedSamples);

        Assert.True(seg.Write(ConstantBlock(1000, 100))); // still usable after flush
        Assert.Equal(100, seg.Read(dest, 0, 100));
        Assert.Equal(30_100, seg.PositionMs);
    }

    [Fact]
    public void Abandon_UnblocksBlockedWriter()
    {
        var seg = new GaplessTrackSegment(8000, 1, source: null, capacitySeconds: 2);
        Assert.True(seg.Write(ConstantBlock(1, 16000))); // ring exactly full

        var writerResult = Task.Run(() => seg.Write(ConstantBlock(1, 100), timeoutMs: 10_000));
        Assert.False(writerResult.Wait(150)); // genuinely blocked on the full ring

        seg.Abandon();
        Assert.True(writerResult.Wait(2000));
        Assert.False(writerResult.Result); // writer told the segment is dead
    }

    [Fact]
    public void Backpressure_WriterResumesWhenReaderDrains()
    {
        var seg = new GaplessTrackSegment(8000, 1, source: null, capacitySeconds: 2);
        Assert.True(seg.Write(ConstantBlock(1, 16000)));

        var writer = Task.Run(() => seg.Write(ConstantBlock(2, 4000), timeoutMs: 10_000));
        Assert.False(writer.Wait(100));

        var dest = new float[8000];
        seg.Read(dest, 0, 8000); // free half the ring

        Assert.True(writer.Wait(2000));
        Assert.True(writer.Result);
        Assert.Equal(16000 - 8000 + 4000, seg.BufferedSamples);
    }

    [Fact]
    public void Resampled_Segment_SplicesWithoutGap()
    {
        // 4kHz mono segment into an 8kHz mono sink: the adapter must upsample
        // and the follow-on same-rate segment must still butt against it.
        var provider = new GaplessSpliceProvider(8000, 1);
        var low = new GaplessTrackSegment(4000, 1, source: null);
        var native = new GaplessTrackSegment(8000, 1, source: null);
        provider.Enqueue(low);
        provider.Enqueue(native);

        low.Write(ConstantBlock(16384, 400));   // 100ms @4k → ~200ms worth @8k
        low.MarkEndOfStream();
        native.Write(ConstantBlock(-16384, 800));
        native.MarkEndOfStream();

        var buffer = new float[2400];
        var read = provider.Read(buffer, 0, 2400);
        Assert.Equal(2400, read);

        // The exact resampler tail length is implementation-defined; assert the
        // structure instead: a positive region, then a negative region, and no
        // run of inserted silence between them (>2ms of zeros = a gap).
        var firstNegative = Array.FindIndex(buffer, s => s < -0.25f);
        Assert.True(firstNegative > 700, $"negative region started at {firstNegative}");
        var positiveRegion = buffer.Take(700).Count(s => s > 0.25f);
        Assert.True(positiveRegion > 600, $"only {positiveRegion} strong positive samples");
        var boundaryZeros = 0;
        var maxRunOfZeros = 0;
        foreach (var s in buffer.Take(firstNegative < 0 ? buffer.Length : firstNegative + 1))
        {
            if (Math.Abs(s) < 0.01f) { boundaryZeros++; maxRunOfZeros = Math.Max(maxRunOfZeros, boundaryZeros); }
            else boundaryZeros = 0;
        }
        Assert.True(maxRunOfZeros <= 16, $"silence run of {maxRunOfZeros} samples at the resampled boundary");
    }

    [Fact]
    public void Clear_AbandonsEverything_ThenSilence()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: null);
        provider.Enqueue(a);
        a.Write(ConstantBlock(16384, 100));

        var buffer = new float[10];
        provider.Read(buffer, 0, 10); // activates A
        provider.Clear();

        Assert.True(a.Abandoned);
        provider.Read(buffer, 0, 10);
        Assert.All(buffer, s => Assert.Equal(0f, s));
        Assert.Null(provider.ActiveSegment);
    }

    [Fact]
    public void Read_AlwaysReturnsFullCount()
    {
        // The device stream must never stop: empty provider = full silence read.
        var provider = new GaplessSpliceProvider(48000, 2);
        var buffer = new float[960];
        Assert.Equal(960, provider.Read(buffer, 0, 960));
        Assert.All(buffer, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Flush_RearmsThePrebufferGate()
    {
        // A seek reaches the segment as Flush(); rendering must then wait for the
        // pre-buffer again instead of chopping the first trickle blocks against
        // silence (the post-seek buzz) — the gate is per-fill, not once-per-life.
        var provider = new GaplessSpliceProvider(8000, 1, startThresholdMs: 100); // gate = 800 samples
        var seg = new GaplessTrackSegment(8000, 1, source: null);
        provider.Enqueue(seg);

        Assert.True(seg.Write(ConstantBlock(16384, 900)));
        var buffer = new float[100];
        provider.Read(buffer, 0, 100);
        Assert.All(buffer, s => Assert.True(s > 0.4f, $"pre-flush sample was {s}"));

        seg.Flush(0);
        Assert.True(seg.Write(ConstantBlock(16384, 100))); // below the 800-sample gate

        provider.Read(buffer, 0, 100);
        Assert.All(buffer, s => Assert.Equal(0f, s)); // re-armed: held in silence

        Assert.True(seg.Write(ConstantBlock(16384, 700))); // 800 buffered — gate opens
        provider.Read(buffer, 0, 100);
        Assert.All(buffer, s => Assert.True(s > 0.4f, $"post-refill sample was {s}"));
    }

    [Fact]
    public void MidTrackUnderrun_HoldsSilenceUntilRefilled()
    {
        // After an underrun the provider must not consume each trickle block the
        // instant it lands — that alternates audio/silence at the ~10ms read
        // cadence (the ~100Hz chop buzz). Hold until ~50ms re-buffers, or EOS.
        var provider = new GaplessSpliceProvider(8000, 1); // refill = 400 samples @ 8k mono
        var seg = new GaplessTrackSegment(8000, 1, source: null);
        provider.Enqueue(seg);

        Assert.True(seg.Write(ConstantBlock(16384, 100)));
        var buffer = new float[200];
        provider.Read(buffer, 0, 200); // 100 audio + 100 silence pad = underrun

        Assert.True(seg.Write(ConstantBlock(16384, 100))); // trickle, below refill
        provider.Read(buffer, 0, 100);
        Assert.All(buffer.Take(100), s => Assert.Equal(0f, s)); // still holding

        Assert.True(seg.Write(ConstantBlock(16384, 400))); // 500 buffered ≥ 400
        provider.Read(buffer, 0, 100);
        Assert.All(buffer.Take(100), s => Assert.True(s > 0.4f, $"post-refill sample was {s}"));

        // EOS releases the hold: play out what is left, don't wait for a refill.
        var p2 = new GaplessSpliceProvider(8000, 1);
        var tail = new GaplessTrackSegment(8000, 1, source: null);
        p2.Enqueue(tail);
        Assert.True(tail.Write(ConstantBlock(16384, 100)));
        p2.Read(buffer, 0, 200);              // underrun arms the hold
        Assert.True(tail.Write(ConstantBlock(16384, 50)));
        tail.MarkEndOfStream();               // EOS bypasses the refill hold
        p2.Read(buffer, 0, 50);
        Assert.All(buffer.Take(50), s => Assert.True(s > 0.4f, $"tail sample was {s}"));
    }

    [Fact]
    public void FadeIn_AppliedAfterSilence_NeverAtTheSpliceSeam()
    {
        // Segment heads can carry decoder warm-up garble; a short fade-in from
        // SILENCE masks it at track start / post-seek. The gapless seam is by
        // definition never preceded by silence, so it must stay bit-exact.
        var provider = new GaplessSpliceProvider(8000, 1, startThresholdMs: 0, startFadeMs: 5); // 40-sample fade
        var a = new GaplessTrackSegment(8000, 1, source: null);
        var b = new GaplessTrackSegment(8000, 1, source: null);
        provider.Enqueue(a);
        provider.Enqueue(b);

        Assert.True(a.Write(ConstantBlock(16384, 100)));  // ≈ +0.5f
        a.MarkEndOfStream();
        Assert.True(b.Write(ConstantBlock(-16384, 100))); // ≈ -0.5f
        b.MarkEndOfStream();

        var buffer = new float[200];
        provider.Read(buffer, 0, 200);

        // Cold start (provider was silent): A's head ramps 0 → full over 40 samples.
        Assert.True(Math.Abs(buffer[0]) < 0.05f, $"first sample was {buffer[0]}");
        Assert.True(buffer[20] > 0.15f && buffer[20] < 0.4f, $"mid-fade sample was {buffer[20]}");
        Assert.True(buffer[60] > 0.45f, $"post-fade sample was {buffer[60]}");
        // The seam is untouched: B starts at full level immediately.
        Assert.True(buffer[100] < -0.45f, $"seam sample was {buffer[100]}");
    }
}
