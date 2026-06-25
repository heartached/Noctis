using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class AudioKeepAliveTests
{
    [Fact]
    public void WasapiSilenceKeepAlive_ImplementsIAudioKeepAlive()
    {
        Assert.True(typeof(IAudioKeepAlive).IsAssignableFrom(typeof(WasapiSilenceKeepAlive)));
    }

    [Fact]
    public void VlcSilenceKeepAlive_ImplementsIAudioKeepAlive()
    {
        Assert.True(typeof(IAudioKeepAlive).IsAssignableFrom(typeof(VlcSilenceKeepAlive)));
    }

    [Fact]
    public void TryStart_ReturnsNull_WhenDisabledByEnv()
    {
        var prev = Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE");
        Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", "0");
        try
        {
            // Env gate is checked before the LibVLC argument is used, so null is safe here.
            Assert.Null(VlcSilenceKeepAlive.TryStart(null!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", prev);
        }
    }

    [Fact]
    public void TryStart_ReturnsNull_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return; // Windows-only assertion: the native keep-alive is macOS/Linux.

        var prev = Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE");
        Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", null);
        try
        {
            // The OS gate returns null before the LibVLC argument is used.
            Assert.Null(VlcSilenceKeepAlive.TryStart(null!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", prev);
        }
    }
}
