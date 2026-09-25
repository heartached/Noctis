using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>Windows 10 dark caption: the DWM attribute id moved from 19 to 20 at build 18985.</summary>
public class Win10DarkTitleBarTests
{
    [Theory]
    [InlineData(10240, null)]  // 1507: no dark caption support
    [InlineData(17134, null)]  // 1803
    [InlineData(17763, 19u)]   // 1809
    [InlineData(18363, 19u)]   // 1909
    [InlineData(18985, 20u)]   // first build with the documented id
    [InlineData(19045, 20u)]   // 22H2, the reporter's likely build
    [InlineData(21999, 20u)]
    [InlineData(22000, null)]  // Windows 11: Avalonia sets it itself
    [InlineData(26200, null)]
    public void AttributeForBuild_MapsWindowsBuilds(int build, uint? expected)
        => Assert.Equal(expected, Win10DarkTitleBar.AttributeForBuild(build));
}
