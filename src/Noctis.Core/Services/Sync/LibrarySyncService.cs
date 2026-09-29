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

/// <summary>
/// One page of ledger changes. <see cref="Seq"/> is where the next pull starts: the last
/// delivered item's sequence when <see cref="More"/> (the page was truncated), else the
/// ledger's top.
/// </summary>
public sealed record SyncChanges(long Seq, IReadOnlyList<SyncItem> Items, bool More = false);

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

    /// <summary>Most items one pull returns; the device pages with <see cref="SyncChanges.More"/>. Settable for tests.</summary>
    public int ChangesPageSize { get; init; } = 5000;

    public async Task<SyncChanges> GetChangesAsync(long since, string deviceId, string? deviceName, CancellationToken ct = default)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var changes = await Task.Run(() =>
        {
            RefreshPlaylists(playlists);
            var page = Store.ChangesPage(since, ChangesPageSize);
            // Truncated: resume after the last item handed out, not at the ledger's top (that
            // skipped everything past the page). The device's checkpoint is what it now holds.
            var seq = page.More ? page.Items[^1].Seq : page.CurrentSeq;
            Store.TouchDevice(deviceId, deviceName, seq);
            return new SyncChanges(seq, page.Items, page.More);
        }, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return changes;
    }

    // ── Push validation: a device's items are untrusted input ──

    /// <summary>Earliest stamp a device may send; older ones are raised to it.</summary>
    public static readonly DateTime OldestStamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How far past this computer's clock a device's stamp may be. A far-future stamp would win last-writer-wins forever.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    public const int MaxPlaylistTracks = 10_000, MaxPlaylistName = 200, MaxPlaylistDescription = 2000;

    /// <summary>A push may raise a track's play count by at most this much over what the ledger holds.</summary>
    public const int MaxPlayCountJump = 10_000;

    private static readonly System.Text.RegularExpressions.Regex ColorPattern =
        new(@"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>UTC (a stamp without a zone is UTC, never local), within [<see cref="OldestStamp"/>, now + <see cref="MaxClockSkew"/>].</summary>
    internal static DateTime ClampStamp(DateTime value, DateTime now)
    {
        value = ToUtc(value);
        var max = now + MaxClockSkew;
        return value < OldestStamp ? OldestStamp : value > max ? max : value;
    }

    /// <summary>As <see cref="ClampStamp"/> for optional dates, except one before 2000 means "unknown" (null).</summary>
    private static DateTime? ClampOptional(DateTime? value, DateTime now)
        => value is not { } v || ToUtc(v) < OldestStamp ? null : ClampStamp(v, now);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// The item as it may enter the ledger, or null to reject it: kind is track or playlist, the
    /// id a 32-hex Guid ("N", lower-cased), the payload a valid state (stored re-serialized, never
    /// raw), stamps clamped, rating 0–5, play count at most <see cref="MaxPlayCountJump"/> above
    /// the ledger's, playlists within their size limits and a #RRGGBB[AA] colour (or none).
    /// </summary>
    internal SyncItem? Normalize(SyncItem item, DateTime now)
    {
        if (item is null || !Guid.TryParseExact(item.Id, "N", out var guid)) return null;
        var id = guid.ToString("N");
        string payload;
        switch (item.Kind)
        {
            case SyncKinds.Track:
            {
                if (SyncJson.Deserialize<TrackSyncState>(item.Payload) is not { } s) return null;
                var known = Store.Get(SyncKinds.Track, id) is { } stored && SyncJson.Deserialize<TrackSyncState>(stored.Payload) is { } k ? k.PlayCount : 0;
                var maxPlays = Math.Min(int.MaxValue, Math.Max(0L, known) + MaxPlayCountJump);
                s = s with
                {
                    Rating = Math.Clamp(s.Rating, 0, 5),
                    PlayCount = (int)Math.Clamp(s.PlayCount, 0L, maxPlays),
                    LastPlayed = ClampOptional(s.LastPlayed, now),
                    FavoritedAt = ClampOptional(s.FavoritedAt, now),
                };
                payload = SyncJson.Serialize(s);
                break;
            }
            case SyncKinds.Playlist:
            {
                if (SyncJson.Deserialize<PlaylistSyncState>(item.Payload) is not { } s) return null;
                var trackIds = s.TrackIds ?? new List<Guid>();
                var name = s.Name ?? string.Empty;
                var description = s.Description ?? string.Empty;
                var color = s.Color ?? string.Empty;
                if (trackIds.Count > MaxPlaylistTracks || name.Length > MaxPlaylistName || description.Length > MaxPlaylistDescription) return null;
                if (color.Length > 0 && !ColorPattern.IsMatch(color)) return null;
                s = s with { Name = name, Description = description, Color = color, TrackIds = trackIds, ModifiedAt = ClampStamp(s.ModifiedAt, now) };
                payload = SyncJson.Serialize(s);
                break;
            }
            default:
                return null;
        }
        return item with { Id = id, Payload = payload, UpdatedUtc = ClampStamp(item.UpdatedUtc, now) };
    }

    public async Task<int> PushAsync(string deviceId, string? deviceName, IReadOnlyList<SyncItem> items, ISyncApplier applier, CancellationToken ct = default)
    {
        var applied = 0;
        var now = DateTime.UtcNow;
        foreach (var raw in items)
        {
            ct.ThrowIfCancellationRequested();
            if (Normalize(raw, now) is not { } item) continue;
            var device = string.IsNullOrWhiteSpace(item.Device) ? deviceId : item.Device;
            bool won;
            lock (_storeGate) { won = Store.Upsert(item.Kind, item.Id, item.Payload, item.UpdatedUtc, device); }
            if (!won) continue;
            applied++;
            var guid = Guid.ParseExact(item.Id, "N");
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
