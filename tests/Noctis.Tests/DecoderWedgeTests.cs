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

    // Discord 11-03: the recovery worked but left a "crunch" at the damaged spot.

    private static short[] Tone(int frames, short level = 16384)
    {
        var pcm = new short[frames];
        Array.Fill(pcm, level);
        return pcm;
    }

    [Fact]
    public void DamagedTail_IsCutAndFadedOut_AndLaterWritesFadeIn()
    {
        var seg = new GaplessTrackSegment(1000, 1, null, basePositionMs: 5000);
        Assert.True(seg.Write(Tone(100)));
        var head = new float[20];
        Assert.Equal(20, seg.Read(head, 0, 20));

        // The write end (100 frames in at 1 kHz) is where the damage starts.
        Assert.Equal(5100, seg.TrimDamagedTail(dropFrames: 10, fadeFrames: 20));
        Assert.Equal(70, seg.BufferedSamples);
        var rest = new float[70];
        Assert.Equal(70, seg.Read(rest, 0, 70));
        Assert.All(rest.Take(50), v => Assert.Equal(0.5f, v));
        Assert.Equal(0f, rest[69]);
        for (var i = 51; i < 70; i++) Assert.True(rest[i] < rest[i - 1], $"not fading at {i}");

        // A decoder that comes back after all fades in from silence instead of starting at full level.
        Assert.True(seg.Write(Tone(30)));
        var after = new float[30];
        seg.Read(after, 0, 30);
        Assert.Equal(0f, after[0]);
        Assert.True(after[10] is > 0f and < 0.5f);
        Assert.Equal(0.5f, after[25]);
    }

    [Fact]
    public void DamagedTail_SkipsTheZerosWrittenForUndecodableFrames()
    {
        var seg = new GaplessTrackSegment(1000, 1, null);
        Assert.True(seg.Write(Tone(100)));
        Assert.True(seg.Write(new short[30]));
        Assert.Equal(130, seg.TrimDamagedTail(dropFrames: 10, fadeFrames: 20, maxSilentFrames: 50));
        Assert.Equal(90, seg.BufferedSamples);
        var rest = new float[90];
        seg.Read(rest, 0, 90);
        Assert.All(rest.Take(70), v => Assert.Equal(0.5f, v));
        Assert.Equal(0f, rest[89]);
        for (var i = 71; i < 90; i++) Assert.True(rest[i] < rest[i - 1], $"not fading at {i}");
    }

    [Fact]
    public void DamagedTail_ReturnsMinusOne_OnceTheInputEnded()
    {
        var seg = new GaplessTrackSegment(1000, 2, null);
        Assert.True(seg.Write(Tone(200)));
        seg.MarkEndOfStream();
        Assert.Equal(-1, seg.TrimDamagedTail(30, 20));
        Assert.Equal(200, seg.BufferedSamples);
    }

    [Fact]
    public void WriteIdle_IsUnknownBeforeTheFirstWrite()
    {
        var seg = new GaplessTrackSegment(1000, 1, null);
        Assert.Equal(-1, seg.WriteIdleMs);
        seg.Write(Tone(10));
        Assert.InRange(seg.WriteIdleMs, 0, 1000);
    }

    [Fact]
    public void Reopen_SkipsThePrimingZeros_AndFadesTheMusicIn()
    {
        // The user's log: the reopened input began with 1254 frames of zeros (SegHead peak=0).
        var seg = new GaplessTrackSegment(1000, 2, null, basePositionMs: 1000);
        seg.DropLeadingSilence(fadeInFrames: 10);
        Assert.True(seg.Write(new short[2 * 57]));
        Assert.Equal(0, seg.BufferedSamples);
        Assert.True(seg.Write(Tone(2 * 40)));
        Assert.Equal(80, seg.BufferedSamples);
        var out1 = new float[80];
        seg.Read(out1, 0, 80);
        Assert.Equal(0f, out1[0]);
        Assert.Equal(out1[0], out1[1]); // per frame, both channels alike
        Assert.True(out1[10] is > 0f and < 0.5f);
        Assert.Equal(0.5f, out1[79]);
        // The zeros count as played: the position stays media time.
        Assert.Equal(1000 + 57 + 40, seg.PositionMs);
        // Silence later in the track is music, not priming.
        Assert.True(seg.Write(new short[20]));
        Assert.Equal(20, seg.BufferedSamples);
    }

    [Fact]
    public void FallbackList_SurvivesARestart_UntilTheFileIsReplaced()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mp3 = Path.Combine(dir, "long.mp3");
            File.WriteAllBytes(mp3, new byte[100]);
            var store = Path.Combine(dir, "mp3_decoder_fallback.txt");

            var first = new Mp3DecoderFallbackList(store);
            Assert.False(first.Contains(mp3));
            Assert.True(first.Add(mp3));
            Assert.False(first.Add(mp3));
            Assert.True(first.Contains(mp3));

            Assert.True(new Mp3DecoderFallbackList(store).Contains(mp3));
            Assert.False(new Mp3DecoderFallbackList(store).Contains(Path.Combine(dir, "other.mp3")));

            // A tag rewritten in place (a rating) moves the write time, not the size.
            File.SetLastWriteTimeUtc(mp3, DateTime.UtcNow.AddMinutes(5));
            Assert.True(new Mp3DecoderFallbackList(store).Contains(mp3));

            File.WriteAllBytes(mp3, new byte[120]); // fixed / re-encoded
            Assert.False(new Mp3DecoderFallbackList(store).Contains(mp3));
            Assert.False(new Mp3DecoderFallbackList(null).Contains(mp3));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    // writeEndMs, block frames @ 1 kHz, expected frames kept (-1 = no cut)
    [InlineData(900, 50, -1)]   // the block ends before the cut
    [InlineData(980, 50, 20)]   // straddles the cut at 1000
    [InlineData(1000, 50, 0)]   // starts right on it
    [InlineData(1010, 50, -1)]  // past it (a seek beyond): keeps playing
    public void DamageAhead_EndsTheInputAtTheCut(long writeEndMs, int frames, int expected)
    {
        var damage = new[] { new Mp3FrameScan.Damage(1000, 1030), new Mp3FrameScan.Damage(5000, 5026) };
        Assert.Equal(expected, VlcAudioPlayer.DamageCutFrames(writeEndMs, frames, 1000, damage, out var run));
        if (expected >= 0) Assert.Equal(1030, run.ResumeMs);
    }

    [Fact]
    public void EndWithFade_FadesTheNewest_KeepsThePadding_AndRefusesMore()
    {
        var seg = new GaplessTrackSegment(1000, 1, null, basePositionMs: 2000) { TailTrimFrames = 50 };
        Assert.True(seg.Write(Tone(100)));
        Assert.Equal(2100, seg.WriteEndMs);
        seg.EndWithFade(20);
        Assert.True(seg.EndOfStream);
        Assert.False(seg.Write(Tone(10)));
        var all = new float[100];
        Assert.Equal(100, seg.Read(all, 0, 100)); // no encoder padding cut: not the file's end
        Assert.Equal(0.5f, all[79]);
        Assert.Equal(0f, all[99]);
        Assert.True(seg.IsFinished);
    }

    [Fact]
    public void Skips_GrowWithEachAttempt()
    {
        Assert.Equal(2000, VlcAudioPlayer.DecoderWedgeSkipMs(2));
        Assert.True(VlcAudioPlayer.DecoderWedgeSkipMs(3) > VlcAudioPlayer.DecoderWedgeSkipMs(2));
    }
}
