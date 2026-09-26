using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The engine sink's idle park (A03). A running WASAPI render stream holds Windows'
/// "audio stream in use" power request even while it renders silence, so the sink must
/// stop its stream once paused, stopped or never-started playback has sat idle for the
/// timeout, never while track audio flows, and must restart it once audio is due again.
/// </summary>
public class GaplessSinkIdleTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const int IdleMs = 10_000;
    private const int TickMs = 2000;
    private const int DueSamples = Rate * Channels * 200 / 1000; // the sink's 200 ms start gate

    private static GaplessTrackSegment Segment(int bufferedSamples)
    {
        var seg = new GaplessTrackSegment(Rate, Channels, source: 0, capacitySeconds: 2);
        if (bufferedSamples > 0)
            Assert.True(seg.Write(new short[bufferedSamples]));
        return seg;
    }

    [Fact]
    public void Playing_NeverParks_EvenWithLittleBuffered()
    {
        var watch = new GaplessSink.IdleWatch(IdleMs, DueSamples, nowMs: 0);
        var seg = Segment(0);
        var scratch = new float[9600];
        for (long now = TickMs; now <= 10 * IdleMs; now += TickMs)
        {
            // A trickling input: each block is rendered as soon as it lands, so the ring
            // never holds a start gate's worth — only the consumed position moves.
            Assert.True(seg.Write(new short[9600]));
            Assert.Equal(9600, seg.Read(scratch, 0, scratch.Length));
            Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(now, seg, providerParked: false, streamParked: false));
        }
    }

    [Fact]
    public void Paused_ParksOnlyAfterTheTimeout()
    {
        var watch = new GaplessSink.IdleWatch(IdleMs, DueSamples, nowMs: 0);
        var seg = Segment(Rate * Channels); // a second held in the ring while paused
        Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(TickMs, seg, providerParked: true, streamParked: false));
        for (long now = 2 * TickMs; now < TickMs + IdleMs; now += TickMs)
            Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(now, seg, providerParked: true, streamParked: false));
        Assert.Equal(GaplessSink.IdleStep.Park, watch.Observe(TickMs + IdleMs, seg, providerParked: true, streamParked: false));
    }

    [Fact]
    public void Stopped_WithNoSegment_ParksAfterTheTimeout()
    {
        var watch = new GaplessSink.IdleWatch(IdleMs, DueSamples, nowMs: 0);
        Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(IdleMs - TickMs, null, providerParked: false, streamParked: false));
        Assert.Equal(GaplessSink.IdleStep.Park, watch.Observe(IdleMs, null, providerParked: false, streamParked: false));
    }

    [Fact]
    public void ParkedStream_WakesOnlyWhenAudioIsDue()
    {
        var watch = new GaplessSink.IdleWatch(IdleMs, DueSamples, nowMs: 0);
        // Restored paused: a pre-roll below the start gate never renders, so it must not
        // keep waking the stream.
        var preroll = Segment(DueSamples / 4);
        for (long now = TickMs; now <= 3 * IdleMs; now += TickMs)
            Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(now, preroll, providerParked: false, streamParked: true));

        // Paused with seconds held: waits for Resume.
        var held = Segment(Rate * Channels);
        Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(4 * IdleMs, held, providerParked: true, streamParked: true));

        // Un-parked with a start gate's worth buffered: the provider would render it now.
        Assert.Equal(GaplessSink.IdleStep.Wake, watch.Observe(4 * IdleMs + TickMs, held, providerParked: false, streamParked: true));
    }

    [Fact]
    public void Touch_RestartsTheTimeout()
    {
        var watch = new GaplessSink.IdleWatch(IdleMs, DueSamples, nowMs: 0);
        watch.Touch(IdleMs - 1000);
        Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(IdleMs, null, providerParked: false, streamParked: false));
        Assert.Equal(GaplessSink.IdleStep.Park, watch.Observe(2 * IdleMs - 1000, null, providerParked: false, streamParked: false));
    }

    [Fact]
    public void ZeroTimeout_NeverParks()
    {
        var watch = new GaplessSink.IdleWatch(0, DueSamples, nowMs: 0);
        Assert.Equal(GaplessSink.IdleStep.None, watch.Observe(long.MaxValue / 2, null, providerParked: false, streamParked: false));
    }
}
