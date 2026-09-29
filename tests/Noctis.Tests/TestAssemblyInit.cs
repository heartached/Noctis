using System;
using System.IO;
using System.Runtime.CompilerServices;
using Noctis.Helpers;
using Noctis.Services;

namespace Noctis.Tests;

/// <summary>
/// Mirrors what Program.Main does before the first log line: the core's log header
/// is app-agnostic (it names the entry assembly), and the desktop app installs its
/// own describer (version, release channel, install source). Tests that read the
/// header expect the app's version of it, so install it before any test writes.
/// <para>
/// Also points the data root at a throw-away folder before anything reads it. Without
/// this every run wrote lyrics_cache/*.lrc, app_written_sidecars.json and logs into the
/// developer's real profile (%APPDATA%/Noctis-dev on Debug, the installed app's
/// %APPDATA%/Noctis on Release).
/// </para>
/// </summary>
internal static class TestAssemblyInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", "data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // The env var too, so anything that re-resolves the root lands in the same folder.
        Environment.SetEnvironmentVariable("NOCTIS_DATA_DIR", root);
        AppPaths.OverrideDataRoot(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        };

        DebugLog.DescribeBuild = UpdateService.DescribeBuild;
    }
}
