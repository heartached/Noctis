using Microsoft.Data.Sqlite;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// SQLite track index used as scalable backing store for large libraries.
/// </summary>
public sealed class SqliteLibraryIndexService : ISqliteLibraryIndexService
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _initialized;

    public SqliteLibraryIndexService(IPersistenceService persistence)
    {
        var dbPath = Path.Combine(persistence.DataDirectory, "library.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS tracks (
                    id TEXT PRIMARY KEY,
                    file_path TEXT NOT NULL,
                    title TEXT NOT NULL,
                    artist TEXT NOT NULL,
                    album TEXT NOT NULL,
                    album_artist TEXT NOT NULL,
                    genre TEXT NOT NULL,
                    year INTEGER NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    file_size INTEGER NOT NULL,
                    last_modified_utc TEXT NOT NULL,
                    date_added_utc TEXT NOT NULL,
                    play_count INTEGER NOT NULL,
                    last_played_utc TEXT NULL,
                    rating INTEGER NOT NULL,
                    is_favorite INTEGER NOT NULL,
                    source_type INTEGER NOT NULL,
                    source_track_id TEXT NOT NULL,
                    source_connection_id TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist);
                CREATE INDEX IF NOT EXISTS ix_tracks_album ON tracks(album);
                CREATE INDEX IF NOT EXISTS ix_tracks_date_added ON tracks(date_added_utc);
                CREATE INDEX IF NOT EXISTS ix_tracks_last_modified ON tracks(last_modified_utc);
                """;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task MigrateFromJsonIfEmptyAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (await CountAsync(ct) > 0) return;
        await UpsertTracksAsync(tracks, ct);
    }

    public async Task UpsertTracksAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        const string upsertSql = """
            INSERT INTO tracks (
                id,file_path,title,artist,album,album_artist,genre,year,duration_ms,file_size,
                last_modified_utc,date_added_utc,play_count,last_played_utc,rating,is_favorite,
                source_type,source_track_id,source_connection_id
            ) VALUES (
                $id,$file_path,$title,$artist,$album,$album_artist,$genre,$year,$duration_ms,$file_size,
                $last_modified_utc,$date_added_utc,$play_count,$last_played_utc,$rating,$is_favorite,
                $source_type,$source_track_id,$source_connection_id
            )
            ON CONFLICT(id) DO UPDATE SET
                file_path=excluded.file_path,
                title=excluded.title,
                artist=excluded.artist,
                album=excluded.album,
                album_artist=excluded.album_artist,
                genre=excluded.genre,
                year=excluded.year,
                duration_ms=excluded.duration_ms,
                file_size=excluded.file_size,
                last_modified_utc=excluded.last_modified_utc,
                date_added_utc=excluded.date_added_utc,
                play_count=excluded.play_count,
                last_played_utc=excluded.last_played_utc,
                rating=excluded.rating,
                is_favorite=excluded.is_favorite,
                source_type=excluded.source_type,
                source_track_id=excluded.source_track_id,
                source_connection_id=excluded.source_connection_id;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = upsertSql;
        cmd.Transaction = tx;

        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pFilePath = cmd.Parameters.Add("$file_path", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pArtist = cmd.Parameters.Add("$artist", SqliteType.Text);
        var pAlbum = cmd.Parameters.Add("$album", SqliteType.Text);
        var pAlbumArtist = cmd.Parameters.Add("$album_artist", SqliteType.Text);
        var pGenre = cmd.Parameters.Add("$genre", SqliteType.Text);
        var pYear = cmd.Parameters.Add("$year", SqliteType.Integer);
        var pDuration = cmd.Parameters.Add("$duration_ms", SqliteType.Integer);
        var pFileSize = cmd.Parameters.Add("$file_size", SqliteType.Integer);
        var pLastModified = cmd.Parameters.Add("$last_modified_utc", SqliteType.Text);
        var pDateAdded = cmd.Parameters.Add("$date_added_utc", SqliteType.Text);
        var pPlayCount = cmd.Parameters.Add("$play_count", SqliteType.Integer);
        var pLastPlayed = cmd.Parameters.Add("$last_played_utc", SqliteType.Text);
        var pRating = cmd.Parameters.Add("$rating", SqliteType.Integer);
        var pIsFavorite = cmd.Parameters.Add("$is_favorite", SqliteType.Integer);
        var pSourceType = cmd.Parameters.Add("$source_type", SqliteType.Integer);
        var pSourceTrackId = cmd.Parameters.Add("$source_track_id", SqliteType.Text);
        var pSourceConnectionId = cmd.Parameters.Add("$source_connection_id", SqliteType.Text);

        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = track.Id.ToString("N");
            pFilePath.Value = track.FilePath;
            pTitle.Value = track.Title;
            pArtist.Value = track.Artist;
            pAlbum.Value = track.Album;
            pAlbumArtist.Value = track.AlbumArtist;
            pGenre.Value = track.Genre;
            pYear.Value = track.Year;
            pDuration.Value = (long)track.Duration.TotalMilliseconds;
            pFileSize.Value = track.FileSize;
            pLastModified.Value = track.LastModified.ToUniversalTime().ToString("O");
            pDateAdded.Value = track.DateAdded.ToUniversalTime().ToString("O");
            pPlayCount.Value = track.PlayCount;
            pLastPlayed.Value = track.LastPlayed?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value;
            pRating.Value = Math.Clamp(track.Rating, 0, 5);
            pIsFavorite.Value = track.IsFavorite ? 1 : 0;
            pSourceType.Value = (int)track.SourceType;
            pSourceTrackId.Value = track.SourceTrackId ?? string.Empty;
            pSourceConnectionId.Value = track.SourceConnectionId ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task DeleteTracksAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tracks WHERE id = $id;";
        cmd.Transaction = tx;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);

        foreach (var id in trackIds)
        {
            pId.Value = id.ToString("N");
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tracks;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks;";
        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }
}
