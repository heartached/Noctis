using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audio cutting out mid-track (owner log, 2026-09-23): the library sits on a hard disk that
/// other processes were hammering, VLC's reads stalled for 3.6–8 s, and the engine's only
/// protection was the 1 s file-caching window — everything past it was dropped as "too late".
/// Once a stall is seen, media opened afterwards read further ahead for the session.
/// </summary>
public class VlcReadAheadTests
{
    [Theory]
    [InlineData(1000, 500, 1000)]      // sub-second gap: ordinary jitter, leave it alone
    [InlineData(1000, 999, 1000)]
    [InlineData(1000, 1200, 2500)]     // 1.2 s stall + 1 s margin, rounded up to 500 ms
    [InlineData(1000, 3624, 5000)]     // the PtsGap from the owner's log
    [InlineData(1000, 6737, 8000)]     // 6.7 s stall would want 8 s: capped there
    [InlineData(5000, 1200, 5000)]     // never lowers an already raised value
    [InlineData(1000, 121926, 1000)]   // the whole process was frozen (system sleep), not the disk
    [InlineData(1000, -50, 1000)]
    [InlineData(10000, 3000, 10000)]   // NOCTIS_CACHING above the cap stays as configured
    public void NextReadAheadMs_GrowsToCoverTheStall_WithinBounds(int currentMs, double gapMs, int expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.NextReadAheadMs(currentMs, gapMs));
    }

    [Fact]
    public void ReadAheadOption_IsOnlyEmittedOnceTheValueWasRaised()
    {
        Assert.Null(VlcAudioPlayer.ReadAheadOption(1000, 1000));
        Assert.Equal(":file-caching=5000", VlcAudioPlayer.ReadAheadOption(1000, 5000));
    }

    /// <summary>
    /// Silent runtime run (audit R1): a 2 s user pause read as a 2005 ms PtsGap and raised
    /// file-caching 1000 -> 3500 for the rest of the session. VLC moves its clock on by the
    /// pause length, so the first block after the resume is stamped exactly that much later.
    /// </summary>
    [Fact]
    public void ExpectedPtsAfterPause_APauseIsNotAnInputStall()
    {
        const long expectedPts = 5_000_000_000;         // µs, VLC clock: next block due here
        const long pauseDate = 4_999_900_000;
        const long resumeDate = pauseDate + 2_005_000;  // 2005 ms pause
        var firstBlockPts = expectedPts + (resumeDate - pauseDate);

        var carried = VlcAudioPlayer.ExpectedPtsAfterPause(expectedPts, pauseDate, resumeDate);

        Assert.Equal(firstBlockPts, carried);
        Assert.Equal(1000, VlcAudioPlayer.NextReadAheadMs(1000, (firstBlockPts - carried) / 1000.0));
        // Uncarried, the same block raised the read-ahead:
        Assert.Equal(3500, VlcAudioPlayer.NextReadAheadMs(1000, (firstBlockPts - expectedPts) / 1000.0));
    }

    [Theory]
    [InlineData(0, 100, 2_000_100, 0)]          // head block pending: no continuity to carry
    [InlineData(1_000, 0, 2_000_100, 1_000)]    // resume without a pause seen
    [InlineData(1_000, 300, 200, 1_000)]        // dates backwards
    [InlineData(1_000, 300, 300, 1_000)]
    public void ExpectedPtsAfterPause_LeavesUntrackedOrBogusPausesAlone(
        long expectedPts, long pauseDate, long resumeDate, long result)
    {
        Assert.Equal(result, VlcAudioPlayer.ExpectedPtsAfterPause(expectedPts, pauseDate, resumeDate));
    }
}
