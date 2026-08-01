using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Issue #26: forcing --demux=avformat against a system libvlc whose distro
/// splits the ffmpeg plugins into optional packages (Arch: vlc-plugin-ffmpeg)
/// makes EVERY media open fail with "VLC is unable to open the MRL" even though
/// the file exists. The flag is only safe where the avformat plugin is
/// guaranteed: the bundled Windows/macOS payloads, and the Linux AppImage
/// (whose AppRun declares the bundle via NOCTIS_BUNDLED_VLC=1).
/// </summary>
public class VlcDemuxPolicyTests
{
    [Theory]
    [InlineData(false, null, true)]  // Windows/macOS bundled payloads: always force
    [InlineData(false, "1", true)]
    [InlineData(true, null, false)]  // Linux system libvlc: never force
    [InlineData(true, "", false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]    // Linux AppImage (bundled plugin set): force
    public void ShouldForceAvformatDemux_MatchesPlatformAndBundle(
        bool isLinux, string? bundledVlcEnv, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.ShouldForceAvformatDemux(isLinux, bundledVlcEnv));
    }
}
