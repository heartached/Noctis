using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// SQLite-backed index used for large-library durability and query speed.
/// </summary>
public interface ISqliteLibraryIndexService
{
    Task InitializeAsync(CancellationToken ct = default);
    Task MigrateFromJsonIfEmptyAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
    Task UpsertTracksAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
    Task DeleteTracksAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the entire index in a single transaction.
    /// Callers used to do ClearAsync() then UpsertTracksAsync(), which are separate
    /// transactions — a crash or cancellation between them left the index empty.
    /// </summary>
    Task ReplaceAllAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);

    // ── Per-track user-state journal ─────────────────────────
    // Mutable user state (rating, favorite, play count, snooze, ...) lives in its
    // own table so a rating change is one small UPSERT instead of re-serializing
    // the entire library.json. The table is deliberately untouched by the scan
    // mirror methods above (ReplaceAllAsync/DeleteTracksAsync/ClearAsync): rows
    // whose track is currently absent are retained so a removed track that
    // returns gets its play counts and ratings back.

    /// <summary>Upserts one full user-state row per track (values snapshot from the Track).</summary>
    Task UpsertUserStateAsync(IEnumerable<Track> tracks, CancellationToken ct = default);

    /// <summary>Loads every journal row, keyed by track Id.</summary>
    Task<Dictionary<Guid, TrackUserState>> LoadUserStateAsync(CancellationToken ct = default);

    /// <summary>
    /// One-time migration: when the journal is empty (first run after upgrade, or a
    /// deleted library.db), seeds it from the given tracks' current (JSON) values.
    /// No-op when any row exists.
    /// </summary>
    Task SeedUserStateIfEmptyAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
}

/// <summary>
/// Snapshot of the per-track mutable user state journaled in library.db.
/// A row always carries the complete field set (no per-field deltas), so
/// overlaying it on a JSON-loaded track is a plain assignment of every field.
/// </summary>
public sealed record TrackUserState(
    int PlayCount,
    DateTime? LastPlayed,
    int Rating,
    bool IsDisliked,
    bool IsFavorite,
    DateTime? FavoritedAt,
    DateTime? SnoozedUntil,
    long SavedPositionMs);

