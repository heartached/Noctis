using Noctis.Services.LyricsStudio;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Speed + time left on official plugin downloads (Settings → Plugins → Get): "4.2 MB/s ·
/// about 30 s left" next to the row's progress bar, measured and worded like Lyrics Studio's
/// model download (<see cref="DownloadMeter"/>).
/// </summary>
public class PluginDownloadSpeedTests
{
    private static ModelDownloadProgress At(long bytes, long total) => new(ModelDownloadPhase.Downloading, bytes, total);

    [Fact]
    public void SpeedText_EmptyUntilSettled_ThenRateAndTimeLeft()
    {
        var meter = new DownloadMeter();
        const long total = 100L << 20;
        meter.Sample(At(0, total), TimeSpan.Zero);
        meter.Sample(At(1L << 20, total), TimeSpan.FromSeconds(1));
        Assert.Equal(string.Empty, DownloadMeter.SpeedText(meter.Read(TimeSpan.FromSeconds(1))));

        for (var s = 2; s <= 10; s++) meter.Sample(At(s * (1L << 20), total), TimeSpan.FromSeconds(s));
        Assert.Equal("1.0 MB/s · about 2 min left", DownloadMeter.SpeedText(meter.Read(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public void SpeedText_UnknownTotal_OmitsTimeLeft()
    {
        var meter = new DownloadMeter();
        for (var s = 0; s <= 4; s++) meter.Sample(At(s * 300L * 1024, 0), TimeSpan.FromSeconds(s));
        Assert.Equal("300 KB/s", DownloadMeter.SpeedText(meter.Read(TimeSpan.FromSeconds(4))));
    }

    [Fact]
    public void EndDownload_ClearsTheSpeedLine()
    {
        var entry = Assert.Single(PluginCatalog.Parse(PluginCatalogTests.Index(PluginCatalogTests.Entry(id: "dev.test.speed"))).Plugins);
        var item = new OfficialPluginItem(entry);
        item.BeginDownload(new CancellationTokenSource());
        item.ProgressDetail = "4.2 MB/s · about 30 s left";
        Assert.True(item.HasProgressDetail);

        item.EndDownload();
        Assert.False(item.HasProgressDetail);
    }
}
