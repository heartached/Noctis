using Avalonia.Threading;
using Noctis.Models;

namespace Noctis.Services.Server;

/// <summary>
/// <see cref="IServerLibrary"/> over the live app services. Reads copy the collections on the
/// UI thread (they are mutated there by scans and edits); writes go through the same paths
/// the desktop UI uses so favourites, play counts and playlists stay in one place.
/// </summary>
public sealed class LibraryServerAdapter : IServerLibrary
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IPlayHistoryService _playHistory;
    private readonly Func<Task>? _playlistsChanged;

    /// <param name="playlistsChanged">Invoked on the UI thread after a playlist write so the sidebar reloads.</param>
    public LibraryServerAdapter(ILibraryService library, IPersistenceService persistence, IPlayHistoryService playHistory, Func<Task>? playlistsChanged = null)
    {
        _library = library;
        _persistence = persistence;
        _playHistory = playHistory;
        _playlistsChanged = playlistsChanged;
    }

    private static Task<T> Ui<T>(Func<T> f) => Dispatcher.UIThread.CheckAccess() ? Task.FromResult(f()) : Dispatcher.UIThread.InvokeAsync(f).GetTask();
    private static Task Ui(Func<Task> f) => Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f);

    public async Task<LibrarySnapshot> SnapshotAsync()
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        return await Ui(() => new LibrarySnapshot(
            _library.Tracks.ToList(),
            _library.Albums.ToList(),
            _library.Artists.ToList(),
            playlists.ToList())).ConfigureAwait(false);
    }

    public string? ArtworkPath(Guid albumId)
    {
        var path = _persistence.GetArtworkPath(albumId);
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred)
        => Ui(async () =>
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

    public Task ScrobbleAsync(Guid trackId)
        => Ui(async () =>
        {
            // Same bookkeeping as PlayerViewModel when a track starts on the desktop.
            var track = _library.GetTrackById(trackId);
            if (track is null) return;
            track.PlayCount++;
            track.LastPlayed = DateTime.UtcNow;
            _playHistory.RecordPlay(track);
            await _library.SaveTrackUserStateAsync(new[] { track });
        });

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
        playlist.TrackIds.AddRange(add);
        playlist.ModifiedAt = DateTime.UtcNow;
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

    private Task NotifyPlaylists() => _playlistsChanged is null ? Task.CompletedTask : Ui(_playlistsChanged);

    // ── Sync (Account & Sync): remote state that won last-writer-wins lands here ──

    public Task ApplyTrackStateAsync(Guid trackId, Sync.TrackSyncState state)
        => Ui(async () =>
        {
            var track = _library.GetTrackById(trackId);
            if (track is null) return;
            var favoriteChanged = track.IsFavorite != state.Favorite;
            track.IsFavorite = state.Favorite;
            if (state.FavoritedAt.HasValue) track.FavoritedAt = state.FavoritedAt;
            // Play counts only ever grow: a device that missed a few plays must not roll them back.
            if (state.PlayCount > track.PlayCount) track.PlayCount = state.PlayCount;
            if (state.LastPlayed.HasValue && (track.LastPlayed is null || state.LastPlayed > track.LastPlayed)) track.LastPlayed = state.LastPlayed;
            await _library.SaveTrackUserStateAsync(new[] { track });
            if (favoriteChanged) _library.NotifyFavoritesChanged(new[] { track });
            // Rating/dislike go through the same calls the desktop's star clicks use, so the
            // file tags get written (deferred) — both are no-ops when nothing changed.
            await _library.SetTracksRatingAsync(new[] { track }, Math.Clamp(state.Rating, 0, 5));
            await _library.SetTracksDislikedAsync(new[] { track }, state.Disliked);
        });

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
        }
        await _persistence.SavePlaylistsAsync(playlists).ConfigureAwait(false);
        await NotifyPlaylists().ConfigureAwait(false);
    }
}
