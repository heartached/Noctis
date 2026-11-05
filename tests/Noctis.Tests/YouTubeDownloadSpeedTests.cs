using Noctis.Plugins.YouTubeImport;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Download speed + ETA on the "Add from YouTube" page: yt-dlp line parsing, the live
/// install tracker, and the "4.2 MB/s · about 30 s left" status text (the app's units and wording).
/// </summary>
public class YouTubeDownloadSpeedTests
{
    [Theory]
    // percent, bytes/s, ETA
    [InlineData("[download]  45.3% of 3.21MiB at 1.20MiB/s ETA 00:01", 45.3, 1258291.2, "00:00:01")]
    [InlineData("[download]  12.4% of ~10.55MiB at  850.00KiB/s ETA 00:11", 12.4, 870400.0, "00:00:11")]
    [InlineData("[download]   3.1% of ~1.20GiB at  2.50GiB/s ETA 01:02:03", 3.1, 2684354560.0, "01:02:03")]
    [InlineData("[download]   7.0% of ~5.00MiB at 512B/s ETA 01:30", 7.0, 512.0, "00:01:30")]
    public void ParseDownloadDetail_ReadsPercentSpeedAndEta(string line, double percent, double bytesPerSec, string eta)
    {
        var d = Assert.IsType<DownloadDetail>(YtDlpParsing.ParseDownloadDetail(line));
        Assert.Equal(percent, d.Percent);
        Assert.Equal(bytesPerSec, d.BytesPerSec!.Value, precision: 1);
        Assert.Equal(TimeSpan.Parse(eta), d.Eta);
    }

    [Theory]
    [InlineData("[download] 100% of 3.21MiB in 00:02", 100.0, null, null)]
    [InlineData("[download]   0.0% of ~5.00MiB at Unknown", 0.0, null, null)]
    [InlineData("[ExtractAudio] Destination: x.m4a", null, null, null)]
    [InlineData("", null, null, null)]
    public void ParseDownloadDetail_ToleratesMissingParts(string line, double? percent, double? speed, string? eta)
    {
        var d = YtDlpParsing.ParseDownloadDetail(line);
        Assert.Equal(percent is not null, d is not null);
        if (d is null) return;
        Assert.Equal(percent, d.Percent);
        Assert.Equal(speed is not null, d.BytesPerSec is not null);
        Assert.Equal(eta is not null, d.Eta is not null);
    }

    [Theory]
    // Same wording and rounding as the app's DownloadMeter (Lyrics Studio, Settings → Plugins).
    [InlineData(4.24 * 1048576, 32, "4.2 MB/s · about 35 s left")]
    [InlineData(300 * 1024.0, 1, "300 KB/s · about 5 s left")]
    [InlineData(12.6 * 1048576, 1932, "13 MB/s · about 32 min left")]
    [InlineData(1048576.0, 3900, "1.0 MB/s · about 1 h 5 min left")]
    [InlineData(5.5 * 1048576, null, "5.5 MB/s")]
    public void Format_ComposesSpeedAndEta(double bytesPerSec, int? etaSeconds, string expected)
    {
        TimeSpan? eta = etaSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        Assert.Equal(expected, DownloadFormat.Format(bytesPerSec, eta));
    }

    [Fact]
    public void Throttle_LetsAboutFourReportsASecondThrough()
    {
        var now = DateTime.UtcNow;
        var throttle = new ReportThrottle(() => now);
        var passed = 0;
        for (var i = 0; i < 1000; i++) // a 64 KiB chunk every millisecond: ~64 MB/s
        {
            if (throttle.Due()) passed++;
            now += TimeSpan.FromMilliseconds(1);
        }
        Assert.Equal(4, passed);
    }

    [Fact]
    public void Tracker_WarmsUp_ThenReportsRateAndEta()
    {
        var now = DateTime.UtcNow;
        var tracker = new DownloadSpeedTracker(() => now);

        Assert.Null(tracker.Sample(0, 10_000_000)); // first sample: warming up
        now += TimeSpan.FromMilliseconds(200);
        Assert.Null(tracker.Sample(100_000, 10_000_000)); // too soon to trust
        now += TimeSpan.FromMilliseconds(400); // 0.6 s, 500 kB in
        var s = tracker.Sample(500_000, 10_000_000);
        Assert.NotNull(s);
        Assert.Equal(500_000 / 0.6, s.Value.BytesPerSec, precision: 3);
        Assert.Equal((10_000_000 - 500_000) / (500_000 / 0.6), s.Value.Eta!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void Tracker_StalledOrDone_YieldsNothing()
    {
        var now = DateTime.UtcNow;
        var tracker = new DownloadSpeedTracker(() => now);
        tracker.Sample(0, 1_000_000);
        now += TimeSpan.FromSeconds(1);
        Assert.Null(tracker.Sample(0, 1_000_000)); // nothing moving

        now += TimeSpan.FromSeconds(1);
        var s = tracker.Sample(500_000, 1_000_000);
        Assert.NotNull(s); // moving again
        Assert.Equal(2, s.Value.Eta!.Value.TotalSeconds, precision: 1);
    }
}
