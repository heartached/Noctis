using System.Runtime.InteropServices;

namespace Noctis.Services;

/// <summary>
/// Keeps libvlc's plugins cache (plugins/plugins.dat) built for the bundled Windows plugins.
///
/// The VideoLAN.LibVLC.Windows package ships no plugins.dat. Without it libvlc's module
/// bank loads every plugin DLL to read its descriptor and keeps them all mapped: 324 of the
/// 330 plugin DLLs sat in the process at idle on Home (552 modules), costing private bytes
/// and startup time. With the cache only the modules actually used are loaded (3 at idle).
/// Measured 11-03, Debug, same data copy, idle on Home after 60 s: private bytes
/// 336 -> 303 MB, modules 552 -> 218, startup "libvlc-warm" 262 -> 132 ms; after playing a
/// FLAC, an MP3 and an ALAC file 58 plugins were loaded instead of 323 (468 -> 441 MB).
///
/// libvlc writes the cache itself when started with --reset-plugins-cache. A fingerprint
/// of the plugin files (relative path, size, write time) kept beside the cache says when it
/// must be rebuilt: first run, or an update that changed the plugins. A stale cache is
/// still safe (libvlc checks every entry's size and write time and loads a mismatched
/// plugin directly), it just stops saving memory. A plugin folder that is not writable
/// (an elevated install under Program Files) only means libvlc fails to save the cache and
/// keeps loading every plugin, which is how it always ran.
/// </summary>
internal static class VlcPluginCache
{
    internal const string CacheFileName = "plugins.dat";
    internal const string StampFileName = "plugins.dat.noctis";
    internal const string ResetArg = "--reset-plugins-cache";

    /// <summary>The bundled plugins folder on Windows, or null (other platforms, or not found).</summary>
    internal static string? BundledPluginsDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => null,
        };
        if (arch == null) return null;
        var dir = Path.Combine(AppContext.BaseDirectory, "libvlc", arch, "plugins");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>A string that changes whenever any plugin DLL is added, removed or replaced.</summary>
    internal static string Fingerprint(string pluginsDir)
    {
        var entries = new List<string>();
        foreach (var file in new DirectoryInfo(pluginsDir).EnumerateFiles("*.dll", SearchOption.AllDirectories))
            entries.Add($"{Path.GetRelativePath(pluginsDir, file.FullName)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', entries);
    }

    /// <summary>True when libvlc should rebuild the cache on this start.</summary>
    internal static bool NeedsRebuild(string pluginsDir, string fingerprint)
    {
        try
        {
            if (!File.Exists(Path.Combine(pluginsDir, CacheFileName))) return true;
            var stamp = Path.Combine(pluginsDir, StampFileName);
            return !File.Exists(stamp) || File.ReadAllText(stamp) != fingerprint;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// After a rebuild: records the fingerprint once libvlc has written a cache at or after
    /// <paramref name="rebuildStartedUtc"/>. Nothing is recorded when the save failed, so the
    /// next start tries again.
    /// </summary>
    internal static bool RecordBuilt(string pluginsDir, string fingerprint, DateTime rebuildStartedUtc)
    {
        try
        {
            var cache = new FileInfo(Path.Combine(pluginsDir, CacheFileName));
            // Write times can round to the filesystem's resolution; allow a couple of seconds.
            if (!cache.Exists || cache.LastWriteTimeUtc < rebuildStartedUtc.AddSeconds(-2)) return false;
            File.WriteAllText(Path.Combine(pluginsDir, StampFileName), fingerprint);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
