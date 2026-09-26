using System.IO;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// P24: on macOS a user-installed VLC.app was chosen before the libvlc payload
/// the .app bundles, and Core.Initialize gets one shot. An Intel-only VLC.app on
/// an arm64 build, or a VLC 4.x install, then failed startup even though the
/// pinned universal VLC 3 bundle sat next to the executable. The bundle must
/// win whenever it exists; VLC.app and Homebrew only serve unbundled runs.
/// </summary>
public class VlcMacLibPathTests
{
    private const string BaseDir = "/Applications/Noctis.app/Contents/MacOS/";
    private const string VlcApp = "/Applications/VLC.app/Contents/MacOS/lib";
    private static readonly string Bundled = Path.Combine(BaseDir, "libvlc", "lib");

    private static System.Func<string, bool> Present(params string[] dirs)
    {
        var files = new HashSet<string>();
        foreach (var d in dirs) files.Add(Path.Combine(d, "libvlc.dylib"));
        return files.Contains;
    }

    [Fact]
    public void BundledPayload_WinsOverInstalledVlcApp()
    {
        Assert.Equal(Bundled, VlcAudioPlayer.PickMacLibVlcDirectory(BaseDir, Present(VlcApp, Bundled)));
    }

    [Fact]
    public void BundledPayload_UsedWhenNoVlcApp()
    {
        Assert.Equal(Bundled, VlcAudioPlayer.PickMacLibVlcDirectory(BaseDir, Present(Bundled)));
    }

    [Fact]
    public void UnbundledRun_FallsBackToVlcApp()
    {
        Assert.Equal(VlcApp, VlcAudioPlayer.PickMacLibVlcDirectory(BaseDir, Present(VlcApp, "/opt/homebrew/lib")));
    }

    [Fact]
    public void UnbundledRun_FallsBackToHomebrew()
    {
        Assert.Equal("/opt/homebrew/lib", VlcAudioPlayer.PickMacLibVlcDirectory(BaseDir, Present("/opt/homebrew/lib")));
    }

    [Fact]
    public void NoLibVlcAnywhere_ReturnsNull()
    {
        Assert.Null(VlcAudioPlayer.PickMacLibVlcDirectory(BaseDir, Present()));
    }
}
