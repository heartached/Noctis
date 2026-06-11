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
    /// Scans configured music folders for audio files.
    /// Reads metadata, extracts artwork, and builds the library index.
    /// </summary>
    Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default);

    /// <summary>
    /// Imports specific audio files into the existing library without a full-folder rescan.
    /// Existing tracks are updated if the source file has changed.
    /// </summary>
    Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default);

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

    /// <summary>Clears all tracks, albums, and artists from the library and persists the empty state.</summary>
    Task ClearAsync();

    /// <summary>Rebuilds indexes and durable library index storage from current persisted state.</summary>
    Task RebuildIndexAsync(CancellationToken ct = default);

    /// <summary>Raises the FavoritesChanged event to notify subscribers.</summary>
    void NotifyFavoritesChanged();

    /// <summary>Sets a 0-5 star rating on the given tracks, saves the library, and writes the file tags.</summary>
    Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating);

    /// <summary>Sets the "not liked" flag on the given tracks, saves the library, and writes the file tags.</summary>
    Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked);

    /// <summary>Sets/clears the snooze expiry on the given tracks and saves the library.</summary>
    Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until);

    /// <summary>Rebuilds indexes and raises LibraryUpdated after a track's metadata has been edited.</summary>
    void NotifyMetadataChanged();
}
