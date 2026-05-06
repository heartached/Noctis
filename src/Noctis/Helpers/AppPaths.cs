namespace Noctis.Helpers;

/// <summary>
/// Centralized lookup for Noctis's per-user data directory. Honors the
/// NOCTIS_DATA_DIR environment variable so dev builds can be pointed at a
/// different folder than the installed copy and avoid clobbering each
/// other's library/settings/playlists.
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
        return Path.Combine(appData, "Noctis");
    }
}
