using System.Runtime.InteropServices;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Which macOS .dmg the updater installs (UpdateService.MacDmgArch).</summary>
public class UpdateMacArchTests
{
    [Theory]
    //          OS arch             translated  expected
    [InlineData(Architecture.Arm64, false,      "arm64")] // arm64 build on Apple Silicon
    [InlineData(Architecture.X64,   false,      "x64")]   // x64 build on an Intel Mac
    [InlineData(Architecture.X64,   true,       "arm64")] // x64 build under Rosetta moves to arm64
    [InlineData(Architecture.Arm64, true,       "arm64")] // runtime already reports Arm64 under Rosetta
    [InlineData(Architecture.X64,   null,       "x64")]   // check failed: OS architecture alone
    [InlineData(Architecture.Arm64, null,       "arm64")]
    public void MacDmgArch_PicksNativeBuild(Architecture osArch, bool? translated, string expected)
    {
        Assert.Equal(expected, UpdateService.MacDmgArch(osArch, translated));
    }

    [Fact]
    public void RosettaCheck_IsFalse_OffMacOS()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.False(MacRosetta.IsTranslated);
    }
}
