namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// Finds the programs the plugin runs (yt-dlp, ffmpeg) and the JavaScript runtimes yt-dlp
/// needs (deno, node): PATH first, then on macOS the Homebrew and MacPorts folders. Noctis
/// opened from Finder or the Dock gets launchd's minimal PATH (/usr/bin:/bin:/usr/sbin:/sbin),
/// so a `brew install yt-dlp ffmpeg node` was invisible there even though it works from a
/// terminal. Noctis's own ffmpeg lookup (AudioConverterService) searches the same folders.
/// </summary>
internal static class ToolLocator
{
    /// <summary>Homebrew (Apple silicon, Intel) and MacPorts bin folders, which a GUI app's PATH lacks.</summary>
    internal static readonly string[] MacPackageManagerBinDirs = { "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" };

    /// <summary>Full path of <paramref name="exe"/> on PATH or (macOS) in the package-manager folders, or null.</summary>
    public static string? Find(string exe) =>
        Find(exe, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsMacOS() ? MacPackageManagerBinDirs : Array.Empty<string>());

    internal static string? Find(string exe, string? pathVariable, IReadOnlyList<string> extraDirs)
    {
        foreach (var dir in SearchDirs(pathVariable, extraDirs))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* invalid PATH segment */ }
        }
        return null;
    }

    /// <summary>
    /// The PATH to hand yt-dlp: this process's PATH plus (macOS) the package-manager folders it
    /// lacks. yt-dlp looks up deno/node (and ffmpeg when no --ffmpeg-location is given) on its
    /// own PATH, so a runtime found here is only usable if the child can find it too.
    /// Null = leave the child's PATH alone.
    /// </summary>
    public static string? ChildPath() =>
        OperatingSystem.IsMacOS() ? ChildPath(Environment.GetEnvironmentVariable("PATH"), MacPackageManagerBinDirs) : null;

    internal static string? ChildPath(string? pathVariable, IReadOnlyList<string> extraDirs)
    {
        var dirs = SearchDirs(pathVariable, extraDirs).ToList();
        return dirs.Count == 0 ? null : string.Join(Path.PathSeparator, dirs);
    }

    private static IEnumerable<string> SearchDirs(string? pathVariable, IReadOnlyList<string> extraDirs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(d => d.Trim()).Concat(extraDirs))
        {
            if (dir.Length > 0 && seen.Add(dir.TrimEnd('/'))) yield return dir;
        }
    }
}
