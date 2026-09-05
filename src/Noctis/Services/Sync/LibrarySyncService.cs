using Noctis.Models;

namespace Noctis.Services.Sync;

/// <summary>Receives the desktop's own user-state changes so they enter the sync ledger.</summary>
public interface ITrackStateRecorder
{
    void RecordTrackStates(IEnumerable<Track> tracks);
}

/// <summary>Applies remote state onto the live library (implemented by the server's library adapter).</summary>
public interface ISyncApplier
{
    Task ApplyTrackStateAsync(Guid trackId, TrackSyncState state);
    Task ApplyPlaylistStateAsync(Guid playlistId, PlaylistSyncState state);
}

public sealed record SyncChanges(long Seq, IReadOnlyList<SyncItem> Items);

/// <summary>
/// Cross-device sync of favorites, ratings, play counts and playlists, hosted by this
/// computer's Noctis server. State-based and last-writer-wins (see <see cref="SyncStore"/>);
/// the desktop is just another device writing into the same ledger, which is what lets a
/// phone and this PC converge without a cloud account.
/// </summary>
public interface ILibrarySyncService : ITrackStateRecorder
{
    bool IsEnabled { get; }
    string DeviceId { get; }
    string DeviceName { get; }
    long CurrentSeq { get; }

    /// <summary>Changes after <paramref name="since"/>, with playlists refreshed from disk first.</summary>
    Task<SyncChanges> GetChangesAsync(long since, string deviceId, string? deviceName, CancellationToken ct = default);

    /// <summary>Merges a device's items (LWW) and applies the winners to the library. Returns how many were applied.</summary>
    Task<int> PushAsync(string deviceId, string? deviceName, IReadOnlyList<SyncItem> items, ISyncApplier applier, CancellationToken ct = default);

    IReadOnlyList<SyncDevice> Devices();

    /// <summary>Puts every track that carries user state into the ledger (first enable / re-seed).</summary>
    Task SeedAsync(IEnumerable<Track> tracks, IReadOnlyList<Playlist> playlists);

    /// <summary>Raised on every push/pull so the Account &amp; Sync tab can refresh its status.</summary>
    event EventHandler? Changed;
}

public sealed class LibrarySyncService : ILibrarySyncService
{
    private readonly Func<AppSettings> _settings;
    private readonly IPersistenceService _persistence;
    private readonly object _storeGate = new();
    private SyncStore? _store;

    public event EventHandler? Changed;

    public LibrarySyncService(Func<AppSettings> settings, IPersistenceService persistence)
    {
        _settings = settings;
        _persistence = persistence;
    }

    private string StorePath => Path.Combine(_persistence.DataDirectory, "sync", "sync.db");

    private SyncStore Store
    {
        get
        {
            lock (_storeGate) return _store ??= new SyncStore(StorePath);
        }
    }

    public bool IsEnabled => Safe(() => _settings().SyncEnabled, false);

    public string DeviceId
    {
        get
        {
            var id = Safe(() => _settings().SyncDeviceId, string.Empty);
            return string.IsNullOrWhiteSpace(id) ? FallbackDeviceId : id.Trim();
        }
    }

    public string DeviceName
    {
        get
        {
            var name = Safe(() => _settings().SyncDeviceName, string.Empty);
            return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        }
    }

    private static string FallbackDeviceId => "desktop-" + Environment.MachineName.ToLowerInvariant();

    public long CurrentSeq => IsEnabled || File.Exists(StorePath) ? Store.CurrentSeq : 0;

    // ── Desktop → ledger ─────────────────────────────────────────────────────

    public void RecordTrackStates(IEnumerable<Track> tracks)
    {
        if (!IsEnabled) return;
        var now = DateTime.UtcNow;
        var device = DeviceId;
        var any = false;
        foreach (var t in tracks)
        {
            if (t is null || t.SourceType != SourceType.Local) continue;
            var payload = SyncJson.Serialize(TrackSyncState.From(t));
            var id = t.Id.ToString("N");
            // Echo guard: applying a phone's change re-saves the track here; identical
            // payloads must not become a new "change" that bounces back to the phone.
            var existing = Store.Get(SyncKinds.Track, id);
            if (existing is not null && existing.Payload == payload) continue;
            if (Store.Upsert(SyncKinds.Track, id, payload, now, device)) any = true;
        }
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SeedAsync(IEnumerable<Track> tracks, IReadOnlyList<Playlist> playlists)
    {
        var now = DateTime.UtcNow;
        var device = DeviceId;
        await Task.Run(() =>
        {
            foreach (var t in tracks)
            {
                if (t is null || t.SourceType != SourceType.Local) continue;
                if (!t.IsFavorite && t.Rating == 0 && !t.IsDisliked && t.PlayCount == 0) continue;
                var id = t.Id.ToString("N");
                if (Store.Get(SyncKinds.Track, id) is not null) continue;
                Store.Upsert(SyncKinds.Track, id, SyncJson.Serialize(TrackSyncState.From(t)), now, device);
            }
            RefreshPlaylists(playlists);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Playlists have no single write hook, but every editor bumps <see cref="Playlist.ModifiedAt"/>,
    /// so the ledger is brought up to date by comparing the persisted list against it on demand.
    /// Playlists that vanished get a tombstone.
    /// </summary>
    private void RefreshPlaylists(IReadOnlyList<Playlist> playlists)
    {
        var device = DeviceId;
        var present = new HashSet<string>();
        foreach (var p in playlists)
        {
            var id = p.Id.ToString("N");
            present.Add(id);
            var payload = SyncJson.Serialize(PlaylistSyncState.From(p));
            var existing = Store.Get(SyncKinds.Playlist, id);
            if (existing is not null && existing.Payload == payload) continue;
            var stamp = p.ModifiedAt.Kind == DateTimeKind.Utc ? p.ModifiedAt : p.ModifiedAt.ToUniversalTime();
            // A remote edit can carry a later ModifiedAt than this stale local copy: LWW keeps it.
            Store.Upsert(SyncKinds.Playlist, id, payload, stamp, device);
        }
        var now = DateTime.UtcNow;
        foreach (var item in Store.All(SyncKinds.Playlist))
        {
            if (present.Contains(item.Id)) continue;
            var state = SyncJson.Deserialize<PlaylistSyncState>(item.Payload);
            if (state is { Deleted: true }) continue;
            Store.Upsert(SyncKinds.Playlist, item.Id, SyncJson.Serialize(PlaylistSyncState.Tombstone(Guid.Empty, now)), now, device);
        }
    }

    // ── Devices ↔ ledger ─────────────────────────────────────────────────────

    public async Task<SyncChanges> GetChangesAsync(long since, string deviceId, string? deviceName, CancellationToken ct = default)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var items = await Task.Run(() =>
        {
            RefreshPlaylists(playlists);
            var changes = Store.ChangesSince(since);
            Store.TouchDevice(deviceId, deviceName, since);
            return changes;
        }, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return new SyncChanges(Store.CurrentSeq, items);
    }

    public async Task<int> PushAsync(string deviceId, string? deviceName, IReadOnlyList<SyncItem> items, ISyncApplier applier, CancellationToken ct = default)
    {
        var applied = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item is null || string.IsNullOrWhiteSpace(item.Id)) continue;
            var device = string.IsNullOrWhiteSpace(item.Device) ? deviceId : item.Device;
            bool won;
            lock (_storeGate) { won = Store.Upsert(item.Kind, item.Id, item.Payload, item.UpdatedUtc, device); }
            if (!won) continue;
            applied++;
            if (!Guid.TryParseExact(item.Id, "N", out var guid) && !Guid.TryParse(item.Id, out guid)) continue;
            try
            {
                switch (item.Kind)
                {
                    case SyncKinds.Track when SyncJson.Deserialize<TrackSyncState>(item.Payload) is { } state:
                        await applier.ApplyTrackStateAsync(guid, state).ConfigureAwait(false);
                        break;
                    case SyncKinds.Playlist when SyncJson.Deserialize<PlaylistSyncState>(item.Payload) is { } state:
                        await applier.ApplyPlaylistStateAsync(guid, state).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.State, "Sync.ApplyFailed", $"{item.Kind}/{item.Id}: {ex.Message}");
            }
        }
        Store.TouchDevice(deviceId, deviceName, Store.CurrentSeq);
        if (applied > 0) DebugLogger.Info(DebugLogger.Category.State, "Sync.Pushed", $"device={deviceId}, applied={applied}/{items.Count}");
        Changed?.Invoke(this, EventArgs.Empty);
        return applied;
    }

    public IReadOnlyList<SyncDevice> Devices() =>
        File.Exists(StorePath) ? Store.Devices() : Array.Empty<SyncDevice>();

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}
