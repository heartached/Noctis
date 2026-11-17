using System.Text.Json;
using Noctis.Mobile.Services.Account;
using Noctis.Models;

namespace Noctis.Services.Sync;

/// <summary>This computer's sign-in to another computer's Noctis Server, for <see cref="ComputerLinkService"/>.</summary>
internal sealed record ComputerLinkAccount
{
    /// <summary>"https://host:port" as <see cref="NoctisServerClient.NormalizeServerUrl"/> gives it.</summary>
    public required string ServerUrl { get; init; }
    /// <summary>The other computer's certificate, pinned at sign-in ("AB:CD:…").</summary>
    public required string Fingerprint { get; init; }
    public required string UserName { get; init; }
    /// <summary>The device id this computer signed in with there (random, per link).</summary>
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    /// <summary>The other computer's name as its sign-in reported it.</summary>
    public string ServerName { get; init; } = "";
    /// <summary>The device key, protected for this computer and user (never stored in clear).</summary>
    public required string ProtectedKey { get; init; }
    public DateTime? LastSyncUtc { get; init; }
    /// <summary>The other computer's song id → this computer's file of the same song (both "N" hex),
    /// from the last sync: a pair holds while both songs exist, so a tag edit never moves it.</summary>
    public Dictionary<string, string> Links { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Never prints the key.</summary>
    public override string ToString() => $"ComputerLinkAccount {{ ServerUrl = {ServerUrl}, UserName = {UserName} }}";
}

/// <summary>What one sync did: songs paired with the other computer, items taken from it, items sent to it.</summary>
internal sealed record ComputerLinkResult(int Paired, int Received, int Sent);

/// <summary>
/// Sync with another computer: this computer signs in to the other computer's Noctis Server the
/// way the phone app does (address, the account's name and password, the security code compared
/// by eye; then a device key), and the two sync ledgers (<see cref="ILibrarySyncService"/>) are
/// kept in step. What travels is what the ledger holds: favourites, ratings, dislikes, play counts,
/// last played, snoozes, and playlists. Music files, lyrics, artwork, playlist covers and settings
/// stay on each computer.
///
/// Each computer has its own files and so its own song ids (a song's id comes from its path).
/// The other computer's catalog is paired with this computer's files by
/// <see cref="NoctisLocalMatcher"/> (title, artist, length or file size), and every song id is
/// translated across the pair; a song only one computer has keeps its id untranslated in
/// playlists, so neither side loses an entry it cannot show.
///
/// Rules, each one tested (ComputerLinkServiceTests):
/// <list type="bullet">
/// <item>Newest change wins, per song and per playlist, by the ledger's stamps. Equal stamps are
/// the same change and are never sent back.</item>
/// <item>A pair seen for the first time merges instead: favourite if either computer has it, the
/// other computer's rating unless it has none, the higher play count and the later last played;
/// both computers then hold the merge. Nothing is unfavourited or unrated by linking.</item>
/// <item>Play counts never go down (the higher count wins), so plays on both computers between two
/// syncs count once each side, not added together.</item>
/// <item>A playlist deleted on one computer is deleted on the other when the delete is newer than
/// the other's last edit of it; a delete of a playlist the other computer never had is not sent.
/// An empty or unreadable playlist list on this computer sends no deletes at all.</item>
/// <item>Every sync reads the other computer's whole ledger again, so a sync that fails half way
/// loses nothing and a song this computer gets later pairs up on the next sync.</item>
/// <item>Offline: changes wait in each computer's ledger until the next sync that reaches the other one.</item>
/// </list>
/// </summary>
internal sealed class ComputerLinkService
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly ILibrarySyncService _ledger;
    private readonly ISyncApplier _applier;
    private readonly NoctisHandlerFactory _handlers;
    private readonly SecretProtector _protector;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _gate = new();
    private ComputerLinkAccount? _account;
    private bool _loaded;

    /// <summary>Items one push carries at most (the server takes 2000) and roughly the bytes
    /// (it takes 4 MB; a playlist can list 10000 songs).</summary>
    internal const int PushChunkItems = 500;
    internal const long PushChunkBytes = 1024 * 1024;

    /// <summary>How often a signed-in link syncs on its own.</summary>
    public static readonly TimeSpan AutoSyncInterval = TimeSpan.FromMinutes(10);

    /// <summary>The first automatic sync after start waits this long (the library loads first).</summary>
    public static readonly TimeSpan FirstAutoSyncDelay = TimeSpan.FromMinutes(1);

    public ComputerLinkService(ILibraryService library, IPersistenceService persistence, ILibrarySyncService ledger,
        ISyncApplier applier, NoctisHandlerFactory handlers, SecretProtector? protector = null)
    {
        _library = library;
        _persistence = persistence;
        _ledger = ledger;
        _applier = applier;
        _handlers = handlers;
        _protector = protector ?? SecretProtector.Current;
        // Signed in: this computer's own changes go into its ledger (with phone sync off too),
        // which is what the next sync sends.
        if (ledger is LibrarySyncService concrete) concrete.AlsoRecord = () => IsLinked;
    }

    /// <summary>Per computer and Windows user (a data folder shared by two computers keeps two links).</summary>
    private string AccountPath => Path.Combine(_persistence.DataDirectory, "sync", $"computer-link-{_protector.ComputerId}.json");

    public ComputerLinkAccount? Account
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded) { _account = Load(); _loaded = true; }
                return _account;
            }
        }
    }

    public bool IsLinked => Account is not null;
    public bool IsSyncing { get; private set; }

    /// <summary>Raised (any thread) when the account, the syncing flag or the last sync changes.</summary>
    public event EventHandler? StateChanged;

    // ── Sign-in ──────────────────────────────────────────────────────────

    /// <summary>First contact: the address as it will be used, and the other computer's security
    /// code to compare with its Settings (Phone app › Connect a phone › Enter manually).</summary>
    public async Task<(string Url, string Fingerprint)> ProbeAsync(string address, CancellationToken ct = default)
    {
        var url = NoctisServerClient.NormalizeServerUrl(address);
        var fingerprint = await NoctisServerClient.ProbeFingerprintAsync(_handlers, url, ct).ConfigureAwait(false);
        return (url, fingerprint);
    }

    /// <summary>Signs in with the security code the user confirmed and keeps the device key.</summary>
    public async Task SignInAsync(string address, string userName, string password, string confirmedFingerprint, CancellationToken ct = default)
    {
        var url = NoctisServerClient.NormalizeServerUrl(address);
        var fingerprint = url.StartsWith("https://", StringComparison.Ordinal)
            ? NoctisServerClient.NormalizeFingerprint(confirmedFingerprint)
            : string.Empty;
        var deviceId = "computer-" + Guid.NewGuid().ToString("N")[..16];
        var deviceName = ClampName(_ledger.DeviceName);
        using var client = new NoctisServerClient(_handlers, url, fingerprint, deviceKey: null);
        var result = await client.SignInAsync(userName.Trim(), password, deviceId, deviceName, ct).ConfigureAwait(false);
        var account = new ComputerLinkAccount
        {
            ServerUrl = url,
            Fingerprint = fingerprint,
            UserName = result.User ?? userName.Trim(),
            DeviceId = deviceId,
            DeviceName = deviceName,
            ServerName = result.Server ?? string.Empty,
            ProtectedKey = _protector.Protect(result.ApiKey),
        };
        lock (_gate)
        {
            Save(account);
            _account = account;
            _loaded = true;
        }
        DebugLog.Write("ComputerLink", $"signed in to {account.ServerName} as {account.UserName}");
        RaiseStateChanged();
    }

    /// <summary>Takes back this computer's key there (best effort) and forgets the link. What was
    /// synced stays on both computers.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var account = Account;
        if (account is null) return;
        try
        {
            using var client = Client(account);
            await client.SignOutAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NoctisServerException or OperationCanceledException or IOException)
        {
            DebugLog.Write("ComputerLink", $"sign-out not confirmed by the other computer: {ex.Message}");
        }
        lock (_gate)
        {
            try { File.Delete(AccountPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DebugLog.Write("ComputerLink", $"could not remove the link file: {ex.Message}"); }
            _account = null;
            _loaded = true;
        }
        RaiseStateChanged();
    }

    // ── Sync ─────────────────────────────────────────────────────────────

    /// <summary>One full sync (see the class rules). One runs at a time; a second call waits for it.</summary>
    public async Task<ComputerLinkResult> SyncNowAsync(CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = Account ?? throw new InvalidOperationException("Not signed in to another computer.");
            IsSyncing = true;
            RaiseStateChanged();
            using var client = Client(account);
            ComputerLinkResult result;
            try
            {
                result = await SyncCoreAsync(client, account, ct).ConfigureAwait(false);
            }
            catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.SignedOut)
            {
                // The other computer removed this one (or its password changed): the key is dead.
                DebugLog.Write("ComputerLink", "the other computer refused the key; signed out");
                lock (_gate)
                {
                    try { File.Delete(AccountPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    _account = null;
                }
                throw;
            }
            DebugLog.Write("ComputerLink", $"synced: {result.Paired} songs paired, {result.Received} received, {result.Sent} sent");
            return result;
        }
        finally
        {
            IsSyncing = false;
            _syncGate.Release();
            RaiseStateChanged();
        }
    }

    private async Task<ComputerLinkResult> SyncCoreAsync(NoctisServerClient client, ComputerLinkAccount account, CancellationToken ct)
    {
        // 1. Pair the other computer's songs with this computer's files. A library not loaded yet
        // (the app just started) or empty pairs nothing and changes nothing: syncing then would
        // drop every pair and merge them again as new on the next sync.
        var tracks = _library.Tracks.ToList();
        if (!tracks.Any(t => t.SourceType == SourceType.Local))
        {
            DebugLog.Write("ComputerLink", "no songs in this library (yet): sync skipped");
            return new ComputerLinkResult(0, 0, 0);
        }
        var songs = await client.GetAllSongsAsync(null, ct).ConfigureAwait(false);
        var sticky = new Dictionary<Guid, Guid>();
        foreach (var (r, l) in account.Links)
            if (NoctisRemoteIds.TryParseSyncId(r, out var rid) && NoctisRemoteIds.TryParseSyncId(l, out var lid)) sticky[rid] = lid;
        var toLocal = NoctisLocalMatcher.Match(songs, tracks, sticky);
        // A song the catalog left out (the other computer is scanning) keeps its pair while this
        // computer still has the file: a partial list proves nothing about it.
        var listed = new HashSet<Guid>(songs.Select(s => s.Id));
        var claimed = new HashSet<Guid>(toLocal.Values);
        foreach (var (r, l) in sticky)
            if (!listed.Contains(r) && _library.GetTrackById(l) is { SourceType: SourceType.Local } && claimed.Add(l)) toLocal[r] = l;
        var toRemote = toLocal.ToDictionary(kv => kv.Value, kv => kv.Key);
        var fresh = toLocal.Where(kv => !sticky.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToHashSet();

        // 2. The other computer's whole ledger.
        var remote = await PullAllAsync(client, account, ct).ConfigureAwait(false);

        // 3. Into this computer's ledger and library, newest change wins.
        var now = DateTime.UtcNow;
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var localCovers = playlists.Where(p => p is not null)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => PlaylistCovers.CoverOf(g.First().CoverArtPath));
        var songById = songs.ToDictionary(s => s.Id);
        var incoming = new List<SyncItem>();
        foreach (var item in remote.Values)
        {
            if (!NoctisRemoteIds.TryParseSyncId(item.Id, out var id)) continue;
            switch (item.Kind)
            {
                case SyncKinds.Track when toLocal.TryGetValue(id, out var lid) && !fresh.Contains(id):
                    incoming.Add(new SyncItem(SyncKinds.Track, lid.ToString("N"), item.Payload.GetRawText(), item.UpdatedUtc, item.Device, 0));
                    break;
                case SyncKinds.Playlist when SyncJson.Deserialize<PlaylistSyncState>(item.Payload.GetRawText()) is { } state:
                    // Covers stay on each computer: this computer's cover (or none) is what the
                    // item says, so taking the other computer's edit never removes or changes it.
                    state = state with
                    {
                        TrackIds = Translate(state.TrackIds, toLocal),
                        Cover = state.Deleted ? null : localCovers.TryGetValue(id, out var cover) ? cover : null,
                    };
                    incoming.Add(new SyncItem(SyncKinds.Playlist, id.ToString("N"), SyncJson.Serialize(state), item.UpdatedUtc, item.Device, 0));
                    break;
            }
        }
        foreach (var rid in fresh)
        {
            var lid = toLocal[rid];
            if (_library.GetTrackById(lid) is not { } local) continue;
            remote.TryGetValue(Key(SyncKinds.Track, rid), out var theirs);
            var their = theirs is not null
                ? SyncJson.Deserialize<TrackSyncState>(theirs.Payload.GetRawText())
                : songById.TryGetValue(rid, out var song)
                    ? new TrackSyncState(song.Starred, song.UserRating, false, song.PlayCount, null, null)
                    : null;
            var mine = TrackSyncState.From(local);
            var merged = Merge(their, mine);
            if (theirs is not null && SyncJson.Serialize(merged) == SyncJson.Serialize(their))
                // Nothing to add from here: their change as it is, with their stamp (not sent back).
                incoming.Add(new SyncItem(SyncKinds.Track, lid.ToString("N"), theirs.Payload.GetRawText(), theirs.UpdatedUtc, theirs.Device, 0));
            else if (HasState(merged, now))
                // The merge, newer than either side: applied here, then sent there.
                incoming.Add(new SyncItem(SyncKinds.Track, lid.ToString("N"), SyncJson.Serialize(merged), now, _ledger.DeviceId, 0));
        }
        var received = incoming.Count == 0 ? 0 : await _ledger.MergeAsync(incoming, _applier, ct).ConfigureAwait(false);

        // 4. This computer's changes the other computer does not have yet.
        var mineItems = await _ledger.LocalItemsAsync(ct).ConfigureAwait(false);
        var playlistsReadable = playlists.Count > 0;
        var outgoing = new List<PushItem>();
        foreach (var item in mineItems)
        {
            if (!NoctisRemoteIds.TryParseSyncId(item.Id, out var id)) continue;
            switch (item.Kind)
            {
                case SyncKinds.Track when toRemote.TryGetValue(id, out var rid):
                {
                    remote.TryGetValue(Key(SyncKinds.Track, rid), out var theirs);
                    if (theirs is not null && item.UpdatedUtc <= theirs.UpdatedUtc) continue;
                    if (SyncJson.Deserialize<TrackSyncState>(item.Payload) is not { } state) continue;
                    outgoing.Add(new PushItem(SyncKinds.Track, rid.ToString("N"), state, item.UpdatedUtc));
                    break;
                }
                case SyncKinds.Playlist:
                {
                    remote.TryGetValue(Key(SyncKinds.Playlist, id), out var theirs);
                    if (theirs is not null && item.UpdatedUtc <= theirs.UpdatedUtc) continue;
                    if (SyncJson.Deserialize<PlaylistSyncState>(item.Payload) is not { } state) continue;
                    if (state.Deleted)
                    {
                        // A delete of a playlist the other computer never had, or with this
                        // computer's playlist list empty (unreadable, or a fresh install), stays here.
                        if (theirs is null || !playlistsReadable) continue;
                        if (SyncJson.Deserialize<PlaylistSyncState>(theirs.Payload.GetRawText()) is { Deleted: true }) continue;
                    }
                    var theirCover = theirs is null ? null : SyncJson.Deserialize<PlaylistSyncState>(theirs.Payload.GetRawText())?.Cover;
                    state = state with
                    {
                        TrackIds = Translate(state.TrackIds, toRemote),
                        // The other computer keeps its own cover (none said = keep).
                        Cover = state.Deleted ? null : theirCover,
                    };
                    outgoing.Add(new PushItem(SyncKinds.Playlist, id.ToString("N"), state, item.UpdatedUtc));
                    break;
                }
            }
        }
        var sent = 0;
        foreach (var chunk in Chunks(outgoing))
        {
            await client.PushSyncChangesAsync(account.DeviceId, account.DeviceName, chunk, ct).ConfigureAwait(false);
            sent += chunk.Count;
        }

        // 5. Done: the pairs and the time, only now (a failed sync pairs again next time).
        lock (_gate)
        {
            if (_account is null || _account.DeviceId != account.DeviceId) return new ComputerLinkResult(toLocal.Count, received, sent);
            var updated = _account with
            {
                LastSyncUtc = DateTime.UtcNow,
                Links = toLocal.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value.ToString("N"), StringComparer.OrdinalIgnoreCase),
            };
            Save(updated);
            _account = updated;
        }
        return new ComputerLinkResult(toLocal.Count, received, sent);
    }

    /// <summary>The other computer's ledger from the start, the latest item per (kind, id).</summary>
    private static async Task<Dictionary<string, PulledItem>> PullAllAsync(NoctisServerClient client, ComputerLinkAccount account, CancellationToken ct)
    {
        var items = new Dictionary<string, PulledItem>(StringComparer.Ordinal);
        long since = 0;
        for (var round = 0; round < 1000; round++)
        {
            var page = await client.GetSyncChangesAsync(since, account.DeviceId, account.DeviceName, ct).ConfigureAwait(false);
            var maxSeq = since;
            foreach (var item in page.Items)
            {
                maxSeq = Math.Max(maxSeq, item.Seq);
                if (item.Kind is SyncKinds.Track or SyncKinds.Playlist && NoctisRemoteIds.TryParseSyncId(item.Id, out var id))
                    items[Key(item.Kind, id)] = item; // pages come in seq order: the later is newer
            }
            var more = page.More || (page.Items.Count > 0 && maxSeq < page.Seq);
            if (!more || maxSeq <= since) break;
            since = maxSeq;
        }
        return items;
    }

    private static string Key(string kind, Guid id) => kind + ":" + id.ToString("N");

    /// <summary>Song ids across the pairs; an id without a pair stays as it is.</summary>
    private static List<Guid> Translate(IEnumerable<Guid>? ids, IReadOnlyDictionary<Guid, Guid> map) =>
        (ids ?? Enumerable.Empty<Guid>()).Where(g => g != Guid.Empty).Take(LibrarySyncService.MaxPlaylistTracks)
            .Select(g => map.TryGetValue(g, out var to) ? to : g).ToList();

    /// <summary>
    /// A song both computers have, paired for the first time: favourite if either has it (then not
    /// disliked), the other computer's rating unless it has none, dislike if either has it, the
    /// higher play count, the later last played and snooze, the earlier favourite date.
    /// </summary>
    internal static TrackSyncState Merge(TrackSyncState? theirs, TrackSyncState mine)
    {
        if (theirs is null) return mine;
        var favorite = theirs.Favorite || mine.Favorite;
        DateTime? favoritedAt = !favorite ? null
            : theirs.FavoritedAt is { } a && mine.FavoritedAt is { } b ? (a < b ? a : b)
            : theirs.FavoritedAt ?? mine.FavoritedAt;
        return new TrackSyncState(
            favorite,
            theirs.Rating > 0 ? theirs.Rating : mine.Rating,
            !favorite && (theirs.Disliked || mine.Disliked),
            Math.Max(theirs.PlayCount, mine.PlayCount),
            Later(theirs.LastPlayed, mine.LastPlayed),
            favoritedAt,
            Later(theirs.SnoozedUntil, mine.SnoozedUntil));
    }

    /// <summary>Anything worth a ledger item (a song nobody touched needs none).</summary>
    private static bool HasState(TrackSyncState s, DateTime now) =>
        s.Favorite || s.Rating > 0 || s.Disliked || s.PlayCount > 0 || s.LastPlayed is not null || s.SnoozedUntil > now;

    private static DateTime? Later(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static IEnumerable<List<PushItem>> Chunks(List<PushItem> items)
    {
        var chunk = new List<PushItem>();
        long bytes = 0;
        foreach (var item in items)
        {
            var size = System.Text.Encoding.UTF8.GetByteCount(SyncJson.Serialize(item.Payload)) + 200L;
            if (chunk.Count > 0 && (chunk.Count >= PushChunkItems || bytes + size > PushChunkBytes))
            {
                yield return chunk;
                chunk = new List<PushItem>();
                bytes = 0;
            }
            chunk.Add(item);
            bytes += size;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    // ── Automatic sync ───────────────────────────────────────────────────

    private CancellationTokenSource? _auto;

    /// <summary>Syncs after <see cref="FirstAutoSyncDelay"/>, then every <see cref="AutoSyncInterval"/>
    /// while signed in. A failed sync (the other computer off, another network) waits for the next turn.</summary>
    public void StartAutoSync()
    {
        if (_auto is not null) return;
        var cts = new CancellationTokenSource();
        _auto = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(FirstAutoSyncDelay, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            using var timer = new PeriodicTimer(AutoSyncInterval);
            do
            {
                if (!IsLinked) continue;
                try { await SyncNowAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception ex) { LastError = ex; RaiseStateChanged(); DebugLog.Write("ComputerLink", $"automatic sync failed: {ex.Message}"); continue; }
                LastError = null;
                RaiseStateChanged();
            }
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false));
        }, cts.Token);
    }

    /// <summary>Why the last automatic sync failed; null after one that worked.</summary>
    public Exception? LastError { get; private set; }

    // ── Storage ──────────────────────────────────────────────────────────

    private NoctisServerClient Client(ComputerLinkAccount account) =>
        new(_handlers, account.ServerUrl, account.Fingerprint, _protector.Unprotect(account.ProtectedKey));

    private ComputerLinkAccount? Load()
    {
        try
        {
            if (!File.Exists(AccountPath)) return null;
            var account = JsonSerializer.Deserialize<ComputerLinkAccount>(File.ReadAllText(AccountPath));
            // A key this computer cannot read (a copied file, another Windows user) is no sign-in.
            if (account is null || string.IsNullOrEmpty(_protector.Unprotect(account.ProtectedKey))) return null;
            return account;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DebugLog.Write("ComputerLink", $"link file unreadable: {ex.Message}");
            return null;
        }
    }

    private void Save(ComputerLinkAccount account)
    {
        var path = AccountPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(account));
        PersistenceService.TryRestrictToOwner(temp, isDirectory: false);
        File.Move(temp, path, overwrite: true);
    }

    private static string ClampName(string? name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) clean = Environment.MachineName;
        return clean.Length > 64 ? clean[..64] : clean;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
