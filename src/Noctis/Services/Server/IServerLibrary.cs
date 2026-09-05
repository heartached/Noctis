using Noctis.Models;

namespace Noctis.Services.Server;

/// <summary>Immutable read of the library for one request; built on the UI thread, consumed on Kestrel's.</summary>
public sealed record LibrarySnapshot(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Album> Albums,
    IReadOnlyList<Artist> Artists,
    IReadOnlyList<Playlist> Playlists);

/// <summary>
/// Everything the Subsonic endpoints need from the app, and nothing else — so the server
/// can be tested against a fake and never reaches into view models directly. The real
/// implementation (<see cref="LibraryServerAdapter"/>) marshals to the UI thread because
/// the library collections are owned there.
/// </summary>
public interface IServerLibrary
{
    Task<LibrarySnapshot> SnapshotAsync();

    /// <summary>Path of the album's cover file, or null.</summary>
    string? ArtworkPath(Guid albumId);

    /// <summary>Stars/unstars tracks (albums and artists star every track they contain). Persists user state.</summary>
    Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred);

    /// <summary>Counts a play (submission=true scrobble).</summary>
    Task ScrobbleAsync(Guid trackId);

    Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds);

    /// <summary>Rename and/or add/remove tracks (indexes refer to the playlist before removal). False when the playlist is unknown.</summary>
    Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes);

    Task<bool> DeletePlaylistAsync(Guid id);
}
