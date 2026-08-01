using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Manages the music library: scanning folders, building track/album/artist indexes.
/// </summary>
public interface ILibraryService
{
    /// <summary>All tracks in the library.</summary>
    IReadOnlyList<Track> Tracks { get; }

    /// <summary>All albums, aggregated from tracks.</summary>
    IReadOnlyList<Album> Albums { get; }

    /// <summary>All artists, aggregated from tracks.</summary>
    IReadOnlyList<Artist> Artists { get; }

    /// <summary>Fires when a library scan completes (full or incremental).</summary>
    event EventHandler? LibraryUpdated;

    /// <summary>Fires during scanning with progress info (current file count).</summary>
    event EventHandler<int>? ScanProgress;

    /// <summary>Fires when track favorites have been toggled (lightweight, no re-index).</summary>
    event EventHandler? FavoritesChanged;

    /// <summary>
    /// Fires when the library itself rewrote the configured music-folder list (a root that
    /// no longer exists on disk and contributes no tracks is dropped). Carries the new
    /// list, so Settings doesn't have to re-read settings.json to notice.
    /// </summary>
    event EventHandler<List<string>>? MusicFoldersChanged;

    /// <summary>
    /// Fires when a scan was abandoned because configured music folders were unavailable
    /// (offline drive / unreachable share). Carries the missing root paths; the existing
    /// library is left untouched.
    /// </summary>
    event EventHandler<string[]>? ScanAborted;

    /// <summary>
    /// Scans configured music folders for audio files.
    /// Reads metadata, extracts artwork, and builds the library index.
    /// </summary>
    Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default);

    /// <summary>
    /// Cancels any in-flight scan and flushes whatever has been scanned so far to
    /// disk — merged with the existing library, so no already-known track is dropped —
    /// so the next launch resumes the scan incrementally instead of restarting it.
    /// Returns once the checkpoint is persisted or <paramref name="timeout"/> elapses.
    /// No-op when no scan is running.
    /// </summary>
    Task PauseActiveScanForShutdownAsync(TimeSpan timeout);

    /// <summary>
    /// Imports specific audio files into the existing library without a full-folder rescan.
    /// Existing tracks are updated if the source file has changed.
    /// <paramref name="progress"/> receives the 1-based count of files processed so far.
    /// </summary>
    Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null);

    /// <summary>Looks up a track by its ID. Returns null if not found.</summary>
    Track? GetTrackById(Guid id);

    /// <summary>Looks up an album by its ID. Returns null if not found.</summary>
    Album? GetAlbumById(Guid id);

    /// <summary>Gets all albums for a specific artist name.</summary>
    IReadOnlyList<Album> GetAlbumsByArtist(string artistName);

    /// <summary>Removes a track from the library by ID (does not delete the file).</summary>
    Task RemoveTrackAsync(Guid id);

    /// <summary>Removes multiple tracks from the library in a single batch (one rebuild + save).</summary>
    Task RemoveTracksAsync(IEnumerable<Guid> ids);

    /// <summary>
    /// Updates the on-disk location of tracks that have been moved/renamed, preserving
    /// each track's user state (favorites, play count, rating). Because track IDs are
    /// derived from the file path, IDs are recomputed; the returned map (old ID → new ID)
    /// lets callers fix up references such as playlist track lists.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(
        IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default);

    /// <summary>Loads the library from persisted JSON data.</summary>
    Task LoadAsync();

    /// <summary>Saves the current library state to JSON.</summary>
    Task SaveAsync();

    /// <summary>
    /// Persists a pure user-state change (rating, favorite, play count, snooze,
    /// saved position) for the given tracks as small journal rows in library.db
    /// instead of re-serializing the entire library.json. The journal overlays the
    /// JSON on load (journal wins), and the JSON catches up on the next structural
    /// save (scan, metadata edit, shutdown flush). Falls back to a full JSON save
    /// when the journal is unavailable so a broken library.db never loses a rating.
    /// Call this — not <see cref="SaveAsync"/> — after mutating any of those fields.
    /// </summary>
    Task SaveTrackUserStateAsync(IReadOnlyCollection<Track> tracks);

    /// <summary>Clears all tracks, albums, and artists from the library and persists the empty state.</summary>
    Task ClearAsync();

    /// <summary>Rebuilds indexes and durable library index storage from current persisted state.</summary>
    Task RebuildIndexAsync(CancellationToken ct = default);

    /// <summary>Raises the FavoritesChanged event to notify subscribers.</summary>
    void NotifyFavoritesChanged();

    /// <summary>
    /// Same as <see cref="NotifyFavoritesChanged()"/> but only re-raises album state for
    /// the albums owning <paramref name="changed"/> — a full sweep is two PropertyChanged
    /// raises per album in the library for a single heart click.
    /// </summary>
    void NotifyFavoritesChanged(IReadOnlyCollection<Track>? changed);

    /// <summary>Sets a 0-5 star rating on the given tracks, saves the library, and writes the file tags.</summary>
    Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating);

    /// <summary>Sets the "not liked" flag on the given tracks, saves the library, and writes the file tags.</summary>
    Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked);

    /// <summary>Sets/clears the snooze expiry on the given tracks and saves the library.</summary>
    Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until);

    /// <summary>Rebuilds indexes and raises LibraryUpdated after a track's metadata has been edited.</summary>
    void NotifyMetadataChanged();

    /// <summary>
    /// Applies a "Merge Featured Artists From Titles" toggle flip to the already-indexed
    /// library immediately — a rescan reuses unchanged files wholesale, so it would never
    /// propagate the setting. On enable, merges in-memory; on disable, re-reads tags of
    /// merged-looking local tracks in the background to restore the original credits.
    /// A newer flip cancels an in-flight pass. Returns the number of tracks changed.
    /// </summary>
    Task<int> ApplyMergeFeaturedFromTitlesAsync(bool enabled, CancellationToken ct = default);
}
