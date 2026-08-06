using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Developer Mode version manager: the "Latest" pill must sit on the newest
/// stable release on GitHub — not on whichever version happens to be running.
/// (Regression: the pill was bound to the installed row, so a user on 1.3.8
/// still saw "Latest" on 1.3.8 after 1.3.9 shipped.)
/// </summary>
public class LatestReleaseBadgeTests
{
    private static ReleaseListItem Release(string tag, bool prerelease = false)
    {
        var version = Version.Parse(tag.TrimStart('v', 'V').Split('-')[0]);
        return new ReleaseListItem
        {
            Version = version,
            Info = new UpdateInfo
            {
                TagName = tag,
                Version = version,
                IsPrerelease = prerelease,
                ReleaseUrl = $"https://github.com/heartached/Noctis/releases/tag/{tag}"
            }
        };
    }

    [Fact]
    public void PickLatestRelease_picksNewestStable_notAnOlderInstalledOne()
    {
        var releases = new[] { Release("v1.3.9"), Release("v1.3.8"), Release("v1.3.7") };
        Assert.Equal("v1.3.9", UpdateService.PickLatestRelease(releases)!.Info.TagName);
    }

    [Fact]
    public void PickLatestRelease_neverLandsOnAPrerelease()
    {
        // Matches GitHub's own badge: a newer pre-release doesn't take "Latest".
        var releases = new[]
        {
            Release("v1.4.0-prerelease", prerelease: true),
            Release("v1.3.9"),
            Release("v1.3.8")
        };
        Assert.Equal("v1.3.9", UpdateService.PickLatestRelease(releases)!.Info.TagName);
    }

    [Fact]
    public void PickLatestRelease_comparesVersionsNumerically_regardlessOfInputOrder()
    {
        // "1.3.10" sorts before "1.3.9" as a string but is the newer version.
        var releases = new[] { Release("v1.3.9"), Release("v1.3.10"), Release("v1.2.0") };
        Assert.Equal("v1.3.10", UpdateService.PickLatestRelease(releases)!.Info.TagName);
    }

    [Fact]
    public void PickLatestRelease_returnsNullWhenEveryReleaseIsAPrerelease()
    {
        var releases = new[] { Release("v1.4.0-prerelease", prerelease: true) };
        Assert.Null(UpdateService.PickLatestRelease(releases));
    }

    [Fact]
    public void PickLatestRelease_returnsNullForEmptyList()
        => Assert.Null(UpdateService.PickLatestRelease(Array.Empty<ReleaseListItem>()));

    [Theory]
    [InlineData(true, true, false)]   // running the latest release: "Latest" pill only
    [InlineData(true, false, true)]   // running an older release: its row shows "Installed"
    [InlineData(false, true, false)]  // newest release, not running: "Latest" pill only
    [InlineData(false, false, false)] // any other row: no pill
    public void InstalledBadge_showsOnlyOnTheRunningRow_andYieldsToLatest(
        bool isCurrent, bool isLatest, bool expected)
    {
        var item = new DevReleaseItem
        {
            TagName = "v1.3.8",
            VersionDisplay = "1.3.8",
            IsCurrent = isCurrent,
            IsLatest = isLatest,
            Info = Release("v1.3.8").Info
        };
        Assert.Equal(expected, item.ShowInstalledBadge);
    }
}
