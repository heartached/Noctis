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
public interface IServerLibrary : Sync.ISyncApplier
{
    Task<LibrarySnapshot> SnapshotAsync();

    /// <summary>A library scan is running: snapshots may list only part of the library
    /// (getScanStatus reports it, so a phone does not drop the songs not listed yet).</summary>
    bool IsScanning => false;

    /// <summary>Path of the album's cover file, or null.</summary>
    string? ArtworkPath(Guid albumId);

    /// <summary>Path of <paramref name="userName"/>'s profile picture, or null (getAvatar then answers
    /// 70). The desktop keeps one profile, so every account on it gets the owner's picture.</summary>
    string? AvatarPath(string userName) => null;

    /// <summary>The desktop's profile name ("Username" in Settings), or null. A phone may sign in
    /// with it instead of the account's email: the desktop keeps one profile, so it names the
    /// main account.</summary>
    string? ProfileName => null;

    /// <summary>The folder songs sent from a phone (noctisUploadTrack) go under, created if
    /// needed: the desktop's "Noctis Imports", the same place a dropped file is moved to. Null =
    /// this host takes no songs (the call then answers error 0).</summary>
    Task<string?> UploadRootAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Adds a file noctisUploadTrack just wrote under <see cref="UploadRootAsync"/> to the
    /// library, the way a dropped file is added (then it lists, streams and syncs like any other).</summary>
    Task ImportUploadedAsync(string path, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Where covers a phone sends (noctisUploadPlaylistCover) wait for the playlist push that
    /// names them (<see cref="Sync.PlaylistCovers.Apply"/>). Null = this host takes no covers.</summary>
    string? PlaylistCoverStagingDirectory => null;

    /// <summary>Stars/unstars tracks (albums and artists star every track they contain). Persists user state.</summary>
    Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred);

    /// <summary>Keeps the queue a phone sent (savePlayQueue) for the desktop to offer ("Pick Up on
    /// Computer"). The default drops it: a host without a window has nobody to offer it to.</summary>
    Task SavePlayQueueAsync(PhoneHandoff handoff) => Task.CompletedTask;

    /// <summary>The queue last saved by <see cref="SavePlayQueueAsync"/> (getPlayQueue), or null.</summary>
    PhoneHandoff? PlayQueue => null;

    /// <summary>Counts a play (submission=true scrobble).</summary>
    Task ScrobbleAsync(Guid trackId);

    /// <summary>Counts a play that happened at <paramref name="playedUtc"/> (already clamped to the past; a phone's offline play).</summary>
    Task ScrobbleAsync(Guid trackId, DateTime playedUtc) => ScrobbleAsync(trackId);

    /// <summary>Counts the play as above and, when <paramref name="forward"/>, sends it on to the
    /// host's own Last.fm / ListenBrainz (a phone in Through Computer mode whose play passed the
    /// scrobble rule; the host's scrobbling settings and exclusions still decide). The default
    /// only counts it: a host without scrobbling has nowhere to send it.</summary>
    Task ScrobbleAsync(Guid trackId, DateTime playedUtc, bool forward) => ScrobbleAsync(trackId, playedUtc);

    /// <summary>Whether the host sends plays to Last.fm and ListenBrainz (signed in and turned on),
    /// for the phone's Scrobbling page (getNoctisSyncStatus). Null: the host does not forward phone
    /// plays at all.</summary>
    (bool LastFm, bool ListenBrainz)? ScrobbleServices => null;

    Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds);

    /// <summary>Rename and/or add/remove tracks (indexes refer to the playlist before removal). False when the playlist is unknown.</summary>
    Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes);

    Task<bool> DeletePlaylistAsync(Guid id);
}
