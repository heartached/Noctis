using Microsoft.Data.Sqlite;
using Noctis.Services;

namespace Noctis.Services.AudioAnalysis;

/// <summary>SQLite-backed analysis cache in the same library.db used by the track index.</summary>
public sealed class AudioAnalysisStore : IAudioAnalysisStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public AudioAnalysisStore(IPersistenceService persistence)
    {
        var dbPath = Path.Combine(persistence.DataDirectory, "library.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    private async Task InitAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS track_analysis (
                    file_path TEXT PRIMARY KEY,
                    file_size INTEGER NOT NULL,
                    last_modified_utc TEXT NOT NULL,
                    bpm INTEGER NOT NULL,
                    bpm_confidence REAL NOT NULL,
                    musical_key TEXT NOT NULL,
                    key_confidence REAL NOT NULL,
                    analyzed_utc TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<TrackAnalysisRecord?> GetAsync(string filePath, CancellationToken ct)
    {
        await InitAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path,file_size,last_modified_utc,bpm,bpm_confidence,musical_key,key_confidence,analyzed_utc FROM track_analysis WHERE file_path=$p;";
        cmd.Parameters.AddWithValue("$p", filePath);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new TrackAnalysisRecord(
            r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3),
            r.GetDouble(4), r.GetString(5), r.GetDouble(6), r.GetString(7));
    }

    public async Task UpsertAsync(TrackAnalysisRecord record, CancellationToken ct)
    {
        await InitAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO track_analysis (file_path,file_size,last_modified_utc,bpm,bpm_confidence,musical_key,key_confidence,analyzed_utc)
            VALUES ($p,$s,$m,$b,$bc,$k,$kc,$a)
            ON CONFLICT(file_path) DO UPDATE SET
                file_size=excluded.file_size, last_modified_utc=excluded.last_modified_utc,
                bpm=excluded.bpm, bpm_confidence=excluded.bpm_confidence,
                musical_key=excluded.musical_key, key_confidence=excluded.key_confidence,
                analyzed_utc=excluded.analyzed_utc;
            """;
        cmd.Parameters.AddWithValue("$p", record.FilePath);
        cmd.Parameters.AddWithValue("$s", record.FileSize);
        cmd.Parameters.AddWithValue("$m", record.LastModifiedUtc);
        cmd.Parameters.AddWithValue("$b", record.Bpm);
        cmd.Parameters.AddWithValue("$bc", record.BpmConfidence);
        cmd.Parameters.AddWithValue("$k", record.MusicalKey);
        cmd.Parameters.AddWithValue("$kc", record.KeyConfidence);
        cmd.Parameters.AddWithValue("$a", record.AnalyzedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
