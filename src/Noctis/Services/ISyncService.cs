using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Cross-device sync contract for playlists, ratings, and playback state.
/// </summary>
public interface ISyncService
{
    Task<IReadOnlyList<SyncRevision>> PullAsync(CancellationToken ct = default);
    Task PushPlayStateAsync(Track track, CancellationToken ct = default);
    Task PushPlaylistAsync(Playlist playlist, CancellationToken ct = default);
}

