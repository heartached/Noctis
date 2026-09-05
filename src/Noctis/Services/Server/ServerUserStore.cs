using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Noctis.Services.Server;

/// <summary>One account on the Noctis server. Never carries the password or its hash.</summary>
public sealed record ServerUser(string Name, bool IsAdmin, DateTime CreatedUtc, bool HasApiKey);

/// <summary>
/// Accounts for the built-in server, in their own SQLite file. Passwords are stored as
/// PBKDF2-SHA256 hashes (per-user salt, 100k iterations) and can be verified but never
/// recovered — which is why the server does not offer Subsonic's legacy md5-token login.
/// Each user may hold one API key (random 256-bit, shown once at creation/regeneration);
/// only its hash is kept here, so a leaked database does not leak working keys.
/// </summary>
public sealed class ServerUserStore
{
    public const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly string _connectionString;

    public ServerUserStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                name        TEXT PRIMARY KEY COLLATE NOCASE,
                salt        BLOB NOT NULL,
                hash        BLOB NOT NULL,
                iterations  INTEGER NOT NULL,
                is_admin    INTEGER NOT NULL DEFAULT 0,
                api_key_hash TEXT,
                created_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        return con;
    }

    public IReadOnlyList<ServerUser> List()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT name, is_admin, created_utc, api_key_hash FROM users ORDER BY name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var list = new List<ServerUser>();
        while (r.Read())
            list.Add(new ServerUser(r.GetString(0), r.GetInt64(1) != 0, DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind), !r.IsDBNull(3)));
        return list;
    }

    public bool Exists(string name)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM users WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Creates a user. Names are trimmed, case-insensitive, 1–64 chars; passwords at least 8 chars.</summary>
    public void Create(string name, string password, bool isAdmin = false)
    {
        name = ValidateName(name);
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Hash(password, salt, Iterations);
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO users (name, salt, hash, iterations, is_admin, created_utc) VALUES ($n, $s, $h, $i, $a, $c)";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$i", Iterations);
        cmd.Parameters.AddWithValue("$a", isAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException($"A user named '{name}' already exists."); }
    }

    public bool Delete(string name)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    public void ChangePassword(string name, string newPassword)
    {
        ValidatePassword(newPassword);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Hash(newPassword, salt, Iterations);
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE users SET salt = $s, hash = $h, iterations = $i WHERE name = $n";
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$i", Iterations);
        cmd.Parameters.AddWithValue("$n", name.Trim());
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"No user '{name}'.");
    }

    /// <summary>The user when <paramref name="password"/> matches; null otherwise. Constant-time compare.</summary>
    public ServerUser? Verify(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(name) || password is null) return null;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT salt, hash, iterations, is_admin, created_utc, api_key_hash FROM users WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var salt = (byte[])r[0];
        var stored = (byte[])r[1];
        var iterations = (int)r.GetInt64(2);
        var candidate = Hash(password, salt, iterations);
        if (!CryptographicOperations.FixedTimeEquals(candidate, stored)) return null;
        return new ServerUser(name.Trim(), r.GetInt64(3) != 0, DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), !r.IsDBNull(5));
    }

    /// <summary>Issues a new API key for the user and returns it — the only time it is visible.</summary>
    public string RegenerateApiKey(string name)
    {
        var key = "nk_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE users SET api_key_hash = $k WHERE name = $n";
        cmd.Parameters.AddWithValue("$k", HashApiKey(key));
        cmd.Parameters.AddWithValue("$n", name.Trim());
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"No user '{name}'.");
        return key;
    }

    public void RevokeApiKey(string name)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE users SET api_key_hash = NULL WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.ExecuteNonQuery();
    }

    /// <summary>The user owning <paramref name="apiKey"/>, or null.</summary>
    public ServerUser? ByApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT name, is_admin, created_utc FROM users WHERE api_key_hash = $k";
        cmd.Parameters.AddWithValue("$k", HashApiKey(apiKey.Trim()));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ServerUser(r.GetString(0), r.GetInt64(1) != 0, DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind), true);
    }

    public static byte[] Hash(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static string HashApiKey(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ValidateName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 64) throw new ArgumentException("User name must be 1–64 characters.");
        if (name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '?' or '&' or '=')) throw new ArgumentException("User name contains characters that are not allowed.");
        return name;
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length < 8) throw new ArgumentException("Password must be at least 8 characters.");
    }
}
