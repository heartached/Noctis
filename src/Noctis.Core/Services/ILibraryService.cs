using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Manages the music library: scanning folders, building track/album/artist indexes.
/// </summary>
public interface ILibraryService
{
    /// <summary>All tracks in the library, minus those under a hidden folder
    /// (<see cref="HiddenFolders"/>). Albums, artists and <see cref="GetTrackById"/> follow it.</summary>
    IReadOnlyList<Track> Tracks { get; }

    /// <summary>Every track, hidden folders included — for the Folders view (which must keep
    /// showing a hidden folder so it can be shown again) and file bookkeeping.</summary>
    IReadOnlyList<Track> AllTracks => Tracks;

    /// <summary>Folders hidden from the library (AppSettings.HiddenLibraryFolders).</summary>
    IReadOnlyList<string> HiddenFolders => Array.Empty<string>();

    /// <summary>
    /// Hides (or shows again) every track under <paramref name="folderPath"/>, at any depth.
    /// Nothing is deleted or rescanned: the setting is saved, the indexes rebuild from the
    /// tracks already in memory and <see cref="LibraryUpdated"/> fires.
    /// </summary>
    Task SetFolderHiddenAsync(string folderPath, bool hidden) => Task.CompletedTask;

    /// <summary>All albums, aggregated from tracks.</summary>
    IReadOnlyList<Album> Albums { get; }

    /// <summary>All artists, aggregated from tracks.</summary>
    IReadOnlyList<Artist> Artists { get; }

    /// <summary>Fires when a library scan completes (full or incremental).</summary>
    event EventHandler? LibraryUpdated;

    /// <summary>Fires during scanning with progress info (current file count).</summary>
    event EventHandler<int>? ScanProgress;

    /// <summary>
    /// True while a scan/import is surfacing partial snapshots (the progressive fill):
    /// <see cref="LibraryUpdated"/> then carries only the tracks found so far, so a
    /// missing id is "not scanned yet", not "deleted". Consumers that treat a missing
    /// track as removed (the player's queue purge) must wait for the authoritative
    /// publish, which arrives with this false (GitHub #72).
    /// </summary>
    bool IsPublishingPartial => false;

    /// <summary>
    /// True for the whole of a folder scan, first publish to last: <see cref="Tracks"/> may be a
    /// partial list (<see cref="IsPublishingPartial"/>) or one the scan is about to replace.
    /// The Noctis Server reports it (getScanStatus) so a phone never takes a song missing from
    /// a mid-scan catalog for a deleted one.
    /// </summary>
    bool IsScanning => IsPublishingPartial;

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
    /// Phone app: replaces the whole <see cref="SourceType.NoctisServer"/> set (the signed-in
    /// desktop's songs) with <paramref name="tracks"/>; local tracks are untouched. A song
    /// already present keeps its instance when its catalog metadata is unchanged, or keeps its
    /// phone-side user state when it changed (rescan rule); a new song takes the state it
    /// arrives with, which is also written to the user-state journal. Rebuilds, saves and
    /// raises <see cref="LibraryUpdated"/> — unless nothing changed, when it does none of that.
    /// Waits for a running scan to finish.
    /// </summary>
    Task ReplaceRemoteTracksAsync(IReadOnlyCollection<Track> tracks, CancellationToken ct = default)
        => throw new NotSupportedException();

    /// <summary>Phone app sign-out: drops every <see cref="SourceType.NoctisServer"/> track
    /// (and covers no remaining track uses), rebuilds, saves, raises <see cref="LibraryUpdated"/>.</summary>
    Task RemoveRemoteTracksAsync() => throw new NotSupportedException();

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
    /// <summary>GitHub #74: set (null/blank clears) the user badge on the tracks.</summary>
    Task SetTracksBadgeAsync(IReadOnlyList<Track> tracks, string? badge);
    /// <summary>Badge names in use across the library, sorted, de-duplicated case-insensitively.</summary>
    IReadOnlyList<string> GetBadgeNames();

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

    /// <summary>
    /// GitHub #117: multi-value artist tags are stored joined with ArtistCredit.JoinText, which
    /// follows the separators. When the join recorded in AppSettings.ArtistCreditJoin differs
    /// from the active one, re-reads the artist tags of local tracks whose credit carries a
    /// stale join (a rescan reuses unchanged files, so it never would), then records the new
    /// join. User state is untouched: only Artist / AlbumArtist (and AlbumId) change.
    /// Passes run one at a time. Returns the number of tracks changed.
    /// </summary>
    Task<int> ApplyArtistCreditJoinAsync(CancellationToken ct = default);

    /// <summary>
    /// Extracts and caches covers for indexed albums that have none (embedded tag art
    /// when enabled, else a cover image beside the tracks), then republishes and
    /// persists the indexes if anything was healed. Albums that already have cached
    /// art cost one existence probe and no file reads. Single-flight: a call while a
    /// pass is running returns 0. Returns the number of albums that gained a cover.
    /// </summary>
    Task<int> BackfillMissingArtworkAsync(CancellationToken ct = default);
}
