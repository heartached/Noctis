using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 10-27: on Windows the single-instance mutex/pipe was keyed by user name alone, so the dev
/// build (NOCTIS_DATA_DIR = Noctis-Dev) could not start while the installed Noctis ran: it
/// handed itself to that window and quit. A custom data root is now its own instance, as on
/// Unix; the default root keeps the original names.
/// </summary>
public class SingleInstanceGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultRoot_KeepsTheOriginalNames(string? dataDir) =>
        Assert.Equal("", SingleInstanceGuard.RootSuffix(dataDir));

    [Fact]
    public void CustomRoot_GetsItsOwnInstance_StableAcrossSpellings()
    {
        var dev = Path.Combine(Path.GetTempPath(), "Noctis-Dev");
        var suffix = SingleInstanceGuard.RootSuffix(dev);
        Assert.Matches("^-[0-9A-F]{8}$", suffix);
        Assert.Equal(suffix, SingleInstanceGuard.RootSuffix(dev.ToLowerInvariant() + "\\"));
        Assert.Equal(suffix, SingleInstanceGuard.RootSuffix(dev.Replace('\\', '/')));
        Assert.NotEqual(suffix, SingleInstanceGuard.RootSuffix(Path.Combine(Path.GetTempPath(), "Noctis-Other")));
    }
}
