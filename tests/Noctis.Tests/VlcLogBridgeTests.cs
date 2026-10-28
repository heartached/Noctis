using System.Text.RegularExpressions;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class VlcLogBridgeTests
{
    [Theory]
    [InlineData("imem", "Invalid get/release function pointers", true)] // legacy probe for callback media
    [InlineData("imem", "some other imem failure", false)]
    [InlineData("pulse", "Invalid get/release function pointers", false)]
    [InlineData("main", "playback way too late (993212): flushing buffers", false)]
    [InlineData(null, null, false)]
    public void IsBenignVlcLogLine_MatchesOnlyTheImemProbe(string? module, string? message, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.IsBenignVlcLogLine(module, message));
    }

    // GitHub #139: under the splice engine VLC's drift correction can only run for the
    // keep-alive's silent pulse stream (amem has no time_get), so its lines are labelled.
    [Theory]
    [InlineData(true, "main", "playback too late (60855): up-sampling", true)]
    [InlineData(true, "main", "playback too early (-40158): down-sampling", true)]
    [InlineData(true, "main", "playback way too late (200557): flushing buffers", true)]
    [InlineData(true, "main", "playback way too early (-199206): playing silence", true)]
    [InlineData(true, "main", "timing screwed (drift: 151280 us): stopping resampling", true)]
    [InlineData(true, "pulse", "starting late (-40876 us)", true)]
    [InlineData(true, "vlcpulse", "stream connection failure: No such entity", true)]
    [InlineData(true, "main", "buffer too late (-80000 us): dropped", false)] // checked before time_get: hits amem too
    [InlineData(true, "avformat", "playback too late (60855): up-sampling", false)]
    [InlineData(true, null, null, false)]
    [InlineData(false, "main", "playback too late (60855): up-sampling", false)] // classic path: the music's own output
    [InlineData(false, "pulse", "starting late (-40876 us)", false)]
    public void IsKeepAliveOutputLine_OnlyOutputClockLines_OnlyUnderTheEngine(
        bool engineWithVlcKeepAlive, string? module, string? message, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.IsKeepAliveOutputLine(engineWithVlcKeepAlive, module, message));
    }

    [Theory]
    [InlineData("Warning pulse: starting late (-1270511 us)", "Warning pulse: starting late (-# us)")]
    [InlineData("Warning main: playback way too late (993212): flushing buffers", "Warning main: playback way too late (#): flushing buffers")]
    [InlineData("Warning avformat: DEMUX_SET_POSITION: 0", "Warning avformat: DEMUX_SET_POSITION: #")]
    [InlineData("Error mmdevice: cannot initialize COM (error 0x80010106)", "Error mmdevice: cannot initialize COM (error #x#)")]
    [InlineData("Warning pulse: balance clobbered by volume change", "Warning pulse: balance clobbered by volume change")]
    [InlineData("", "")]
    public void Shape_ReplacesEachDigitRun(string line, string expected)
    {
        Assert.Equal(expected, VlcLogCollapser.Shape(line));
    }

    [Fact]
    public void RepeatsOfOneShape_WriteABurst_ThenOneCountedSummaryPerWindow()
    {
        var c = new VlcLogCollapser();
        var written = new List<string>();
        for (var i = 0; i < 50; i++) // 50 lines in 5 s, lateness changing every line
            written.AddRange(c.Accept($"Warning pulse: starting late (-{900_000 + i} us)", 1_000 + i * 100) ?? new());

        Assert.Equal(VlcLogCollapser.BurstPerWindow, written.Count);

        // The next line after the window closes first reports the held-back ones.
        var next = c.Accept("Warning pulse: starting late (-5 us)", 1_000 + VlcLogCollapser.WindowMs);
        Assert.NotNull(next);
        Assert.Equal(2, next!.Count);
        Assert.Equal("×47 more like this in 5 s: Warning pulse: starting late (-900049 us)", next[0]);
        Assert.Equal("Warning pulse: starting late (-5 us)", next[1]); // a new window's burst
    }

    [Fact]
    public void ASummaryWrittenLongAfterItsWindow_SaysHowOldTheLineIs()
    {
        // Discord 10-28: four seeks in 6 s, the fourth DEMUX_SET_TIME held back; the next
        // VLC line came 31 min later and carried the summary with it.
        var c = new VlcLogCollapser();
        for (var i = 0; i < 4; i++)
            c.Accept($"Warning avcodec: DEMUX_SET_TIME: {1672268000 + i}", i * 2000);

        var late = c.Accept("Error mpg123: mpg123_decode_frame error: A generic mpg123 error.", 6000 + 1_873_000);
        Assert.NotNull(late);
        Assert.Equal("×1 more like this in 6 s, last one 31 min 13 s ago: Warning avcodec: DEMUX_SET_TIME: 1672268003", late![0]);
    }

    [Fact]
    public void AnotherShape_IsNeverHeldBackByAStorm()
    {
        var c = new VlcLogCollapser();
        for (var i = 0; i < 100; i++)
            c.Accept($"Warning main: playback way too late ({i}): flushing buffers", i);

        var distinct = c.Accept("Error pulse: cannot write", 200);
        Assert.Equal(new[] { "Error pulse: cannot write" }, distinct);
    }

    [Fact]
    public void Forced_Lines_AreWrittenPastTheBurst()
    {
        var c = new VlcLogCollapser();
        for (var i = 0; i < VlcLogCollapser.BurstPerWindow; i++)
            c.Accept("Error main: decoder failure", i);

        Assert.Null(c.Accept("Error main: decoder failure", 10));
        Assert.Equal(new[] { "Error main: decoder failure" }, c.Accept("Error main: decoder failure", 20, force: true));
    }

    [Fact]
    public void Flush_ReportsHeldBackCounts_OnlyOnce()
    {
        var c = new VlcLogCollapser();
        for (var i = 0; i < 10; i++)
            c.Accept("Warning main: buffer too late (-75340 us): dropped", i * 10);

        Assert.Equal(new[] { "×7 more like this in 1 s: Warning main: buffer too late (-75340 us): dropped" }, c.Flush(500));
        Assert.Null(c.Flush(600));
    }

    [Fact]
    public void AWindowWithNothingHeldBack_ClosesSilently()
    {
        var c = new VlcLogCollapser();
        c.Accept("Warning pulse: balance clobbered by volume change", 0);

        Assert.Equal(new[] { "Warning pulse: balance clobbered by volume change" },
            c.Accept("Warning pulse: balance clobbered by volume change", VlcLogCollapser.WindowMs * 3));
        Assert.Null(c.Flush(VlcLogCollapser.WindowMs * 10));
    }

    [Fact]
    public void TheIssue70Storm_FitsAHourInTheSessionLog_WithEveryLineAccountedFor()
    {
        // The reporter's 1.5.7 log: three interleaved warnings ≈ 48/s from launch.
        var c = new VlcLogCollapser();
        var written = new List<string>();
        var total = 0;
        for (var ms = 0L; ms < 60_000; ms += 100)
        {
            foreach (var line in new[]
                     {
                         "Warning avformat: DEMUX_SET_POSITION: 0",
                         $"Warning pulse: starting late (-{6_600_000 + ms} us)",
                         $"Warning main: playback way too late ({6_640_000 + ms}): flushing buffers",
                         $"Warning pulse: starting late (-{6_700_000 + ms} us)",
                         $"Warning main: playback way too late ({6_470_000 + ms}): flushing buffers",
                     })
            {
                total++;
                written.AddRange(c.Accept(line, ms) ?? new());
            }
        }
        written.AddRange(c.Flush(60_000) ?? new());

        // 3000 lines in a minute → a few dozen (the 5000-line cap now lasts ~1 h).
        Assert.True(written.Count <= 3 * 6 * (VlcLogCollapser.BurstPerWindow + 1), $"wrote {written.Count}");
        var summarized = written.Select(l => Regex.Match(l, @"^×(\d+) more"))
            .Where(m => m.Success).Sum(m => int.Parse(m.Groups[1].Value));
        var verbatim = written.Count(l => !l.StartsWith('×'));
        Assert.Equal(total, verbatim + summarized);
    }
}
