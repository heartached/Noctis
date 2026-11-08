using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Sync;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// Custom covers of the computer's playlists, kept in step both ways (see <see cref="PlaylistCovers"/>):
/// the playlist state names the cover by a hash; a cover the phone lacks is fetched
/// (getNoctisPlaylistCover), and one picked on the phone is sent (noctisUploadPlaylistCover) just
/// before the playlist push that names it. Best effort: a cover that cannot be fetched or sent is
/// logged and never fails the sync (a fetch is tried again on the next one). Playlists made on the
/// phone stay on the phone, covers included.
/// </summary>
public sealed partial class NoctisAccountService
{
    /// <summary>A cover file nothing points at is removed once it is this old (a cover being set
    /// while a sync runs is written before playlists.json names it).</summary>
    private static readonly TimeSpan UnusedCoverAge = TimeSpan.FromMinutes(10);

    private string PlaylistCoversDirectory => PlaylistCovers.CoversDirectory(_persistence.DataDirectory);

    /// <summary>The phone removed the custom cover of a playlist: one of the computer's loses it
    /// there too on the next sync (without this, "no cover" on the phone only means "not fetched yet").</summary>
    public void RecordPlaylistCoverRemoved(Guid playlistId)
    {
        var hex = playlistId.ToString("N");
        lock (_gate)
        {
            if (_account is null || !_sync.Playlists.ContainsKey(hex)) return;
            _pending.RemovedCovers[hex] = UtcNow;
        }
        SchedulePendingSave();
    }

    /// <summary>Fetches the agreed cover of every computer playlist whose cover on the phone differs,
    /// unless the phone edited the playlist since (its own cover is pushed instead). True when a
    /// playlist in <paramref name="phone"/> changed.</summary>
    private async Task<bool> SyncPlaylistCoversAsync(NoctisServerClient client, List<Playlist> phone,
        Dictionary<string, PlaylistSyncState> agreed, CancellationToken ct)
    {
        var changed = false;
        var marks = false;
        var dir = PlaylistCoversDirectory;
        foreach (var (hex, state) in agreed)
        {
            if (state.Deleted || state.Cover is not { Length: > 0 } want || !NoctisRemoteIds.TryParseSyncId(hex, out var id)) continue;
            var local = phone.FirstOrDefault(p => p.Id == id);
            if (local is null || Newer(local.ModifiedAt, state.ModifiedAt) || PlaylistCovers.CoverOf(local.CoverArtPath) == want) continue;
            var download = Path.Combine(dir, ".download-" + hex);
            try
            {
                if (!await client.GetPlaylistCoverAsync(id, download, ct).ConfigureAwait(false))
                {
                    DebugLog.Write("Account", "playlist cover: the computer has none to send");
                    continue;
                }
                var path = PlaylistCovers.Store(download, id, dir, move: true);
                if (path is null)
                {
                    DebugLog.Write("Account", "playlist cover: not a picture, or too big");
                    continue;
                }
                local.CoverArtPath = path;
                changed = true;
                lock (_gate) marks |= _pending.RemovedCovers.Remove(hex);
            }
            catch (Exception ex) when (ex is NoctisServerException or IOException or UnauthorizedAccessException)
            {
                DebugLog.Write("Account", $"playlist cover not fetched: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
            }
            finally
            {
                NoctisServerClient.TryDelete(download);
                NoctisServerClient.TryDelete(download + ".part");
            }
        }
        if (marks) SchedulePendingSave();
        return changed;
    }

    /// <summary>
    /// The cover a push of a phone-edited computer playlist says: the phone's picture (sent first
    /// unless the computer already has it), "" when the user removed it, or null (nothing said: the
    /// computer keeps its own) when the phone has none only because it was never fetched, the file
    /// cannot be read, or the computer refused the picture (an older one does not know the call).
    /// Only an unreachable computer throws, failing the sync, which is tried again later.
    /// </summary>
    private async Task<string?> PushedCoverAsync(NoctisServerClient client, string hex, Playlist local, string? agreedCover, CancellationToken ct)
    {
        var mine = PlaylistCovers.CoverOf(local.CoverArtPath);
        if (mine is null) return null;
        if (mine.Length == 0)
        {
            if (agreedCover is not { Length: > 0 }) return agreedCover;
            lock (_gate) return _pending.RemovedCovers.ContainsKey(hex) ? string.Empty : null;
        }
        if (mine == agreedCover) return mine;
        try
        {
            var sent = await client.UploadPlaylistCoverAsync(local.CoverArtPath!, ct).ConfigureAwait(false);
            return sent == mine ? mine : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   || ex is NoctisServerException { Kind: NoctisErrorKind.Server })
        {
            DebugLog.Write("Account", $"playlist cover not sent: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
            return null;
        }
    }

    /// <summary>Removes cover files no playlist points at any more (a replaced or removed cover, a
    /// playlist gone with the computer's), once they are <see cref="UnusedCoverAge"/> old.</summary>
    private void PrunePlaylistCovers(List<Playlist> phone)
    {
        try
        {
            var dir = PlaylistCoversDirectory;
            if (!Directory.Exists(dir)) return;
            var used = new HashSet<string>(phone.Where(p => !string.IsNullOrEmpty(p.CoverArtPath))
                .Select(p => Path.GetFullPath(p.CoverArtPath!)), StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.UtcNow - UnusedCoverAge;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (!used.Contains(Path.GetFullPath(f)) && File.GetLastWriteTimeUtc(f) < cutoff) NoctisServerClient.TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("Account", $"unused playlist covers not removed ({ex.GetType().Name})");
        }
    }
}
