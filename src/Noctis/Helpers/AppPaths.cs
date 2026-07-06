namespace Noctis.Helpers;

/// <summary>
/// Centralized lookup for Noctis's per-user data directory. Honors the
/// NOCTIS_DATA_DIR environment variable so dev builds can be pointed at a
/// different folder than the installed copy and avoid clobbering each
/// other's library/settings/playlists. Debug builds additionally default to
/// a separate "Noctis-dev" profile (seeded once from the installed profile).
/// </summary>
public static class AppPaths
{
    public static string DataRoot { get; } = ResolveDataRoot();

    private static string ResolveDataRoot()
    {
        var env = Environment.GetEnvironmentVariable("NOCTIS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#if DEBUG
        var devRoot = Path.Combine(appData, "Noctis-dev");
        SeedDevProfileIfMissing(Path.Combine(appData, "Noctis"), devRoot);
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
                "play_history.json"
            })
            {
                var src = Path.Combine(releaseRoot, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(devRoot, name));
            }

            foreach (var dirName in new[]
            {
                "artwork", "playlist_covers", "profile", "artist_bios", "lyrics_cache"
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
