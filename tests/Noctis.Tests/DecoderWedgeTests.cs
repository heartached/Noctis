using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 10-28 "Trouble Playing Long MP3 File": LibVLC's mpg123 decoder failed every frame
/// after damaged data ~31 min into a one-hour MP3 and the app sat silent.
/// </summary>
public class DecoderWedgeTests
{
    [Theory]
    [InlineData("mpg123", "mpg123_decode_frame error: A generic mpg123 error.", true)]
    [InlineData("mpg123", "mpg123_feed failed: Out of memory", false)]
    [InlineData("avcodec", "mpg123_decode_frame error: A generic mpg123 error.", false)]
    [InlineData("mpg123", null, false)]
    public void IsWedgeLine_MatchesOnlyMpg123DecodeErrors(string module, string? message, bool expected)
        => Assert.Equal(expected, DecoderWedgeDetector.IsWedgeLine(module, message));

    [Fact]
    public void TheReportedStorm_IsCaughtWithinASecond()
    {
        // The log's rate: 380 in 10 s, one per 22.05 kHz MPEG-2 frame (26 ms).
        var d = new DecoderWedgeDetector();
        var firedAt = -1L;
        for (var i = 0; i < 100 && firedAt < 0; i++)
            if (d.Accept(i * 26L)) firedAt = i * 26L;
        Assert.InRange(firedAt, 0, 1000);
        Assert.Equal(DecoderWedgeDetector.Threshold, d.LastStormErrors);
    }

    [Fact]
    public void AStorm_ReportsOnce_ThenAgainAfterTheCooldown()
    {
        var d = new DecoderWedgeDetector();
        var fired = new List<long>();
        for (long t = 0; t < 10_000; t += 26)
            if (d.Accept(t)) fired.Add(t);
        Assert.Equal(3, fired.Count); // ~0.3 s, then once per cooldown while it lasts
        Assert.True(fired[1] - fired[0] >= DecoderWedgeDetector.CooldownMs);
    }

    [Fact]
    public void ScatteredErrors_AreNotAStorm()
    {
        // A one-off damaged frame the decoder gets past (one every few seconds).
        var d = new DecoderWedgeDetector();
        for (long t = 0; t < 120_000; t += 3000)
            Assert.False(d.Accept(t));
    }

    [Theory]
    [InlineData(false, 1, "ReopenOnAvcodec")]
    [InlineData(true, 1, "SkipAhead")]
    [InlineData(true, 2, "SkipAhead")]
    [InlineData(true, 3, "SkipAhead")]
    [InlineData(true, 4, "GiveUp")]
    [InlineData(false, 4, "GiveUp")]
    public void Recovery_ReopensFirst_ThenSkips_ThenGivesUp(bool onAvcodec, int attempt, string expected)
        => Assert.Equal(expected, VlcAudioPlayer.ChooseDecoderWedgeAction(onAvcodec, attempt).ToString());

    [Fact]
    public void Skips_GrowWithEachAttempt()
    {
        Assert.Equal(2000, VlcAudioPlayer.DecoderWedgeSkipMs(2));
        Assert.True(VlcAudioPlayer.DecoderWedgeSkipMs(3) > VlcAudioPlayer.DecoderWedgeSkipMs(2));
    }
}
