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

    [Fact]
    public void TryStart_ReturnsNull_WhenNotOptedIn_OutsideTheAppImage()
    {
        // Default (no NOCTIS_KEEPALIVE, no NOCTIS_BUNDLED_VLC): the silent-loop
        // keep-alive must not start. Windows uses WasapiSilenceKeepAlive instead;
        // on macOS it corrupts CoreAudio output, and on Linux system-libvlc
        // installs with a split plugin set it spammed
        // "VLC is unable to open the MRL '...silence.wav'" at launch (issue #26).
        // Only the Linux AppImage runs it by default (GitHub #70, see
        // ShouldStartKeepAlive). On the Linux/macOS CI legs this is THE
        // regression test for that gate.
        var prev = Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE");
        var prevBundled = Environment.GetEnvironmentVariable("NOCTIS_BUNDLED_VLC");
        Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", null);
        Environment.SetEnvironmentVariable("NOCTIS_BUNDLED_VLC", null);
        try
        {
            // All gates fire before the LibVLC argument is used, so null is safe.
            Assert.Null(VlcSilenceKeepAlive.TryStart(null!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", prev);
            Environment.SetEnvironmentVariable("NOCTIS_BUNDLED_VLC", prevBundled);
        }
    }

    [Theory]
    [InlineData(false, null, null, false)]  // macOS: opt-in only
    [InlineData(false, null, "1", false)]
    [InlineData(false, "1", null, true)]
    [InlineData(true, null, null, false)]   // Linux system libvlc: opt-in only (issue #26)
    [InlineData(true, "", "", false)]
    [InlineData(true, null, "0", false)]
    [InlineData(true, "1", null, true)]
    [InlineData(true, null, "1", true)]     // Linux AppImage: on by default (GitHub #70)
    [InlineData(true, "", "1", true)]
    [InlineData(true, "0", "1", false)]     // NOCTIS_KEEPALIVE=0 still opts out
    [InlineData(false, "0", null, false)]
    public void ShouldStartKeepAlive_MatchesPlatformBundleAndEnv(
        bool isLinux, string? keepAliveEnv, string? bundledVlcEnv, bool expected)
    {
        Assert.Equal(expected, VlcSilenceKeepAlive.ShouldStartKeepAlive(isLinux, keepAliveEnv, bundledVlcEnv));
    }

    [Fact]
    public void TryStart_ReturnsNull_OnWindows_EvenWhenOptedIn()
    {
        if (!OperatingSystem.IsWindows())
            return; // opt-in actually constructs on macOS/Linux — Windows-only gate test.

        var prev = Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE");
        Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", "1");
        try
        {
            // The OS gate wins over the opt-in: Windows always uses the WASAPI path.
            Assert.Null(VlcSilenceKeepAlive.TryStart(null!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", prev);
        }
    }
}
