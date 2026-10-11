using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Gapless early advance vs. an under-reported length. VLC's length for an MP3 with no
// Xing/LAME header (or a concatenated / truncated file) is a bitrate estimate that can run
// seconds short. The view model advances the queue 0.5 s before the length it is told, and
// PlayInternal's splice cuts a still-live outgoing segment over to the staged track — the
// harness (10-10) lost the last 4 s of a 10 s VBR MP3 that VLC called 6.4 s. On the engine
// the reported length now follows what the ring has actually received.
public class GaplessReportedLengthTests
{
    [Fact]
    public void PlausibleEstimate_IsLeftAlone_WhileDecoding()
    {
        // 4 s delivered of a 10 s claim: nothing to correct.
        Assert.Equal(10_000, VlcAudioPlayer.EngineReportedLengthMs(10_000, inputEnded: false, deliveredMs: 4_000));
        // Encoder padding past the claim is slack, not an under-report.
        Assert.Equal(6_034, VlcAudioPlayer.EngineReportedLengthMs(6_034, inputEnded: false, deliveredMs: 6_034 + VlcAudioPlayer.EngineLengthSlackMs));
    }

    [Fact]
    public void InputPastTheClaimedLength_ReportsTheRingPlusALead()
    {
        // VLC said 6.4 s, the ring already holds 7.9 s: the end is at least 7.9 s + lead, so
        // the view model's "remaining <= 0.5 s" cannot fire until the input really ends.
        var reported = VlcAudioPlayer.EngineReportedLengthMs(6_388, inputEnded: false, deliveredMs: 7_900);
        Assert.Equal(7_900 + VlcAudioPlayer.EngineLengthUnderReportLeadMs, reported);
        Assert.True(reported - 7_900 > 500 + 1_000, "lead must cover the handoff lead plus the decode-ahead window");
    }

    [Fact]
    public void InputEnded_ReportsTheDeliveredLength()
    {
        // EOF reached: the delivered length is the true one, over- or under-reported alike.
        Assert.Equal(10_031, VlcAudioPlayer.EngineReportedLengthMs(6_388, inputEnded: true, deliveredMs: 10_031));
        Assert.Equal(6_008, VlcAudioPlayer.EngineReportedLengthMs(6_034, inputEnded: true, deliveredMs: 6_008));
    }

    [Fact]
    public void NothingDeliveredYet_KeepsVlcLength()
    {
        Assert.Equal(6_388, VlcAudioPlayer.EngineReportedLengthMs(6_388, inputEnded: false, deliveredMs: 0));
        Assert.Equal(0, VlcAudioPlayer.EngineReportedLengthMs(0, inputEnded: false, deliveredMs: 0));
    }

    [Fact]
    public void DeliveredLength_IsReadFromTheSegment()
    {
        // The getter derives "delivered" from the segment: audible position + what is buffered.
        var seg = new GaplessTrackSegment(8000, 1, source: 0, basePositionMs: 1_000);
        seg.Write(new short[8000]);                  // 1 s written, none consumed
        seg.Read(new float[4000], 0, 4000);          // 0.5 s consumed
        var deliveredMs = seg.PositionMs + (long)seg.BufferedSamples * 1000 / (seg.SampleRate * seg.Channels);
        Assert.Equal(2_000, deliveredMs);
    }
}
