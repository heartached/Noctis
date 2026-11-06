namespace Noctis.Helpers;

/// <summary>
/// Centralized lookup for Noctis's per-user data directory. Honors the
/// NOCTIS_DATA_DIR environment variable so dev builds can be pointed at a
/// different folder than the installed copy and avoid clobbering each
/// other's library/settings/playlists. Debug builds additionally default to
/// a separate "Noctis-dev" profile (seeded once from the installed profile).
/// That folder is the HOME root; the user can move the data elsewhere (Settings ›
/// Advanced › Data Folder, GitHub #165), recorded in a pointer file in the home root
/// (<see cref="DataLocation"/>).
/// </summary>
public static class AppPaths
{
    private static readonly object _rootLock = new();
    private static string? _dataRoot;
    private static string? _homeRoot;

    /// <summary>
    /// The profile's own folder: NOCTIS_DATA_DIR when set, else the default (%APPDATA%/Noctis,
    /// Debug: Noctis-dev). It holds the data unless the user moved it, and always holds the
    /// pointer to where it went. The single-instance guard keys on it, so a moved profile is
    /// still the same app.
    /// </summary>
    public static string HomeRoot
    {
        get { lock (_rootLock) return _homeRoot ??= ResolveHomeRoot(); }
    }

    /// <summary>True when the data lives in <see cref="HomeRoot"/> (not moved, or not overridden).</summary>
    public static bool IsDataInHomeRoot => DataLocation.SamePath(DataRoot, HomeRoot);

    /// <summary>Resolved lazily so a host can <see cref="OverrideDataRoot"/> first.</summary>
    public static string DataRoot
    {
        get { lock (_rootLock) return _dataRoot ??= ResolveDataRoot(); }
    }

    /// <summary>
    /// Points the data root somewhere else (the Android head passes Context.FilesDir).
    /// Must run before any service reads <see cref="DataRoot"/>: a later call with a
    /// different root throws, because half the services would already be writing to
    /// the old one. Re-asserting the current root is a no-op.
    /// </summary>
    public static void OverrideDataRoot(string root)
    {
        var full = Path.GetFullPath(root);
        lock (_rootLock)
        {
            if (_dataRoot != null && !string.Equals(_dataRoot, full, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"AppPaths.DataRoot is already '{_dataRoot}'; override it before any service touches it.");
            _dataRoot = full;
        }
    }

    /// <summary>
    /// The home root, or the folder its pointer names. A test copy pointed at by
    /// NOCTIS_DATA_DIR has no pointer of its own, so the variable still decides. The desktop
    /// app runs <see cref="DataLocation.PrepareAtStartup"/> before anything reads this.
    /// </summary>
    private static string ResolveDataRoot() => DataLocation.ConfiguredRoot(HomeRoot);

    private static string ResolveHomeRoot()
    {
        var env = Environment.GetEnvironmentVariable("NOCTIS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            // Expand tokens like %LOCALAPPDATA% (launchSettings.json sets the dev
            // profile that way) so we never create a literal "%LOCALAPPDATA%..."
            // folder in the working directory, and resolve to an absolute path.
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(env));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#if DEBUG
        var devRoot = Path.Combine(appData, "Noctis-dev");
        // From wherever the installed profile keeps its data (it may have been moved, #165).
        SeedDevProfileIfMissing(DataLocation.ConfiguredRoot(Path.Combine(appData, "Noctis")), devRoot);
        return devRoot;
#else
        return Path.Combine(appData, "Noctis");
#endif
    }

#if DEBUG
    /// <summary>
    /// One-time copy of the installed profile's core data into the dev profile
    /// so a Debug build starts with the real library instead of empty. Heavy,
    /// regenerable caches (animated covers, audit, cache) are skipped.
    /// </summary>
    private static void SeedDevProfileIfMissing(string releaseRoot, string devRoot)
    {
        try
        {
            if (Directory.Exists(devRoot) || !Directory.Exists(releaseRoot))
                return;

            Directory.CreateDirectory(devRoot);

            foreach (var name in new[]
            {
                "settings.json", "library.json", "library.db", "library.db-wal",
                "library.db-shm", "playlists.json", "queue.json", "indexes.json",
                "play_history.json", "play_history.recent.json"
            })
            {
                var src = Path.Combine(releaseRoot, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(devRoot, name));
            }

            foreach (var dirName in new[]
            {
                "artwork", "playlist_covers", "profile", "artist_bios", "lyrics_cache",
                "lyrics_store"
            })
            {
                var src = Path.Combine(releaseRoot, dirName);
                if (Directory.Exists(src))
                    CopyDirectory(src, Path.Combine(devRoot, dirName));
            }
        }
        catch
        {
            // Best effort: a partial seed still leaves a working dev profile.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
#endif
}
