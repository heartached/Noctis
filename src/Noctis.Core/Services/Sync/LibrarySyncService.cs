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

    /// <summary>Takes a device off <see cref="Devices"/> (one signed out for good). What it synced stays.</summary>
    void ForgetDevice(string deviceId);

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
            var id = t.Id.ToString("N");
            var existing = Store.Get(SyncKinds.Track, id);
            var payload = Payload(t, existing?.Payload);
            // Echo guard: applying a phone's change re-saves the track here; identical
            // payloads must not become a new "change" that bounces back to the phone.
            if (existing is not null && existing.Payload == payload) continue;
            if (Store.Upsert(SyncKinds.Track, id, payload, now, device)) any = true;
        }
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The track's ledger payload; an Unsnooze since the stored one says so
    /// (<see cref="TrackSyncState.From(Track, TrackSyncState?)"/>).</summary>
    private static string Payload(Track t, string? stored) =>
        SyncJson.Serialize(TrackSyncState.From(t, stored is null ? null : SyncJson.Deserialize<TrackSyncState>(stored)));

    public async Task SeedAsync(IEnumerable<Track> tracks, IReadOnlyList<Playlist> playlists)
    {
        var now = DateTime.UtcNow;
        var device = DeviceId;
        await Task.Run(() =>
        {
            var known = Store.All(SyncKinds.Track).ToDictionary(i => i.Id, i => i.Payload, StringComparer.Ordinal);
            foreach (var t in tracks)
            {
                if (t is null || t.SourceType != SourceType.Local) continue;
                var id = t.Id.ToString("N");
                var payload = Payload(t, known.GetValueOrDefault(id));
                // A song the ledger has is brought up to date: sync was off while it changed
                // (unfavourited, re-rated), so nothing recorded that. A new one only with state.
                if (known.TryGetValue(id, out var stored)
                        ? stored == payload
                        : !t.IsFavorite && t.Rating == 0 && !t.IsDisliked && t.PlayCount == 0 && !t.IsSnoozed) continue;
                Store.Upsert(SyncKinds.Track, id, payload, now, device);
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
            var state = PlaylistSyncState.From(p);
            var existing = Store.Get(SyncKinds.Playlist, id);
            var stored = existing is null ? null : SyncJson.Deserialize<PlaylistSyncState>(existing.Payload);
            // A cover file unreadable right now says nothing: the ledger keeps what it has.
            if (state.Cover is null && stored is not null) state = state with { Cover = stored.Cover };
            var payload = SyncJson.Serialize(state);
            if (existing is not null && existing.Payload == payload) continue;
            var stamp = p.ModifiedAt.Kind == DateTimeKind.Utc ? p.ModifiedAt : p.ModifiedAt.ToUniversalTime();
            // Only the cover differs: a row from before covers synced, or a cover this computer
            // does not show (a phone's whose picture never arrived). On a row from before covers,
            // "no cover" is no news; any other cover is, and must get past LWW although the
            // playlist itself did not change (an equal stamp loses).
            if (existing is not null && stored is { Deleted: false }
                && SyncJson.Serialize(state with { Cover = stored.Cover }) == existing.Payload)
            {
                if (stored.Cover is null && state.Cover is not { Length: > 0 }) continue;
                if (stamp <= existing.UpdatedUtc) stamp = existing.UpdatedUtc.AddMilliseconds(1);
            }
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

    /// <summary>Payload bytes after which a pull page stops early (see <see cref="SyncStore.ChangesPage"/>). Settable for tests.</summary>
    public long ChangesPageBytes { get; init; } = SyncStore.DefaultPageBytes;

    public async Task<SyncChanges> GetChangesAsync(long since, string deviceId, string? deviceName, CancellationToken ct = default)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var changes = await Task.Run(() =>
        {
            RefreshPlaylists(playlists);
            var page = Store.ChangesPage(since, ChangesPageSize, ChangesPageBytes);
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

    /// <summary>How far past this computer's clock a device's stamp may be (the store's rule: a stamp past it never holds an item).</summary>
    public static readonly TimeSpan MaxClockSkew = SyncStore.MaxClockSkew;

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

    /// <summary>A snooze end in UTC, at most <see cref="MaxSnooze"/> ahead; any past date is
    /// <see cref="TrackSyncState.NotSnoozed"/> (one value for "not snoozed", so the echo guard
    /// sees the same payload whichever past date a device sent). Null stays null ("says nothing").</summary>
    internal static DateTime? ClampSnooze(DateTime? value, DateTime now)
    {
        if (value is not { } v) return null;
        v = ToUtc(v);
        if (v <= now) return TrackSyncState.NotSnoozed;
        var max = now + MaxSnooze;
        return v > max ? max : v;
    }

    /// <summary>The longest snooze a push may set (the apps snooze for 30 days).</summary>
    public static readonly TimeSpan MaxSnooze = TimeSpan.FromDays(366);

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
    /// the ledger's, playlists within their size limits, a #RRGGBB[AA] colour (or none) and a
    /// cover that is none or a hash (or absent).
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
                    SnoozedUntil = ClampSnooze(s.SnoozedUntil, now),
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
                // A cover is "" (none) or a hash; anything else says nothing (kept as it is).
                var cover = s.Cover is { Length: 0 } || PlaylistCovers.IsHash(s.Cover) ? s.Cover : null;
                s = s with { Name = name, Description = description, Color = color, TrackIds = trackIds, ModifiedAt = ClampStamp(s.ModifiedAt, now), Cover = cover };
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

    public void ForgetDevice(string deviceId)
    {
        if (!File.Exists(StorePath) || !Store.RemoveDevice(deviceId)) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}
