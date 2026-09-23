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

    /// <summary>
    /// True when settings.json existed but could not be parsed on the last load attempt.
    /// The damaged file was renamed aside and the values came from settings.json.bak, or
    /// from defaults when no usable backup existed. Unlike the library, settings stay
    /// writable — refusing to save would leave the app unable to persist anything.
    /// </summary>
    bool SettingsLoadFailed { get; }

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

    /// <summary>
    /// The saved queue, with any newer <see cref="SaveQueuePositionAsync"/> checkpoint for the
    /// same track already folded into <see cref="QueueState.PositionSeconds"/>.
    /// </summary>
    Task<QueueState?> LoadQueueStateAsync();

    /// <summary>Writes the queue and supersedes any position checkpoint.</summary>
    Task SaveQueueStateAsync(QueueState state);

    /// <summary>
    /// Checkpoints the playback position alone, without rewriting the queue. For hosts that
    /// checkpoint on a timer (the phone, every five seconds) where re-serializing and fsyncing
    /// the whole queue for a moved position would cost hundreds of MB of flash writes an hour.
    /// Superseded by the next <see cref="SaveQueueStateAsync"/>.
    /// </summary>
    Task SaveQueuePositionAsync(Guid? currentTrackId, double positionSeconds);

    // --- Index Cache ---
    Task<LibraryIndexCache?> LoadIndexCacheAsync();
    Task SaveIndexCacheAsync(LibraryIndexCache cache);

    // --- Artwork ---
    /// <summary>Returns the expected file path for an album's cached artwork.</summary>
    string GetArtworkPath(Guid albumId);

    /// <summary>Saves raw image bytes as the cached artwork for an album.</summary>
    void SaveArtwork(Guid albumId, byte[] imageData);

    /// <summary>Cache path of a track's OWN cover (<see cref="TrackArtwork"/>): the
    /// "tracks" folder beside the album covers.</summary>
    string GetTrackArtworkPath(Guid trackId) =>
        Path.Combine(Path.GetDirectoryName(GetArtworkPath(Guid.Empty)) ?? string.Empty, "tracks", $"{trackId}.jpg");

    /// <summary>Saves a track's own cover (best effort, like <see cref="SaveArtwork"/>).</summary>
    void SaveTrackArtwork(Guid trackId, byte[] imageData)
    {
        try
        {
            var path = GetTrackArtworkPath(trackId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, imageData);
        }
        catch { /* Non-critical: the track just shows its album's cover */ }
    }

    /// <summary>Drops a track's own cover so it shows its album's again.</summary>
    void DeleteTrackArtwork(Guid trackId)
    {
        try { File.Delete(GetTrackArtworkPath(trackId)); }
        catch { /* Non-critical */ }
    }

    /// <summary>
    /// Returns the cache path for an animated cover.
    /// Album scope: <DataRoot>/animated_covers/<albumId>.<ext>
    /// Track scope: <DataRoot>/animated_covers/<albumId>__<trackId>.<ext>
    /// </summary>
    string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension);

    /// <summary>Ensures the animated_covers directory exists. Idempotent.</summary>
    void EnsureAnimatedCoverDir();
}
