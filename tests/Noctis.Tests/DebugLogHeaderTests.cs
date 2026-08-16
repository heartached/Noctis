using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DebugLogHeaderTests
{
    // The session-log header is what makes a pasted bug report self-diagnosing:
    // it must identify how AND where this copy is installed, so "updater does
    // nothing" reports (Portable copies in non-writable dirs, AppImage launches)
    // can be triaged without a round-trip to the user.
    [Fact]
    public void Header_Reports_Install_Source_And_Location()
    {
        var snapshot = DebugLog.Snapshot();

        Assert.Contains($"Install source: {UpdateService.Source}", snapshot);

        // Test hosts never run as an AppImage, so the location is BaseDirectory.
        Assert.Contains($"Install location: {System.AppContext.BaseDirectory}", snapshot);
    }
}
