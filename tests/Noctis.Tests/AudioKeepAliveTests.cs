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
    public void TryStart_ReturnsNull_WhenNotOptedIn_OnEveryPlatform()
    {
        // Default (no NOCTIS_KEEPALIVE): the silent-loop keep-alive must not
        // start anywhere. Windows uses WasapiSilenceKeepAlive instead, and on
        // macOS/Linux the stream is opt-in — on Linux it historically poisoned
        // PulseAudio/PipeWire stream-restore (playback started muted) and, on
        // system-libvlc installs with a split plugin set, spammed
        // "VLC is unable to open the MRL '...silence.wav'" at launch (issue #26).
        // On the Linux/macOS CI legs this is THE regression test for that gate.
        var prev = Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE");
        Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", null);
        try
        {
            // All gates fire before the LibVLC argument is used, so null is safe.
            Assert.Null(VlcSilenceKeepAlive.TryStart(null!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOCTIS_KEEPALIVE", prev);
        }
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
