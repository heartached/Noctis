using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Server-backed sync baseline using enabled Navidrome connections.
/// </summary>
public sealed class NavidromeSyncService : ISyncService
{
    private readonly IPersistenceService _persistence;
    private readonly IMediaSourceConnector _navidromeConnector;
    private readonly IAuditTrailService _auditTrail;

    public NavidromeSyncService(
        IPersistenceService persistence,
        IEnumerable<IMediaSourceConnector> connectors,
        IAuditTrailService auditTrail)
    {
        _persistence = persistence;
        _navidromeConnector = connectors.FirstOrDefault(c => c.SourceType == SourceType.Navidrome)!;
        _auditTrail = auditTrail;
    }

    public async Task<IReadOnlyList<SyncRevision>> PullAsync(CancellationToken ct = default)
    {
        var settings = await _persistence.LoadSettingsAsync();
        var revisions = new List<SyncRevision>();

        foreach (var conn in settings.SourceConnections.Where(c => c.Enabled && c.Type == SourceType.Navidrome))
        {
            ct.ThrowIfCancellationRequested();
            var ok = await _navidromeConnector.ValidateConnectionAsync(conn, ct);
            if (!ok) continue;

            revisions.Add(new SyncRevision
            {
                SourceType = SourceType.Navidrome,
                SourceKey = conn.Id.ToString("N"),
                RevisionToken = DateTime.UtcNow.ToString("O"),
                LastSyncedUtc = DateTime.UtcNow
            });
        }

        return revisions;
    }

    public Task PushPlayStateAsync(Track track, CancellationToken ct = default)
    {
        return _auditTrail.AppendAsync(new AuditEvent
        {
            EventType = "sync.playstate.push",
            EntityType = "track",
            EntityId = track.Id.ToString("N"),
            Reason = "Queued play-state sync",
            Details = new Dictionary<string, string>
            {
                ["playCount"] = track.PlayCount.ToString(),
                ["rating"] = track.Rating.ToString()
            }
        }, ct);
    }

    public Task PushPlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        return _auditTrail.AppendAsync(new AuditEvent
        {
            EventType = "sync.playlist.push",
            EntityType = "playlist",
            EntityId = playlist.Id.ToString("N"),
            Reason = "Queued playlist sync",
            Details = new Dictionary<string, string>
            {
                ["name"] = playlist.Name,
                ["trackCount"] = playlist.TrackIds.Count.ToString()
            }
        }, ct);
    }
}

