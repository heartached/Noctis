using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Speed + ETA on official plugin downloads (Settings → Plugins → Get):
/// "12.4 Mbit/s · about 32 min left" next to the row's progress bar.
/// </summary>
public class PluginDownloadSpeedTests
{
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
        Assert.Equal(500_000 * 8 / 0.6 / 1_000_000, s.Value.MegabitsPerSec, precision: 3);
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
    }

    [Fact]
    public void Tracker_UnknownTotal_OmitsEta()
    {
        var now = DateTime.UtcNow;
        var tracker = new DownloadSpeedTracker(() => now);
        tracker.Sample(0, 0);
        now += TimeSpan.FromSeconds(1);
        var s = tracker.Sample(500_000, 0);
        Assert.NotNull(s);
        Assert.Null(s.Value.Eta);
    }
}
