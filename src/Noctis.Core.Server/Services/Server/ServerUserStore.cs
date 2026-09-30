using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Noctis.Services.Server;

/// <summary>One account on the Noctis server. Never carries the password or its hash.</summary>
public sealed record ServerUser(string Name, bool IsAdmin, DateTime CreatedUtc, bool HasApiKey);

/// <summary>A device signed in to an account (noctisSignIn). Never carries its key or the key's hash.</summary>
public sealed record ServerDevice(string User, string DeviceId, string DeviceName, DateTime CreatedUtc, DateTime? LastUsedUtc);

/// <summary>
/// Accounts for the built-in server, in their own SQLite file. Passwords are stored as
/// PBKDF2-SHA256 hashes (per-user salt, 100k iterations) and can be verified but never
/// recovered — which is why the server does not offer Subsonic's legacy md5-token login.
/// Each user may hold one API key (random 256-bit, shown once at creation/regeneration);
/// only its hash is kept here, so a leaked database does not leak working keys.
/// Signed-in devices get their own key each (one per user + device id), so one phone can be
/// signed out or removed without touching the others.
/// </summary>
public sealed class ServerUserStore
{
    public const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>A device key's last-used time is written at most this often (every request would be a DB write).</summary>
    public static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);

    // Verify() runs PBKDF2 against this for unknown names so "no such user" takes as long as
    // "wrong password" and response timing does not reveal which account names exist.
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(SaltBytes);
    private static readonly byte[] DummyHash = RandomNumberGenerator.GetBytes(HashBytes);

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
            CREATE TABLE IF NOT EXISTS device_keys (
                user_name     TEXT NOT NULL COLLATE NOCASE,
                device_id     TEXT NOT NULL,
                device_name   TEXT NOT NULL,
                key_hash      TEXT NOT NULL UNIQUE,
                created_utc   TEXT NOT NULL,
                last_used_utc TEXT,
                PRIMARY KEY (user_name, device_id)
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

    /// <summary>Deletes the user and every device signed in to it.</summary>
    public bool Delete(string name)
    {
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM device_keys WHERE user_name = $n";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM users WHERE name = $n";
        var deleted = cmd.ExecuteNonQuery() > 0;
        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// Sets a new password and signs everything out: the account's API key and every device key
    /// are revoked (a password change is what an owner does after losing a phone).
    /// </summary>
    public void ChangePassword(string name, string newPassword)
    {
        ValidatePassword(newPassword);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Hash(newPassword, salt, Iterations);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE users SET salt = $s, hash = $h, iterations = $i, api_key_hash = NULL WHERE name = $n";
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$i", Iterations);
        cmd.Parameters.AddWithValue("$n", name.Trim());
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"No user '{name}'.");
        cmd.CommandText = "DELETE FROM device_keys WHERE user_name = $n";
        cmd.ExecuteNonQuery();
        tx.Commit();
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
        if (!r.Read())
        {
            // Same work as a wrong password, so timing does not tell which names exist.
            CryptographicOperations.FixedTimeEquals(Hash(password, DummySalt, Iterations), DummyHash);
            return null;
        }
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

    /// <summary>The user owning <paramref name="apiKey"/> (the account's own key or a signed-in device's), or null.</summary>
    public ServerUser? ByApiKey(string? apiKey) => ByApiKey(apiKey, out _);

    /// <summary>
    /// As <see cref="ByApiKey(string?)"/>; <paramref name="device"/> is the signed-in device when
    /// the key is a device key (null for the account's own key). Keys are found by their SHA-256,
    /// so the lookup never touches a secret; a device key's last-used time is refreshed here, at
    /// most once per <see cref="LastUsedWriteInterval"/>.
    /// </summary>
    public ServerUser? ByApiKey(string? apiKey, out ServerDevice? device)
    {
        device = null;
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var hash = HashApiKey(apiKey.Trim());
        using var con = Open();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name, is_admin, created_utc, api_key_hash FROM users WHERE api_key_hash = $k";
            cmd.Parameters.AddWithValue("$k", hash);
            using var r = cmd.ExecuteReader();
            if (r.Read() && SameHash(r.GetString(3), hash))
                return new ServerUser(r.GetString(0), r.GetInt64(1) != 0, ParseUtc(r.GetString(2)), true);
        }

        ServerUser user;
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                SELECT u.name, u.is_admin, u.created_utc, u.api_key_hash, d.device_id, d.device_name, d.created_utc, d.last_used_utc, d.key_hash
                FROM device_keys d JOIN users u ON u.name = d.user_name
                WHERE d.key_hash = $k
                """;
            cmd.Parameters.AddWithValue("$k", hash);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || !SameHash(r.GetString(8), hash)) return null;
            user = new ServerUser(r.GetString(0), r.GetInt64(1) != 0, ParseUtc(r.GetString(2)), !r.IsDBNull(3));
            device = new ServerDevice(user.Name, r.GetString(4), r.GetString(5), ParseUtc(r.GetString(6)), r.IsDBNull(7) ? null : ParseUtc(r.GetString(7)));
        }

        var now = DateTime.UtcNow;
        if (device.LastUsedUtc is not { } last || now - last >= LastUsedWriteInterval)
        {
            using var touch = con.CreateCommand();
            touch.CommandText = "UPDATE device_keys SET last_used_utc = $t WHERE key_hash = $k";
            touch.Parameters.AddWithValue("$t", now.ToString("O"));
            touch.Parameters.AddWithValue("$k", hash);
            touch.ExecuteNonQuery();
            device = device with { LastUsedUtc = now };
        }
        return user;
    }

    // ── Signed-in devices (noctisSignIn) ──

    /// <summary>Most devices one account may have signed in; signing in another evicts the least recently used.</summary>
    public const int MaxDevicesPerUser = 20;

    /// <summary>
    /// Issues a key for <paramref name="deviceId"/> on <paramref name="user"/> and returns it — the
    /// only time it is visible. Signing in again from the same device replaces its key (the old one
    /// stops working). Past <see cref="MaxDevicesPerUser"/> the least recently used device is signed
    /// out, so a forgotten old phone never blocks a new one. The caller has already verified the
    /// password and validated the device fields.
    /// </summary>
    public string IssueDeviceKey(string user, string deviceId, string deviceName)
    {
        var key = "nk_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow.ToString("O");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO device_keys (user_name, device_id, device_name, key_hash, created_utc, last_used_utc)
            SELECT name, $d, $dn, $k, $t, $t FROM users WHERE name = $n
            ON CONFLICT(user_name, device_id) DO UPDATE SET device_name = $dn, key_hash = $k, created_utc = $t, last_used_utc = $t
            """;
        cmd.Parameters.AddWithValue("$n", user.Trim());
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$dn", deviceName);
        cmd.Parameters.AddWithValue("$k", HashApiKey(key));
        cmd.Parameters.AddWithValue("$t", now);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"No user '{user}'.");
        cmd.CommandText = """
            DELETE FROM device_keys WHERE user_name = $n AND device_id NOT IN (
                SELECT device_id FROM device_keys WHERE user_name = $n
                ORDER BY COALESCE(last_used_utc, created_utc) DESC LIMIT $max)
            """;
        cmd.Parameters.AddWithValue("$max", MaxDevicesPerUser);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return key;
    }

    /// <summary>Devices signed in to any account, most recently used first.</summary>
    public IReadOnlyList<ServerDevice> Devices()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT user_name, device_id, device_name, created_utc, last_used_utc FROM device_keys ORDER BY COALESCE(last_used_utc, created_utc) DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<ServerDevice>();
        while (r.Read())
            list.Add(new ServerDevice(r.GetString(0), r.GetString(1), r.GetString(2), ParseUtc(r.GetString(3)), r.IsDBNull(4) ? null : ParseUtc(r.GetString(4))));
        return list;
    }

    /// <summary>Signs a device out (its key stops working). False when it was not signed in.</summary>
    public bool RevokeDevice(string user, string deviceId)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM device_keys WHERE user_name = $n AND device_id = $d";
        cmd.Parameters.AddWithValue("$n", user.Trim());
        cmd.Parameters.AddWithValue("$d", deviceId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Revokes the device key <paramref name="apiKey"/> (noctisSignOut). The account's own key is not touched.</summary>
    public bool RevokeDeviceKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM device_keys WHERE key_hash = $k";
        cmd.Parameters.AddWithValue("$k", HashApiKey(apiKey.Trim()));
        return cmd.ExecuteNonQuery() > 0;
    }

    private static bool SameHash(string stored, string candidate)
        => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(stored), System.Text.Encoding.ASCII.GetBytes(candidate));

    private static DateTime ParseUtc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    public static byte[] Hash(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static string HashApiKey(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ValidateName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 64) throw new ArgumentException("Invalid user name or password.");
        if (name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '?' or '&' or '=')) throw new ArgumentException("User name contains characters that are not allowed.");
        return name;
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length < 8) throw new ArgumentException("Invalid user name or password.");
    }
}
