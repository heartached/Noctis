using LibVLCSharp.Shared;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// P25: LibVLCSharp imports "libvlc", which .NET probes on Linux as libvlc.so
/// only, a name distros ship solely in libvlc-dev / vlc-devel. Tarball builds on
/// a system with just the vlc runtime (libvlc.so.5) refused to start. The
/// fallback must map LibVLCSharp's libvlc import to the libvlc 3 soname and
/// leave every other import alone.
/// </summary>
public class VlcLinuxSonameTests
{
    [Fact]
    public void LibVlcSharpLibVlcImport_FallsBackToVersionedSoname()
    {
        Assert.Equal("libvlc.so.5",
            VlcAudioPlayer.LinuxLibVlcFallbackSoname(typeof(Core).Assembly, "libvlc"));
    }

    [Theory]
    [InlineData("libc")]
    [InlineData("libSystem")]
    [InlineData("libvlccore")]
    public void OtherLibVlcSharpImports_AreLeftAlone(string libraryName)
    {
        Assert.Null(VlcAudioPlayer.LinuxLibVlcFallbackSoname(typeof(Core).Assembly, libraryName));
    }

    [Fact]
    public void OtherAssemblies_AreLeftAlone()
    {
        Assert.Null(VlcAudioPlayer.LinuxLibVlcFallbackSoname(typeof(VlcAudioPlayer).Assembly, "libvlc"));
    }
}
