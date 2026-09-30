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
}
