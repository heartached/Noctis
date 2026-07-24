using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Handles all file I/O for persisting application state:
/// settings, library data, playlists, queue state, and artwork cache.
/// All files are stored under %APPDATA%\Noctis\.
/// </summary>
public interface IPersistenceService
{
    /// <summary>Base directory for all persisted data.</summary>
    string DataDirectory { get; }

    // --- Settings ---
    Task<AppSettings> LoadSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);

    // --- Library ---
    Task<List<Track>?> LoadLibraryAsync();
    Task SaveLibraryAsync(List<Track> tracks);

    /// <summary>
    /// True when library.json existed but could not be parsed on the last load attempt.
    /// The damaged file has been renamed aside (see <see cref="LastCorruptFilePath"/>) and
    /// <see cref="SaveLibraryAsync"/> refuses to write for the rest of the session, so an
    /// empty in-memory library can never overwrite recoverable user data.
    /// </summary>
    bool LibraryLoadFailed { get; }

    /// <summary>Path the damaged file was renamed to, or null when nothing was quarantined.</summary>
    string? LastCorruptFilePath { get; }

    // --- Playlists ---
    Task<List<Playlist>> LoadPlaylistsAsync();
    Task SavePlaylistsAsync(List<Playlist> playlists);

    // --- Queue ---
    Task<QueueState?> LoadQueueStateAsync();
    Task SaveQueueStateAsync(QueueState state);

    // --- Index Cache ---
    Task<LibraryIndexCache?> LoadIndexCacheAsync();
    Task SaveIndexCacheAsync(LibraryIndexCache cache);

    // --- Artwork ---
    /// <summary>Returns the expected file path for an album's cached artwork.</summary>
    string GetArtworkPath(Guid albumId);

    /// <summary>Saves raw image bytes as the cached artwork for an album.</summary>
    void SaveArtwork(Guid albumId, byte[] imageData);

    /// <summary>
    /// Returns the cache path for an animated cover.
    /// Album scope: <DataRoot>/animated_covers/<albumId>.<ext>
    /// Track scope: <DataRoot>/animated_covers/<albumId>__<trackId>.<ext>
    /// </summary>
    string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension);

    /// <summary>Ensures the animated_covers directory exists. Idempotent.</summary>
    void EnsureAnimatedCoverDir();
}
