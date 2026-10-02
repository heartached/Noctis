using Microsoft.Data.Sqlite;

namespace Noctis.Plugins.Mixxx;

/// <summary>One track of Mixxx's library: where the file is, its BPM and its key.</summary>
/// <param name="Location">Absolute path as Mixxx stores it (Qt style: '/' separators on every OS).</param>
/// <param name="Bpm">library.bpm; 0 = not analysed.</param>
/// <param name="KeyId">library.key_id (keys.proto ChromaticKey); 0 = none.</param>
public sealed record MixxxTrack(string Location, double Bpm, int KeyId);

/// <summary>
/// Finds and reads Mixxx's library database, <c>mixxxdb.sqlite</c> in Mixxx's settings folder.
/// Read-only: the file is opened with SQLite's read-only mode and never written, so it is safe
/// while Mixxx runs (Mixxx keeps SQLite's default rollback journal; a reader only takes a
/// shared lock for the length of one query).
/// </summary>
public static class MixxxDatabase
{
    public const string FileName = "mixxxdb.sqlite";

    /// <summary>Where Mixxx keeps its library on <paramref name="platform"/> ("windows", "macos",
    /// "linux"), most likely first. From the Mixxx manual, "The Mixxx Settings Directory".</summary>
    public static IReadOnlyList<string> CandidatePaths(string platform, string home, string localAppData) => platform switch
    {
        "windows" => new[] { Path.Combine(localAppData, "Mixxx", FileName) },
        "macos" => new[]
        {
            // Mixxx 2.3 and later run sandboxed.
            Path.Combine(home, "Library", "Containers", "org.mixxx.mixxx", "Data", "Library", "Application Support", "Mixxx", FileName),
            Path.Combine(home, "Library", "Application Support", "Mixxx", FileName),
        },
        _ => new[]
        {
            Path.Combine(home, ".mixxx", FileName),
            // Flatpak (org.mixxx.Mixxx).
            Path.Combine(home, ".var", "app", "org.mixxx.Mixxx", ".mixxx", FileName),
        },
    };

    /// <summary>The first candidate that exists on this computer, else the most likely one.</summary>
    public static string DefaultPath()
    {
        var candidates = CandidatePaths(CurrentPlatform,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    /// <summary>
    /// The file to read for the "databasePath" setting: empty means <see cref="DefaultPath"/>;
    /// quotes around a pasted path, environment variables and a leading "~/" are expanded, and a
    /// folder means the <see cref="FileName"/> inside it.
    /// </summary>
    public static string ResolvePath(string? configured, Func<string> defaultPath, string home)
    {
        var path = (configured ?? "").Trim().Trim('"').Trim();
        if (path.Length == 0) return defaultPath();
        path = Environment.ExpandEnvironmentVariables(path);
        if (path == "~") path = home;
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.GetFullPath(Path.Combine(home, path[2..])); // GetFullPath: one separator style
        return Directory.Exists(path) ? Path.Combine(path, FileName) : path;
    }

    /// <summary>
    /// Every track Mixxx still lists, with its BPM and key id. Tracks the user removed from
    /// Mixxx (mixxx_deleted) or whose file Mixxx found missing (fs_deleted) are left out.
    /// Works with any schema since key_id was added (revision 19); older files just have no keys.
    /// </summary>
    /// <exception cref="FileNotFoundException">No file at <paramref name="path"/>.</exception>
    /// <exception cref="InvalidDataException">The file is a database but not Mixxx's library.</exception>
    /// <exception cref="SqliteException">Not a database, or unreadable.</exception>
    public static IReadOnlyList<MixxxTrack> Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Mixxx library not found.", path);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            // No pool: the file handle closes with the connection instead of lingering.
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var library = Columns(connection, "library");
        var locations = Columns(connection, "track_locations");
        if (!library.Contains("bpm") || !library.Contains("location") || !locations.Contains("id") || !locations.Contains("location"))
            throw new InvalidDataException("This file is not a Mixxx library (no library/track_locations tables).");

        // library.location holds track_locations.id (schema revision 3 and later).
        var keyId = library.Contains("key_id") ? "library.key_id" : "0";
        var filters = "";
        if (library.Contains("mixxx_deleted")) filters += " AND COALESCE(library.mixxx_deleted, 0) = 0";
        if (locations.Contains("fs_deleted")) filters += " AND COALESCE(track_locations.fs_deleted, 0) = 0";

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT track_locations.location, library.bpm, {keyId} " +
            "FROM library INNER JOIN track_locations ON library.location = track_locations.id " +
            $"WHERE track_locations.location IS NOT NULL{filters}";
        var tracks = new List<MixxxTrack>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var location = reader.GetString(0);
            var bpm = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
            var key = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            tracks.Add(new MixxxTrack(location, bpm, key is >= int.MinValue and <= int.MaxValue ? (int)key : 0));
        }
        return tracks;
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    /// <summary>"windows", "macos" or "linux".</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}
