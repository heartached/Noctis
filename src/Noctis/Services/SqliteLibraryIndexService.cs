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
                CREATE TABLE IF NOT EXISTS track_user_state (
                    id TEXT PRIMARY KEY,
                    play_count INTEGER NOT NULL,
                    last_played_utc TEXT NULL,
                    rating INTEGER NOT NULL,
                    is_disliked INTEGER NOT NULL,
                    is_favorite INTEGER NOT NULL,
                    favorited_at_utc TEXT NULL,
                    snoozed_until_utc TEXT NULL,
                    saved_position_ms INTEGER NOT NULL
                );
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

    public Task UpsertTracksAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
        => WriteTracksAsync(tracks, clearFirst: false, ct);

    /// <inheritdoc />
    public Task ReplaceAllAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
        => WriteTracksAsync(tracks, clearFirst: true, ct);

    private async Task WriteTracksAsync(IEnumerable<Track> tracks, bool clearFirst, CancellationToken ct)
    {
        await InitializeAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Inside the same transaction as the inserts, so an interrupted rebuild rolls
        // back to the previous contents instead of leaving an empty table.
        if (clearFirst)
        {
            await using var clearCmd = conn.CreateCommand();
            clearCmd.CommandText = "DELETE FROM tracks;";
            clearCmd.Transaction = tx;
            await clearCmd.ExecuteNonQueryAsync(ct);
        }

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
            // Coalesce every NOT NULL text column (matching the source-id fields
            // below): one null — e.g. from a hand-edited/corrupt library.json —
            // otherwise throws and rolls back the whole batch transaction.
            pFilePath.Value = track.FilePath ?? string.Empty;
            pTitle.Value = track.Title ?? string.Empty;
            pArtist.Value = track.Artist ?? string.Empty;
            pAlbum.Value = track.Album ?? string.Empty;
            pAlbumArtist.Value = track.AlbumArtist ?? string.Empty;
            pGenre.Value = track.Genre ?? string.Empty;
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

    // ── Per-track user-state journal ─────────────────────────
    // Kept strictly separate from the `tracks` mirror above: scans delete+reinsert
    // that table wholesale, and the journal must survive them. Nothing in the
    // journal methods touches `tracks` and nothing in the mirror methods touches
    // `track_user_state`.

    public async Task UpsertUserStateAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        const string upsertSql = """
            INSERT INTO track_user_state (
                id,play_count,last_played_utc,rating,is_disliked,is_favorite,
                favorited_at_utc,snoozed_until_utc,saved_position_ms
            ) VALUES (
                $id,$play_count,$last_played_utc,$rating,$is_disliked,$is_favorite,
                $favorited_at_utc,$snoozed_until_utc,$saved_position_ms
            )
            ON CONFLICT(id) DO UPDATE SET
                play_count=excluded.play_count,
                last_played_utc=excluded.last_played_utc,
                rating=excluded.rating,
                is_disliked=excluded.is_disliked,
                is_favorite=excluded.is_favorite,
                favorited_at_utc=excluded.favorited_at_utc,
                snoozed_until_utc=excluded.snoozed_until_utc,
                saved_position_ms=excluded.saved_position_ms;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = upsertSql;
        cmd.Transaction = tx;

        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pPlayCount = cmd.Parameters.Add("$play_count", SqliteType.Integer);
        var pLastPlayed = cmd.Parameters.Add("$last_played_utc", SqliteType.Text);
        var pRating = cmd.Parameters.Add("$rating", SqliteType.Integer);
        var pIsDisliked = cmd.Parameters.Add("$is_disliked", SqliteType.Integer);
        var pIsFavorite = cmd.Parameters.Add("$is_favorite", SqliteType.Integer);
        var pFavoritedAt = cmd.Parameters.Add("$favorited_at_utc", SqliteType.Text);
        var pSnoozedUntil = cmd.Parameters.Add("$snoozed_until_utc", SqliteType.Text);
        var pSavedPosition = cmd.Parameters.Add("$saved_position_ms", SqliteType.Integer);

        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = track.Id.ToString("N");
            pPlayCount.Value = track.PlayCount;
            pLastPlayed.Value = track.LastPlayed?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value;
            pRating.Value = Math.Clamp(track.Rating, 0, 5);
            pIsDisliked.Value = track.IsDisliked ? 1 : 0;
            pIsFavorite.Value = track.IsFavorite ? 1 : 0;
            pFavoritedAt.Value = track.FavoritedAt?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value;
            pSnoozedUntil.Value = track.SnoozedUntil?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value;
            pSavedPosition.Value = track.SavedPositionMs;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<Dictionary<Guid, TrackUserState>> LoadUserStateAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,play_count,last_played_utc,rating,is_disliked,is_favorite,
                   favorited_at_utc,snoozed_until_utc,saved_position_ms
            FROM track_user_state;
            """;

        var result = new Dictionary<Guid, TrackUserState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Skip unparseable rows instead of failing the whole load — one bad row
            // must not cost the user every other rating.
            if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
            result[id] = new TrackUserState(
                PlayCount: reader.GetInt32(1),
                LastPlayed: ReadUtc(reader, 2),
                Rating: reader.GetInt32(3),
                IsDisliked: reader.GetInt32(4) != 0,
                IsFavorite: reader.GetInt32(5) != 0,
                FavoritedAt: ReadUtc(reader, 6),
                SnoozedUntil: ReadUtc(reader, 7),
                SavedPositionMs: reader.GetInt64(8));
        }
        return result;

        static DateTime? ReadUtc(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            return DateTime.TryParse(reader.GetString(ordinal), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var value)
                ? value
                : null;
        }
    }

    public async Task SeedUserStateIfEmptyAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM track_user_state;";
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0) return;
        }

        await UpsertUserStateAsync(tracks, ct);
    }
}
