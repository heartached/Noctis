using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Returns a unified track view across local library and enabled remote connectors.
/// </summary>
public sealed class UnifiedLibraryService : IUnifiedLibraryService
{
    private readonly ILibraryService _localLibrary;
    private readonly IPersistenceService _persistence;
    private readonly IReadOnlyList<IMediaSourceConnector> _connectors;
    private readonly IAuditTrailService _auditTrail;
    // Volatile reference swapped wholesale by RefreshRemoteSourcesAsync: readers
    // enumerate a snapshot, so a UI fetch never races an in-place Clear/insert
    // (Dictionary is unsafe for concurrent read + structural write), and remote
    // tracks don't transiently vanish for the duration of a network refresh.
    private volatile Dictionary<Guid, List<Track>> _remoteCache = new();

    public UnifiedLibraryService(
        ILibraryService localLibrary,
        IPersistenceService persistence,
        IEnumerable<IMediaSourceConnector> connectors,
        IAuditTrailService auditTrail)
    {
        _localLibrary = localLibrary;
        _persistence = persistence;
        _connectors = connectors.ToList();
        _auditTrail = auditTrail;
    }

    public Task<IReadOnlyList<Track>> GetUnifiedTracksAsync(CancellationToken ct = default)
    {
        var remote = _remoteCache;
        var all = new List<Track>(_localLibrary.Tracks.Count + remote.Values.Sum(v => v.Count));
        all.AddRange(_localLibrary.Tracks);
        foreach (var tracks in remote.Values)
            all.AddRange(tracks);

        // Stable deterministic ordering for unified views.
        IReadOnlyList<Track> ordered = all
            .OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(ordered);
    }

    public async Task RefreshRemoteSourcesAsync(CancellationToken ct = default)
    {
        var settings = await _persistence.LoadSettingsAsync();
        var enabled = settings.SourceConnections.Where(c => c.Enabled).ToList();

        var fresh = new Dictionary<Guid, List<Track>>();
        foreach (var connection in enabled)
        {
            ct.ThrowIfCancellationRequested();
            var connector = _connectors.FirstOrDefault(c => c.SourceType == connection.Type);
            if (connector == null) continue;

            var valid = await connector.ValidateConnectionAsync(connection, ct);
            if (!valid) continue;

            var tracks = (await connector.ScanAsync(connection, ct)).ToList();
            fresh[connection.Id] = tracks;

            await _auditTrail.AppendAsync(new AuditEvent
            {
                EventType = "source.refresh",
                EntityType = "connection",
                EntityId = connection.Id.ToString("N"),
                Reason = "Remote source refreshed",
                Details = new Dictionary<string, string>
                {
                    ["name"] = connection.Name,
                    ["type"] = connection.Type.ToString(),
                    ["trackCount"] = tracks.Count.ToString()
                }
            }, ct);
        }

        _remoteCache = fresh;
    }
}
