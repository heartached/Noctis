using System.Diagnostics;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

// Discord (Spark, 09-02, Steam Deck AppImage): "Open Data Folder" did nothing.
// The AppImage's AppRun exports LD_LIBRARY_PATH / VLC_PLUGIN_PATH for our
// process and every child inherited them, so the host's xdg-open / file
// manager loaded the bundled Ubuntu libraries and died — silently, because
// the launcher swallowed every failure. Host tools now start with the
// AppImage runtime scrubbed from their environment.
public class PlatformHelperAppImageTests
{
    private const string AppDir = "/tmp/.mount_NoctisAbc123";

    [Fact]
    public void StripAppDirEntries_DropsOnlyBundledEntries_KeepsOrder()
    {
        var kept = PlatformHelper.StripAppDirEntries(
            $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc:/usr/lib:/home/deck/lib", AppDir);
        Assert.Equal("/usr/lib:/home/deck/lib", kept);
    }

    [Fact]
    public void StripAppDirEntries_WithoutKnownAppDir_DropsMountEntries()
    {
        var kept = PlatformHelper.StripAppDirEntries($"{AppDir}/usr/lib:/usr/lib", null);
        Assert.Equal("/usr/lib", kept);
    }

    [Fact]
    public void Scrub_RemovesBundledLibraryPaths_AndVlcPluginPath()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["LD_LIBRARY_PATH"] = $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc:/usr/lib";
        psi.Environment["VLC_PLUGIN_PATH"] = $"{AppDir}/usr/lib/vlc/plugins";
        psi.Environment["NOCTIS_BUNDLED_VLC"] = "1";
        psi.Environment["XDG_DATA_DIRS"] = "/usr/share";

        PlatformHelper.ScrubAppImageEnvironment(psi, AppDir, "/home/deck/Noctis.AppImage");

        Assert.Equal("/usr/lib", psi.Environment["LD_LIBRARY_PATH"]);
        Assert.False(psi.Environment.ContainsKey("VLC_PLUGIN_PATH"));
        Assert.False(psi.Environment.ContainsKey("NOCTIS_BUNDLED_VLC"));
        Assert.Equal("/usr/share", psi.Environment["XDG_DATA_DIRS"]);
    }

    [Fact]
    public void Scrub_RemovesTheVariable_WhenOnlyBundledEntriesRemain()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["LD_LIBRARY_PATH"] = $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc";
        PlatformHelper.ScrubAppImageEnvironment(psi, AppDir, null);
        Assert.False(psi.Environment.ContainsKey("LD_LIBRARY_PATH"));
    }

    [Fact]
    public void Scrub_IsNoOp_OutsideAnAppImage()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["LD_LIBRARY_PATH"] = "/opt/custom/lib";
        psi.Environment["VLC_PLUGIN_PATH"] = "/opt/vlc/plugins";
        PlatformHelper.ScrubAppImageEnvironment(psi, null, null);
        Assert.Equal("/opt/custom/lib", psi.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("/opt/vlc/plugins", psi.Environment["VLC_PLUGIN_PATH"]);
    }
}
