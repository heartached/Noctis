using Noctis.Models;

namespace Noctis.Services.Server;

/// <summary>
/// The <see cref="IServerLibrary"/> the OpenSubsonic server talks to: reads and mutates
/// the live <see cref="ILibraryService"/> and the playlist store on behalf of remote
/// clients, and lands sync winners.
///
/// Library mutations go through <c>marshal</c>, the host's choice of thread: the
/// desktop app passes a lambda that hops onto the Avalonia dispatcher (the library's
/// collections are bound to views), the headless host passes one that serialises
/// through a lock. Requests arrive on Kestrel threads either way.
/// </summary>
public sealed class LibraryServerAdapter : IServerLibrary
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IPlayHistoryService _playHistory;
    private readonly Func<Task>? _playlistsChanged;
    private readonly Func<Func<Task>, Task> _marshal;
    private readonly Func<string?>? _avatarPath;
    private readonly Func<string?>? _profileName;
    private readonly Func<CancellationToken, Task<string?>>? _uploadRoot;
    private readonly PhoneHandoffInbox _handoff;
    private readonly Func<Track, DateTime, Task>? _forwardScrobble;
    private readonly Func<(bool LastFm, bool ListenBrainz)>? _scrobbleServices;

    public Task SavePlayQueueAsync(PhoneHandoff handoff)
    {
        _handoff.Save(handoff);
        return Task.CompletedTask;
    }

    public PhoneHandoff? PlayQueue => _handoff.Latest;

    /// <param name="marshal">
    /// Runs a library mutation on the thread that owns the library. Null serialises
    /// mutations through a lock, which is right for a host with no UI thread.
    /// </param>
    /// <param name="avatarPath">
    /// The current profile picture's path (the desktop's AppSettings.ProfileAvatarPath), read
    /// per request so a new picture is served at once. Null (the headless host): no avatar.
    /// </param>
    /// <param name="uploadRoot">
    /// The folder songs sent from a phone go under (the desktop's managed "Noctis Imports",
    /// created and added to the music folders on first use), run through <c>marshal</c> because it
    /// may touch the settings. Null (the headless host): phones cannot send songs.
    /// </param>
    /// <param name="handoff">
    /// Where a phone's "Pick Up on Computer" queue is kept; the desktop window reads the same
    /// instance. Null: one under the data directory (no window to offer it, but getPlayQueue works).
    /// </param>
    /// <param name="forwardScrobble">
    /// Sends a phone's play on to the host's Last.fm / ListenBrainz (the track, when it started),
    /// applying the host's own scrobbling settings and exclusions. Called on the library's thread
    /// and not awaited by the phone's request. Null (the headless host): phone plays are only counted.
    /// </param>
    /// <param name="scrobbleServices">Whether the host's Last.fm and ListenBrainz are on, for the phone.</param>
    public LibraryServerAdapter(ILibraryService library, IPersistenceService persistence, IPlayHistoryService playHistory,
        Func<Task>? playlistsChanged = null, Func<Func<Task>, Task>? marshal = null, Func<string?>? avatarPath = null,
        Func<string?>? profileName = null, Func<CancellationToken, Task<string?>>? uploadRoot = null,
        PhoneHandoffInbox? handoff = null, Func<Track, DateTime, Task>? forwardScrobble = null,
        Func<(bool LastFm, bool ListenBrainz)>? scrobbleServices = null)
    {
        _forwardScrobble = forwardScrobble;
        _scrobbleServices = scrobbleServices;
        _handoff = handoff ?? new PhoneHandoffInbox(persistence.DataDirectory);
        _library = library;
        _persistence = persistence;
        _playHistory = playHistory;
        _playlistsChanged = playlistsChanged;
        _marshal = marshal ?? SerialisingMarshaller();
        _avatarPath = avatarPath;
        _profileName = profileName;
        _uploadRoot = uploadRoot;
    }

    /// <summary>One mutation at a time, on whatever thread called; for hosts without a UI thread.</summary>
    public static Func<Func<Task>, Task> SerialisingMarshaller()
    {
        var gate = new SemaphoreSlim(1, 1);
        return async work =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { await work().ConfigureAwait(false); }
            finally { gate.Release(); }
        };
    }

    private Task Run(Func<Task> work) => _marshal(work);

    private async Task<T> Run<T>(Func<T> read)
    {
        T result = default!;
        await _marshal(() => { result = read(); return Task.CompletedTask; }).ConfigureAwait(false);
        return result;
    }

    public async Task<LibrarySnapshot> SnapshotAsync()
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        return await Run(() => new LibrarySnapshot(
            _library.Tracks.ToList(),
            _library.Albums.ToList(),
            _library.Artists.ToList(),
            playlists.ToList())).ConfigureAwait(false);
    }

    public bool IsScanning => _library.IsScanning;

    public string? ArtworkPath(Guid albumId)
    {
        var path = _persistence.GetArtworkPath(albumId);
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    public string? ProfileName => _profileName?.Invoke();

    public async Task<string?> UploadRootAsync(CancellationToken ct)
    {
        if (_uploadRoot is null) return null;
        string? root = null;
        await Run(async () => root = await _uploadRoot(ct)).ConfigureAwait(false);
        return root;
    }

    /// <summary>The import path a drop takes (ImportFilesAsync), on the library's thread like
    /// every other write: the song lands in the library and library.json.</summary>
    public Task ImportUploadedAsync(string path, CancellationToken ct)
        => Run(() => _library.ImportFilesAsync(new[] { path }, ct));

    public string? AvatarPath(string userName)
    {
        var path = _avatarPath?.Invoke();
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred)
        => Run(async () =>
        {
            var ids = new HashSet<Guid>(trackIds);
            var albumSet = new HashSet<Guid>(albumIds);
            var artistNames = new HashSet<string>(
                _library.Artists.Where(a => artistIds.Contains(a.Id)).Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            var changed = new List<Track>();
            foreach (var t in _library.Tracks)
            {
                if (!(ids.Contains(t.Id) || albumSet.Contains(t.AlbumId) || artistNames.Contains(t.Artist) || artistNames.Contains(t.AlbumArtist)))
                    continue;
                if (t.IsFavorite == starred) continue;
                t.IsFavorite = starred;
                changed.Add(t);
            }
            if (changed.Count == 0) return;
            await _library.SaveTrackUserStateAsync(changed);
            _library.NotifyFavoritesChanged(changed);
        });

    public Task ScrobbleAsync(Guid trackId) => ScrobbleAsync(trackId, DateTime.UtcNow);

    public Task ScrobbleAsync(Guid trackId, DateTime playedUtc) => ScrobbleAsync(trackId, playedUtc, forward: false);

    public (bool LastFm, bool ListenBrainz)? ScrobbleServices => _scrobbleServices?.Invoke();

    public Task ScrobbleAsync(Guid trackId, DateTime playedUtc, bool forward)
        => Run(async () =>
        {
            // Same bookkeeping as PlayerViewModel when a track starts on the desktop.
            var track = _library.GetTrackById(trackId);
            if (track is null) return;
            if (track.PlayCount < int.MaxValue) track.PlayCount++;
            // An offline play reported late must not move "last played" backwards.
            if (track.LastPlayed is null || playedUtc > track.LastPlayed) track.LastPlayed = playedUtc;
            // At the phone's time, in its place: an offline play lands on the day it was played.
            _playHistory.RecordPlay(track, playedUtc);
            if (forward && _forwardScrobble is { } send) _ = Forward(send, track, playedUtc);
            await _library.SaveTrackUserStateAsync(new[] { track });
        });

    /// <summary>The phone's sync does not wait on Last.fm; a failure is logged, never thrown at it.</summary>
    private static async Task Forward(Func<Track, DateTime, Task> send, Track track, DateTime playedUtc)
    {
        try { await send(track, playedUtc).ConfigureAwait(false); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "Server", $"forwarding a phone play failed: {ex.Message}"); }
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var playlist = new Playlist { Name = string.IsNullOrWhiteSpace(name) ? "New Playlist" : name.Trim(), TrackIds = trackIds.ToList() };
        playlists.Add(playlist);
        await _persistence.SavePlaylistsAsync(playlists).ConfigureAwait(false);
        await NotifyPlaylists().ConfigureAwait(false);
        return playlist;
    }

    public async Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var playlist = playlists.FirstOrDefault(p => p.Id == id);
        if (playlist is null) return false;
        if (!string.IsNullOrWhiteSpace(name)) playlist.Name = name.Trim();
        foreach (var index in removeIndexes.Distinct().OrderByDescending(i => i))
            if (index >= 0 && index < playlist.TrackIds.Count) playlist.TrackIds.RemoveAt(index);
        var now = DateTime.UtcNow;
        playlist.TrackIds.AddRange(add);
        foreach (var trackId in add) playlist.TrackAddedAt[trackId] = now;
        foreach (var gone in playlist.TrackAddedAt.Keys.Where(k => !playlist.TrackIds.Contains(k)).ToList())
            playlist.TrackAddedAt.Remove(gone);
        playlist.ModifiedAt = now;
        await _persistence.SavePlaylistsAsync(playlists).ConfigureAwait(false);
        await NotifyPlaylists().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeletePlaylistAsync(Guid id)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var removed = playlists.RemoveAll(p => p.Id == id) > 0;
        if (!removed) return false;
        await _persistence.SavePlaylistsAsync(playlists).ConfigureAwait(false);
        await NotifyPlaylists().ConfigureAwait(false);
        return true;
    }

    private Task NotifyPlaylists() => _playlistsChanged is null ? Task.CompletedTask : Run(_playlistsChanged);

    // ── Sync (Account & Sync): remote state that won last-writer-wins lands here ──

    public Task ApplyTrackStateAsync(Guid trackId, Sync.TrackSyncState state)
        => Run(async () =>
        {
            var track = _library.GetTrackById(trackId);
            if (track is null) return;
            var favoriteChanged = track.IsFavorite != state.Favorite;
            track.IsFavorite = state.Favorite;
            if (state.FavoritedAt.HasValue) track.FavoritedAt = state.FavoritedAt;
            // Play counts only ever grow: a device that missed a few plays must not roll them back.
            if (state.PlayCount > track.PlayCount) track.PlayCount = state.PlayCount;
            if (state.LastPlayed.HasValue && (track.LastPlayed is null || state.LastPlayed > track.LastPlayed)) track.LastPlayed = state.LastPlayed;
            // Snooze is app state only (SetTracksSnoozedAsync writes no tag), so it is set here,
            // before the save, which then records the same payload the ledger already holds.
            // Absent: the sender said nothing about it (an older phone). A past date: not snoozed.
            if (state.SnoozedUntil is { } snooze)
                track.SnoozedUntil = snooze > DateTime.UtcNow ? snooze : null;
            await _library.SaveTrackUserStateAsync(new[] { track });
            if (favoriteChanged) _library.NotifyFavoritesChanged(new[] { track });
            // Rating/dislike go through the same calls the desktop's star clicks use, so the
            // file tags get written (deferred) — both are no-ops when nothing changed.
            await _library.SetTracksRatingAsync(new[] { track }, Math.Clamp(state.Rating, 0, 5));
            await _library.SetTracksDislikedAsync(new[] { track }, state.Disliked);
        });

    public string PlaylistCoverStagingDirectory => Sync.PlaylistCovers.StagingDirectory(_persistence.DataDirectory);

    public async Task ApplyPlaylistStateAsync(Guid playlistId, Sync.PlaylistSyncState state)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var existing = playlists.FirstOrDefault(p => p.Id == playlistId);
        if (state.Deleted)
        {
            if (existing is null) return;
            playlists.Remove(existing);
        }
        else
        {
            if (existing is null)
            {
                existing = new Playlist { Id = playlistId, CreatedAt = state.ModifiedAt };
                playlists.Add(existing);
            }
            existing.Name = string.IsNullOrWhiteSpace(state.Name) ? existing.Name : state.Name;
            existing.Description = state.Description ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(state.Color)) existing.Color = state.Color;
            existing.TrackIds = state.TrackIds?.ToList() ?? new List<Guid>();
            existing.ModifiedAt = state.ModifiedAt;
            Sync.PlaylistCovers.Apply(existing, state.Cover,
                Sync.PlaylistCovers.CoversDirectory(_persistence.DataDirectory), PlaylistCoverStagingDirectory);
        }
        await _persistence.SavePlaylistsAsync(playlists).ConfigureAwait(false);
        await NotifyPlaylists().ConfigureAwait(false);
    }
}
