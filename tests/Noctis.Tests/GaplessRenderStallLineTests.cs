using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-27 "weird audio cut off": the RenderStall lines said "audible=True" for 25-40 ms gen0
/// stalls, which read as cuts, though the device had 100 ms queued and only the 760 ms
/// blocking gen2 ran it dry. The line now says what each field means: "playing" (music was
/// playing) and "underrun" (the gap outlasted the audio queued at the device).
/// </summary>
public class GaplessRenderStallLineTests
{
    [Fact]
    public void ShortGcStall_WhilePlaying_IsNoUnderrun()
    {
        var line = GaplessSink.RenderStallLine(31.8, 22.3, 1, 0, 0, playing: true, queuedMs: 100);
        Assert.Equal("gapMs=31.8, gcPauseMs=22.3, gcs=1/0/0, playing=True, queuedMs=100, underrun=False", line);
    }

    [Fact]
    public void BlockingGen2_LongerThanTheQueuedAudio_IsAnUnderrun()
    {
        var line = GaplessSink.RenderStallLine(760.9, 760.1, 1, 1, 1, playing: true, queuedMs: 100);
        Assert.Equal("gapMs=760.9, gcPauseMs=760.1, gcs=1/1/1, playing=True, queuedMs=100, underrun=True", line);
        Assert.DoesNotContain("audible=", line);
    }

    [Theory]
    [InlineData(99.9, 100, false)]
    [InlineData(100.0, 100, false)]
    [InlineData(100.1, 100, true)]
    [InlineData(80.0, 110, false)] // the pulse server's 110 ms
    [InlineData(120.0, 110, true)]
    public void Underrun_IsTheGapAgainstTheQueuedAudio(double gapMs, int queuedMs, bool expected) =>
        Assert.Equal(expected, GaplessSink.IsUnderrun(gapMs, queuedMs));
}
