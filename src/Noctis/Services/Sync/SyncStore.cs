using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Noctis.Services.Sync;

/// <summary>
/// The sync ledger: the newest known state of every synced item, stamped with who wrote it
/// and when, plus a monotonically increasing change sequence so a device can ask "what
/// changed since N". Conflict rule is last-writer-wins on <c>updated_utc</c> (ties broken by
/// device id so every replica converges to the same answer) — the same simple model Joplin
/// uses per note. One SQLite file, same idiom as <see cref="Server.ServerUserStore"/>.
/// </summary>
public sealed class SyncStore
{
    private readonly string _connectionString;
    private readonly object _writeGate = new();

    public SyncStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS items (
                kind        TEXT NOT NULL,
                id          TEXT NOT NULL,
                payload     TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                device      TEXT NOT NULL,
                seq         INTEGER NOT NULL,
                PRIMARY KEY (kind, id)
            );
            CREATE INDEX IF NOT EXISTS items_seq ON items (seq);
            CREATE TABLE IF NOT EXISTS devices (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                last_seq      INTEGER NOT NULL DEFAULT 0
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

    /// <summary>Highest change sequence in the ledger (0 when empty).</summary>
    public long CurrentSeq
    {
        get
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM items";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public SyncItem? Get(string kind, string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT kind, id, payload, updated_utc, device, seq FROM items WHERE kind = $k AND id = $i";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>
    /// Applies an incoming state if it is newer than what is stored (or equal-time from a
    /// "greater" device id, so replicas agree). Returns true when the ledger changed; the
    /// item then carries a fresh sequence number.
    /// </summary>
    public bool Upsert(string kind, string id, string payload, DateTime updatedUtc, string device)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id)) return false;
        updatedUtc = updatedUtc.Kind == DateTimeKind.Utc ? updatedUtc : updatedUtc.ToUniversalTime();
        device ??= string.Empty;
        lock (_writeGate)
        {
            using var con = Open();
            using var tx = con.BeginTransaction();
            using (var read = con.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT updated_utc, device, payload FROM items WHERE kind = $k AND id = $i";
                read.Parameters.AddWithValue("$k", kind);
                read.Parameters.AddWithValue("$i", id);
                using var r = read.ExecuteReader();
                if (r.Read())
                {
                    var storedUtc = ParseUtc(r.GetString(0));
                    var storedDevice = r.GetString(1);
                    var storedPayload = r.GetString(2);
                    var newer = updatedUtc > storedUtc
                                || (updatedUtc == storedUtc && string.CompareOrdinal(device, storedDevice) > 0 && payload != storedPayload);
                    if (!newer) return false;
                }
            }
            long seq;
            using (var max = con.CreateCommand())
            {
                max.Transaction = tx;
                max.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM items";
                seq = Convert.ToInt64(max.ExecuteScalar(), CultureInfo.InvariantCulture) + 1;
            }
            using (var write = con.CreateCommand())
            {
                write.Transaction = tx;
                write.CommandText = """
                    INSERT INTO items (kind, id, payload, updated_utc, device, seq) VALUES ($k, $i, $p, $u, $d, $s)
                    ON CONFLICT(kind, id) DO UPDATE SET payload = $p, updated_utc = $u, device = $d, seq = $s
                    """;
                write.Parameters.AddWithValue("$k", kind);
                write.Parameters.AddWithValue("$i", id);
                write.Parameters.AddWithValue("$p", payload);
                write.Parameters.AddWithValue("$u", updatedUtc.ToString("O", CultureInfo.InvariantCulture));
                write.Parameters.AddWithValue("$d", device);
                write.Parameters.AddWithValue("$s", seq);
                write.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
    }

    /// <summary>Items whose sequence is greater than <paramref name="since"/>, oldest change first.</summary>
    public IReadOnlyList<SyncItem> ChangesSince(long since, int limit = 5000)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT kind, id, payload, updated_utc, device, seq FROM items WHERE seq > $s ORDER BY seq LIMIT $l";
        cmd.Parameters.AddWithValue("$s", since);
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 50_000));
        using var r = cmd.ExecuteReader();
        var list = new List<SyncItem>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public IReadOnlyList<SyncItem> All(string kind)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT kind, id, payload, updated_utc, device, seq FROM items WHERE kind = $k";
        cmd.Parameters.AddWithValue("$k", kind);
        using var r = cmd.ExecuteReader();
        var list = new List<SyncItem>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public void TouchDevice(string id, string? name, long lastSeq)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_writeGate)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO devices (id, name, last_seen_utc, last_seq) VALUES ($i, $n, $t, $s)
                ON CONFLICT(id) DO UPDATE SET name = CASE WHEN $n = '' THEN name ELSE $n END, last_seen_utc = $t, last_seq = MAX(last_seq, $s)
                """;
            cmd.Parameters.AddWithValue("$i", id.Trim());
            cmd.Parameters.AddWithValue("$n", (name ?? string.Empty).Trim());
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$s", lastSeq);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SyncDevice> Devices()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, name, last_seen_utc, last_seq FROM devices ORDER BY last_seen_utc DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<SyncDevice>();
        while (r.Read()) list.Add(new SyncDevice(r.GetString(0), r.GetString(1), ParseUtc(r.GetString(2)), r.GetInt64(3)));
        return list;
    }

    private static SyncItem Read(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), ParseUtc(r.GetString(3)), r.GetString(4), r.GetInt64(5));

    private static DateTime ParseUtc(string s)
    {
        var parsed = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }
}
